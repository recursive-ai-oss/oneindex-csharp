using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OneIndex.EfDurability;

namespace OneIndex.EntityFrameworkCore.Tests;

/// <summary>A SQLite file and an index root in a temporary directory, with every runtime closed on dispose.</summary>
public sealed class Env : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneindex-ef-" + Guid.NewGuid().ToString("N"));
    private readonly List<AppDb> _open = [];
    private readonly List<OneIndexRuntime> _runtimes = [];

    public Env()
    {
        Directory.CreateDirectory(_dir);
        using AppDb init = AppDb.Create(Db, null);
        init.Initialize();
    }

    public string Db => Path.Combine(_dir, "app.db");

    public string Root => Path.Combine(_dir, "idx");

    public AppDb Open(Action<OneIndexOptions>? configure = null)
    {
        AppDb db = AppDb.Create(Db, Root, configure);
        _open.Add(db);
        _runtimes.Add(OneIndexRuntime.For(db));
        return db;
    }

    public OneIndexRuntime Runtime(Action<OneIndexOptions>? configure = null) => OneIndexRuntime.For(Open(configure));

    public static Invoice Make(string id, string title, string status = "open", int amount = 1, int customer = 0, int version = 1, int tier = 0)
        => new() { Id = id, Title = title, Status = status, Amount = amount, CustomerId = customer, Version = version, Meta = JsonDocument.Parse("{\"tier\":" + tier + "}").RootElement.Clone() };

    public void Dispose()
    {
        foreach (OneIndexRuntime r in _runtimes)
        {
            r.Dispose();
        }

        foreach (AppDb d in _open)
        {
            d.Dispose();
        }

        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // Left for the temp cleaner.
        }
    }
}

public class OutboxTests
{
    [Fact]
    public async Task SaveChangesWritesOneRowPerEntityInTheSameTransactionAndRollsBackWithIt()
    {
        using var env = new Env();
        using AppDb db = env.Open();
        db.Invoices.Add(Env.Make("a", "alpha"));
        db.Invoices.Add(Env.Make("b", "beta"));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<OutboxEntry>().CountAsync());

        // A save that fails writes neither the entity nor its outbox row.
        using AppDb db2 = env.Open();
        db2.Invoices.Add(Env.Make("c", "gamma"));
        db2.Invoices.Add(Env.Make("a", "duplicate key"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        using AppDb check = AppDb.Create(env.Db, null);
        Assert.Equal(2, await check.Set<OutboxEntry>().CountAsync());
        Assert.Equal(2, await check.Invoices.CountAsync());

        // The failed context can be fixed and saved again without duplicated rows.
        db2.ChangeTracker.Clear();
        db2.Invoices.Add(Env.Make("c", "gamma"));
        await db2.SaveChangesAsync();
        Assert.Equal(3, await check.Set<OutboxEntry>().CountAsync());
    }

    [Fact]
    public async Task CatchUpMirrorsInsertsUpdatesAndDeletes()
    {
        using var env = new Env();
        using AppDb db = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        db.Invoices.AddRange(Env.Make("a", "alpha"), Env.Make("b", "beta"), Env.Make("c", "gamma"));
        await db.SaveChangesAsync();
        Assert.Equal(0, await rt.CatchUpAsync()); // a new index is built from the tables, then the log rows up to its position are covered
        Assert.Equal(["a", "b", "c"], rt.Index<Invoice>().CommittedIds().Order(StringComparer.Ordinal));

        Invoice b = await db.Invoices.FindAsync("b") ?? throw new InvalidOperationException();
        b.Title = "beta two";
        b.Version = 2;
        db.Invoices.Remove(await db.Invoices.FindAsync("c") ?? throw new InvalidOperationException());
        db.Invoices.Add(Env.Make("d", "delta"));
        await db.SaveChangesAsync();
        Assert.Equal(3, await rt.CatchUpAsync());
        SearchIndex<Invoice> index = rt.Index<Invoice>();
        Assert.Equal(["a", "b", "d"], index.CommittedIds().Order(StringComparer.Ordinal));
        Assert.Equal(2, index.Search("beta two").Take(5).Single().Document.Version);
        OneIndexPosition p = rt.GetPosition<Invoice>()!.Value;
        Assert.Equal(3, p.Count);
        Assert.Equal(p.Seq, await db.Set<OutboxEntry>().MaxAsync(e => e.Seq));
    }

    [Fact]
    public async Task AppliedPositionIsCommittedWithTheDocuments()
    {
        using var env = new Env();
        using (AppDb db = env.Open())
        {
            db.Invoices.Add(Env.Make("a", "alpha"));
            await db.SaveChangesAsync();
            await OneIndexRuntime.For(db).CatchUpAsync();
            db.Invoices.Add(Env.Make("b", "beta"));
            await db.SaveChangesAsync();
            await OneIndexRuntime.For(db).CatchUpAsync();
            OneIndexRuntime.For(db).Dispose();
        }

        // Reopened from disk, the index still knows where it was and applies nothing twice.
        using AppDb again = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(again);
        OneIndexPosition before = rt.GetPosition<Invoice>()!.Value;
        Assert.Equal(0, await rt.CatchUpAsync());
        Assert.Equal(before, rt.GetPosition<Invoice>()!.Value);
        Assert.Equal(1, before.Count);
    }

    [Fact]
    public async Task InlineApplyMakesSaveChangesVisibleBeforeItReturns()
    {
        using var env = new Env();
        using AppDb db = env.Open(o => o.ApplyInline = true);
        db.Invoices.Add(Env.Make("a", "alpha"));
        db.SaveChanges();
        SearchIndex<Invoice> index = OneIndexRuntime.For(db).Index<Invoice>();
        Assert.Equal(["a"], index.CommittedIds());
        db.Invoices.Add(Env.Make("b", "beta"));
        await db.SaveChangesAsync();
        Assert.Equal(2, index.CommittedIds().Count);
        Assert.Null(OneIndexRuntime.For(db).LastApplyError);
    }

    [Fact]
    public async Task ReadYourWritesIsOptIn()
    {
        using var env = new Env();
        using AppDb db = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        await rt.CatchUpAsync();
        db.Invoices.Add(Env.Make("a", "alpha"));
        await db.SaveChangesAsync();

        Assert.Empty(await db.Invoices.Search("alpha").ToListAsync()); // not applied yet: the default does not wait
        Assert.Single(await db.Invoices.Search("alpha").AfterPendingChanges().ToListAsync());

        db.Invoices.Add(Env.Make("b", "bravo"));
        await db.SaveChangesAndIndexAsync();
        Assert.Single(await db.Invoices.Search("bravo").ToListAsync());
    }

    [Fact]
    public async Task OwnedAndSharedChangesAreNotMissed()
    {
        // An entity updated without touching an indexed property still produces a row: the log says "changed", the database says how.
        using var env = new Env();
        using AppDb db = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        db.Invoices.Add(Env.Make("a", "alpha", customer: 1));
        await db.SaveChangesAsync();
        await rt.CatchUpAsync();
        (await db.Invoices.FindAsync("a"))!.CustomerId = 9;
        await db.SaveChangesAsync();
        Assert.Equal(1, await rt.CatchUpAsync());
    }
}

public class ApplierTests
{
    private static async Task RunWorkloadAsync(Env env, int saves, int seed, bool inline, bool threaded)
    {
        using AppDb db = env.Open(o =>
        {
            o.ApplyInline = inline;
            o.BatchSize = 7;
            o.PollInterval = TimeSpan.FromMilliseconds(5);
            o.RetainRows = 0;
        });
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        using var cts = new CancellationTokenSource();
        Task? loop = threaded ? Task.Run(() => rt.RunAsync(cts.Token)) : null;
        var rng = new Random(seed);
        for (int i = 0; i < saves; i++)
        {
            using AppDb w = env.Open();
            await Mutations.SaveSomeAsync(w, rng, i % 2 == 0);
        }

        if (loop is not null)
        {
            // Let the loop drain, then stop it; a final catch-up covers whatever the loop had not reached.
            await cts.CancelAsync();
            await loop;
        }

        await rt.CatchUpAsync();
    }

    private static async Task<List<(string Id, string Title, string Status, int Amount, int Version)>> ContentsAsync(Env env)
    {
        using AppDb db = env.Open();
        SearchIndex<Invoice> index = OneIndexRuntime.For(db).Index<Invoice>();
        var list = new List<(string, string, string, int, int)>();
        foreach (string id in index.CommittedIds().Order(StringComparer.Ordinal))
        {
            Invoice d = index.SearchBy(new Search.TermQuery("Id", id)).Take(1).Single().Document;
            list.Add((d.Id, d.Title, d.Status, d.Amount, d.Version));
        }

        return list;
    }

    [Fact]
    public async Task CooperativeInlineAndThreadedAppliersGiveTheSameIndex()
    {
        using var cooperative = new Env();
        using var threaded = new Env();
        using var inline = new Env();
        await RunWorkloadAsync(cooperative, 150, 11, inline: false, threaded: false);
        await RunWorkloadAsync(threaded, 150, 11, inline: false, threaded: true);
        await RunWorkloadAsync(inline, 150, 11, inline: true, threaded: false);

        var a = await ContentsAsync(cooperative);
        Assert.NotEmpty(a);
        Assert.Equal(a, await ContentsAsync(threaded));
        Assert.Equal(a, await ContentsAsync(inline));

        using AppDb db = cooperative.Open();
        var rows = await db.Invoices.AsNoTracking().OrderBy(i => i.Id).Select(i => new { i.Id, i.Title, i.Status, i.Amount, i.Version }).ToListAsync();
        Assert.Equal(rows.Select(r => (r.Id, r.Title, r.Status, r.Amount, r.Version)), a);
    }

    [Fact]
    public async Task AnIndexBeyondRetentionRebuildsItself()
    {
        using var env = new Env();
        using AppDb db = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        db.Invoices.AddRange(Env.Make("a", "alpha"), Env.Make("b", "beta"));
        await db.SaveChangesAsync();
        await rt.CatchUpAsync();
        OneIndexPosition first = rt.GetPosition<Invoice>()!.Value;

        db.Invoices.Add(Env.Make("c", "gamma"));
        (await db.Invoices.FindAsync("a"))!.Title = "alpha two";
        db.Invoices.Remove(await db.Invoices.FindAsync("b") ?? throw new InvalidOperationException());
        await db.SaveChangesAsync();
        Assert.True(await rt.PruneAsync(0) > 0);

        Assert.Equal(0, await rt.CatchUpAsync()); // rebuilt, not replayed
        OneIndexPosition second = rt.GetPosition<Invoice>()!.Value;
        Assert.True(second.Base > first.Base);
        Assert.Equal(0, second.Count);
        SearchIndex<Invoice> index = rt.Index<Invoice>();
        Assert.Equal(["a", "c"], index.CommittedIds().Order(StringComparer.Ordinal));
        Assert.Single(index.Search("alpha two").Take(5));
    }

    [Fact]
    public async Task PrunedLogKeepsTheNewestRowsAndIndexesWithinItContinue()
    {
        using var env = new Env();
        using AppDb db = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        await rt.CatchUpAsync();
        for (int i = 0; i < 10; i++)
        {
            db.Invoices.Add(Env.Make("i" + i, "row " + i));
            await db.SaveChangesAsync();
        }

        await rt.CatchUpAsync();
        long before = rt.GetPosition<Invoice>()!.Value.Base;
        db.Invoices.Add(Env.Make("late", "late row"));
        await db.SaveChangesAsync();
        await rt.PruneAsync(5); // the new row is within the window, so no rebuild
        Assert.Equal(1, await rt.CatchUpAsync());
        Assert.Equal(before, rt.GetPosition<Invoice>()!.Value.Base);
        Assert.Equal(11, rt.Index<Invoice>().CommittedIds().Count);
    }

    [Fact]
    public async Task ReindexRepairsWhatTheInterceptorCannotSee()
    {
        using var env = new Env();
        using AppDb db = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        db.Invoices.AddRange(Env.Make("a", "alpha", customer: 1), Env.Make("b", "beta", customer: 2), Env.Make("c", "gamma", customer: 1));
        await db.SaveChangesAsync();
        await rt.CatchUpAsync();

        // Bulk statements bypass SaveChanges: the log has no row for them.
        await db.Invoices.Where(i => i.CustomerId == 1).ExecuteUpdateAsync(u => u.SetProperty(i => i.Title, "changed"));
        await db.Invoices.Where(i => i.Id == "b").ExecuteDeleteAsync();
        Assert.Equal(0, await rt.CatchUpAsync());
        SearchIndex<Invoice> index = rt.Index<Invoice>();
        Assert.Empty(index.Search("changed").Take(5));
        Assert.Equal(3, index.CommittedIds().Count);

        Assert.Equal(2, await rt.ReindexAsync(db.Invoices.Where(i => i.CustomerId == 1)));
        Assert.Equal(2, index.Search("changed").Take(5).Execute().Count);
        Assert.Equal(3, index.CommittedIds().Count); // the query repair does not delete

        await rt.ReindexAsync<Invoice>();
        Assert.Equal(["a", "c"], index.CommittedIds().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ContextFactoryFromTheOptionsIsUsedWhenGiven()
    {
        using var env = new Env();
        AppDb? made = null;
        using AppDb db = env.Open(o => o.ContextFactory = () => made = AppDb.Create(env.Db, env.Root));
        db.Invoices.Add(Env.Make("a", "alpha"));
        await db.SaveChangesAsync();
        await OneIndexRuntime.For(db).CatchUpAsync();
        Assert.NotNull(made);
        Assert.Single(await db.Invoices.Search("alpha").ToListAsync());
    }
}

public class SearchTests
{
    private static readonly string[] Words = ["acme", "invoice", "widget", "hosting", "support", "license"];

    private static async Task<Env> SeedAsync(int n, int seed)
    {
        var env = new Env();
        var rng = new Random(seed);
        using AppDb db = env.Open();
        for (int i = 0; i < n; i++)
        {
            db.Invoices.Add(Env.Make("inv-" + i.ToString("D4"), Words[rng.Next(Words.Length)] + " " + Words[rng.Next(Words.Length)],
                Mutations.Statuses[rng.Next(4)], rng.Next(1000), rng.Next(20), rng.Next(1, 5), rng.Next(3)));
        }

        await db.SaveChangesAsync();
        await OneIndexRuntime.For(db).CatchUpAsync();
        return env;
    }

    [Fact]
    public async Task PagesAreFullAndInSearchOrderWhenDatabaseOnlyPredicatesDiscardMostCandidates()
    {
        using Env env = await SeedAsync(1500, 5);
        using AppDb db = env.Open();
        // CustomerId is not indexed: only the database can test it, and only 1 candidate in 20 passes.
        List<Invoice> page = await db.Invoices.Search("acme").Where(i => i.CustomerId == 3).Take(10).ToListAsync();
        Assert.Equal(10, page.Count);
        Assert.All(page, i => Assert.Equal(3, i.CustomerId));
        Assert.Equal(page.Select(p => p.Id), page.Select(p => p.Id).Distinct());
    }

    [Fact]
    public async Task UntranslatableOrIndexedPredicatesAreSplitByConjunct()
    {
        using Env env = await SeedAsync(200, 6);
        using AppDb db = env.Open();
        EfSearchQuery<Invoice> q = db.Invoices.Search("widget")
            .Where(i => i.Status == "open" && i.CustomerId < 10 && i.Meta.Path("tier") >= 1);
        List<Invoice> rows = await q.Take(50).ToListAsync();
        Assert.All(rows, i =>
        {
            Assert.Equal("open", i.Status);
            Assert.True(i.CustomerId < 10);
            Assert.True(i.Meta.GetProperty("tier").GetInt32() >= 1);
        });
        Assert.Equal(rows.Select(r => r.Id), q.Take(50).ToList().Select(r => r.Id)); // the sync twin agrees
    }

    [Fact]
    public async Task DatabaseRowsWinOverTheIndexWhenTheyDiffer()
    {
        using Env env = await SeedAsync(20, 7);
        using AppDb db = env.Open();
        List<Invoice> all = await db.Invoices.Search().Take(100).ToListAsync();
        // A row deleted behind the index's back is not returned.
        await db.Invoices.Where(i => i.Id == all[0].Id).ExecuteDeleteAsync();
        Assert.DoesNotContain(await db.Invoices.Search().Take(100).ToListAsync(), i => i.Id == all[0].Id);
    }

    [Fact]
    public async Task SkipAndTakePageThroughTheFilteredResults()
    {
        using Env env = await SeedAsync(300, 8);
        using AppDb db = env.Open();
        List<Invoice> all = await db.Invoices.Search("hosting").Where(i => i.CustomerId % 2 == 0).Take(200).ToListAsync();
        var paged = new List<Invoice>();
        for (int skip = 0; skip < all.Count; skip += 7)
        {
            paged.AddRange(await db.Invoices.Search("hosting").Where(i => i.CustomerId % 2 == 0).Skip(skip).Take(7).ToListAsync());
        }

        Assert.Equal(all.Select(i => i.Id), paged.Select(i => i.Id));
    }

    // The differential: random queries mixing indexed and database-only predicates, against the index's own hit list filtered by LINQ-to-Objects over the rows.
    [Fact]
    public async Task RandomQueriesAgreeWithLinqToObjectsOverTheIndexHits()
    {
        using Env env = await SeedAsync(600, 9);
        using AppDb db = env.Open();
        SearchIndex<Invoice> index = OneIndexRuntime.For(db).Index<Invoice>();
        Dictionary<string, Invoice> rows = await db.Invoices.AsNoTracking().ToDictionaryAsync(i => i.Id);
        var rng = new Random(2024);
        int pages = 0, nonEmpty = 0, short_ = 0;
        for (int round = 0; round < 300; round++)
        {
            string? text = rng.Next(5) == 0 ? null : Words[rng.Next(Words.Length)] + (rng.Next(3) == 0 ? " " + Words[rng.Next(Words.Length)] : "");
            int c = rng.Next(20);
            int amount = rng.Next(1000);
            string status = Mutations.Statuses[rng.Next(4)];
            var predicates = new List<Expression<Func<Invoice, bool>>>();
            for (int k = rng.Next(0, 4); k > 0; k--)
            {
                predicates.Add(rng.Next(7) switch
                {
                    0 => i => i.Status == status,
                    1 => i => i.Amount > amount,
                    2 => i => i.CustomerId == c,
                    3 => i => i.CustomerId < c && i.Amount < amount + 300,
                    4 => i => i.Status != status || i.CustomerId == c,
                    5 => i => i.Version >= 2,
                    _ => i => i.CustomerId % 3 == c % 3,
                });
            }

            int take = rng.Next(1, 25);
            int skip = rng.Next(0, 3) * take;
            EfSearchQuery<Invoice> q = db.Invoices.Search(text);
            SearchQuery<Invoice> all = index.Search(text);
            foreach (Expression<Func<Invoice, bool>> p in predicates)
            {
                q = q.Where(p);
            }

            // Expected: every index hit for the text in order, kept when its current row passes every predicate.
            Func<Invoice, bool>[] compiled = [.. predicates.Select(p => p.Compile())];
            List<string> expected = [.. all.Take(10_000).Select(h => h.Id).Where(id => rows.TryGetValue(id, out Invoice? r) && compiled.All(f => f(r)))];
            // The index applies indexed predicates to its own copy, which equals the row here (nothing changed since the catch-up).
            List<string> actual = [.. (await q.Skip(skip).Take(take).ToListAsync()).Select(i => i.Id)];
            Assert.Equal(expected.Skip(skip).Take(take), actual);
            pages++;
            nonEmpty += actual.Count > 0 ? 1 : 0;
            short_ += actual.Count < take ? 1 : 0;
        }

        Assert.True(nonEmpty > 100, $"only {nonEmpty} of {pages} pages had results");
        Assert.True(short_ < pages, "every page was short");
    }
}
