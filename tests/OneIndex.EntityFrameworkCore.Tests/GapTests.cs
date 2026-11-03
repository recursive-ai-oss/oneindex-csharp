using Microsoft.EntityFrameworkCore;
using OneIndex.EfDurability;

namespace OneIndex.EntityFrameworkCore.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// Out-of-order visibility of log rows. SQLite serializes writers, so these tests play the part of a database where a transaction takes its position
/// when it inserts its outbox row but shows it only when it commits: the "transaction" is a change to the entity table plus a log row with an explicit
/// position, committed together, and the tests choose the order in which positions become visible.
/// </summary>
public class GapTests
{
    private static string Type => typeof(Invoice).FullName!;

    private static async Task CommitAsync(AppDb plain, long seq, string id, int version, bool delete = false)
    {
        Invoice? row = await plain.Invoices.FindAsync(id);
        if (delete)
        {
            if (row is not null)
            {
                plain.Invoices.Remove(row);
            }
        }
        else if (row is null)
        {
            plain.Invoices.Add(Env.Make(id, "title " + version, version: version));
        }
        else
        {
            row.Version = version;
            row.Title = "title " + version;
        }

        await plain.SaveChangesAsync();
        await plain.Database.ExecuteSqlRawAsync("INSERT INTO OneIndexOutbox (Seq, EntityType, EntityId, Operation) VALUES ({0}, {1}, {2}, 1)", seq, Type, id);
        plain.ChangeTracker.Clear();
    }

    private static Dictionary<string, int> IndexVersions(OneIndexRuntime rt)
    {
        SearchIndex<Invoice> index = rt.Index<Invoice>();
        return index.CommittedIds().ToDictionary(id => id, id => index.SearchBy(new Search.TermQuery("Id", id)).Take(1).Single().Document.Version, StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, int>> DbVersionsAsync(Env env)
    {
        using AppDb db = AppDb.Create(env.Db, null);
        return await db.Invoices.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.Version, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ARowThatBecomesVisibleBelowTheMarkIsStillApplied()
    {
        using var env = new Env();
        OneIndexRuntime rt = env.Runtime();
        using AppDb plain = AppDb.Create(env.Db, null);
        await rt.CatchUpAsync();

        await CommitAsync(plain, 2, "b", 1); // position 2 shows first; position 1 is still inside an open transaction
        await rt.CatchUpAsync();
        OneIndexPosition p = rt.GetPosition<Invoice>()!.Value;
        Assert.Equal(2, p.Seq);
        Assert.Equal(1, p.GapCount);
        Assert.Equal(["b"], rt.Index<Invoice>().CommittedIds());

        await CommitAsync(plain, 1, "a", 1);
        await rt.CatchUpAsync();
        p = rt.GetPosition<Invoice>()!.Value;
        Assert.Equal(0, p.GapCount);
        Assert.Equal(2, p.Seq);
        Assert.Equal(2, p.Count);
        Assert.Equal(await DbVersionsAsync(env), IndexVersions(rt));
    }

    [Fact]
    public async Task EveryVisibilityOrderOfOverlappingTransactionsGivesTheDatabaseState()
    {
        const int N = 160;
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
        {
            using var env = new Env();
            // This test is about the order rows become visible, not about age: a slow machine must not run into the 30 s default.
            OneIndexRuntime rt = env.Runtime(o =>
            {
                o.BatchSize = 7;
                o.LateRowAge = TimeSpan.FromHours(1);
            });
            using AppDb plain = AppDb.Create(env.Db, null);
            await rt.CatchUpAsync();
            var rng = new Random(seed);
            int[] order = [.. Enumerable.Range(1, N).OrderBy(_ => rng.Next())];
            string[] ids = [.. Enumerable.Range(0, 12).Select(i => "e" + i)];
            // Each position is a transaction that sets one entity (or deletes it); it becomes visible at the point the shuffled order says.
            string[] target = [.. Enumerable.Range(0, N + 1).Select(_ => ids[rng.Next(ids.Length)])];
            bool[] delete = [.. Enumerable.Range(0, N + 1).Select(_ => rng.Next(8) == 0)];
            int visible = 0;
            while (visible < N)
            {
                for (int k = rng.Next(1, 7); k > 0 && visible < N; k--)
                {
                    int seq = order[visible++];
                    await CommitAsync(plain, seq, target[seq], seq, delete[seq]);
                }

                await rt.CatchUpAsync();
            }

            await rt.CatchUpAsync();
            OneIndexPosition p = rt.GetPosition<Invoice>()!.Value;
            Assert.Equal(0, p.GapCount);
            Assert.Equal(N, p.Seq);
            Assert.Equal(N, p.Count);
            Assert.Equal(await DbVersionsAsync(env), IndexVersions(rt));
        }
    }

    [Fact]
    public async Task AMissingPositionIsGivenUpAfterTheConfiguredAgeAndNeverBefore()
    {
        using var env = new Env();
        var clock = new FakeTime();
        OneIndexRuntime rt = env.Runtime(o =>
        {
            o.TimeProvider = clock;
            o.LateRowAge = TimeSpan.FromSeconds(30);
        });
        using AppDb plain = AppDb.Create(env.Db, null);
        await rt.CatchUpAsync();
        await CommitAsync(plain, 2, "b", 1); // position 1 rolled back
        await rt.CatchUpAsync();
        clock.Advance(TimeSpan.FromSeconds(29));
        await rt.CatchUpAsync();
        Assert.Equal(1, rt.GetPosition<Invoice>()!.Value.GapCount);
        clock.Advance(TimeSpan.FromSeconds(2));
        await rt.CatchUpAsync();
        Assert.Equal(0, rt.GetPosition<Invoice>()!.Value.GapCount);
        await CommitAsync(plain, 3, "c", 1);
        await rt.CatchUpAsync();
        Assert.Equal(await DbVersionsAsync(env), IndexVersions(rt));
    }

    [Fact]
    public async Task AMissingPositionFarBehindTheNewestRowIsGivenUpAtOnce()
    {
        using var env = new Env();
        OneIndexRuntime rt = env.Runtime(o => o.LateRowWindow = 5);
        using AppDb plain = AppDb.Create(env.Db, null);
        await rt.CatchUpAsync();
        for (int seq = 2; seq <= 4; seq++)
        {
            await CommitAsync(plain, seq, "e" + seq, 1);
        }

        await rt.CatchUpAsync();
        Assert.Equal(1, rt.GetPosition<Invoice>()!.Value.GapCount);
        for (int seq = 5; seq <= 8; seq++)
        {
            await CommitAsync(plain, seq, "e" + seq, 1);
        }

        await rt.CatchUpAsync();
        Assert.Equal(0, rt.GetPosition<Invoice>()!.Value.GapCount);
        Assert.Equal(await DbVersionsAsync(env), IndexVersions(rt));
    }

    [Fact]
    public async Task GapsBeyondThePositionSizeStallTheMarkButNothingIsMissedAndNothingIsReleasedEarly()
    {
        using var env = new Env();
        var clock = new FakeTime();
        OneIndexRuntime rt = env.Runtime(o =>
        {
            o.TimeProvider = clock;
            o.LateRowAge = TimeSpan.FromSeconds(30);
        });
        using AppDb plain = AppDb.Create(env.Db, null);
        await rt.CatchUpAsync();
        // 49 visible rows two hundred positions apart: 9,751 positions that nobody has committed yet.
        for (int k = 1; k <= 49; k++)
        {
            await CommitAsync(plain, k * 200, "e" + k, k);
        }

        await rt.CatchUpAsync();
        SearchIndex<Invoice> index = rt.Index<Invoice>();
        Assert.True(index.AppliedPosition.Length <= 1024);
        Assert.Equal(49, index.CommittedIds().Count); // rows beyond the stalled mark are applied anyway
        OneIndexPosition p = rt.GetPosition<Invoice>()!.Value;
        Assert.True(p.Seq < 9800, "the mark must stop before the gaps that do not fit");

        // A row that appears at a recorded gap is applied (position 3 was recorded: the walk got past it).
        await CommitAsync(plain, 3, "late", 1);
        await rt.CatchUpAsync();
        Assert.Contains("late", index.CommittedIds());

        // Waiting out the age releases gaps in rounds until the mark reaches the newest row.
        int rounds = 0;
        while (rt.GetPosition<Invoice>()!.Value.Seq < 9800 && rounds++ < 60)
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            await rt.CatchUpAsync();
            Assert.True(index.AppliedPosition.Length <= 1024);
        }

        Assert.True(rounds < 60);
        Assert.Equal(await DbVersionsAsync(env), IndexVersions(rt));
    }

    [Fact]
    public async Task ARebuildKeepsThePositionsThatNoRowShowedWhenItReadTheLog()
    {
        using var env = new Env();
        using AppDb plain = AppDb.Create(env.Db, null);
        await CommitAsync(plain, 2, "b", 1);
        OneIndexRuntime rt = env.Runtime();
        await rt.CatchUpAsync(); // a new index is built from the tables
        OneIndexPosition p = rt.GetPosition<Invoice>()!.Value;
        Assert.Equal(1, p.GapCount);

        await CommitAsync(plain, 1, "a", 1); // the transaction that held position 1 commits after the tables were read
        await rt.CatchUpAsync();
        Assert.Equal(["a", "b"], rt.Index<Invoice>().CommittedIds().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AGapThatRetentionHasPassedForcesARebuild()
    {
        using var env = new Env();
        OneIndexRuntime rt = env.Runtime();
        using AppDb plain = AppDb.Create(env.Db, null);
        await rt.CatchUpAsync();
        await CommitAsync(plain, 2, "b", 1);
        await rt.CatchUpAsync();
        long firstBase = rt.GetPosition<Invoice>()!.Value.Base;
        await CommitAsync(plain, 3, "c", 1);
        OutboxStore.PruneTo(plain, 2); // retention passes the open position: its row, if it ever commits, is gone
        await rt.CatchUpAsync();
        OneIndexPosition p = rt.GetPosition<Invoice>()!.Value;
        Assert.True(p.Base > firstBase);
        Assert.Equal(await DbVersionsAsync(env), IndexVersions(rt));
    }

    [Fact]
    public async Task ThePruningMarkOnlyMovesForward()
    {
        using var env = new Env();
        using AppDb db = AppDb.Create(env.Db, null);
        OutboxStore.ReadState(db);
        for (int seq = 1; seq <= 10; seq++)
        {
            await db.Database.ExecuteSqlRawAsync("INSERT INTO OneIndexOutbox (Seq, EntityType, EntityId, Operation) VALUES ({0}, {1}, {2}, 1)", seq, Type, "x");
        }

        // The interleaving that once lowered the mark: a slow prune that decided on cutoff 3 runs after a prune to 8.
        OutboxStore.PruneTo(db, 8);
        OutboxStore.PruneTo(db, 3);
        Assert.Equal(8, OutboxStore.ReadState(db).PrunedThrough);
        Assert.Equal(2, await db.Set<OutboxEntry>().CountAsync());
    }

    [Fact]
    public async Task ChangesToAnOwnedJsonColumnReachTheIndexThroughTheOwner()
    {
        using var env = new Env();
        using AppDb db = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        Invoice inv = Env.Make("a", "alpha");
        inv.Billing = new Billing { Contact = "acme", City = "Paris", Terms = 30, Notes = ["x", "y"] };
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        await rt.CatchUpAsync();
        SearchIndex<Invoice> index = rt.Index<Invoice>();
        Assert.Single(index.Search().Where("Billing.City = Paris").Take(5).Execute());

        (await db.Invoices.FindAsync("a"))!.Billing.City = "Oslo"; // only the owned part changes
        await db.SaveChangesAsync();
        Assert.Equal(1, await rt.CatchUpAsync());
        Assert.Empty(index.Search().Where("Billing.City = Paris").Take(5).Execute());
        Invoice hit = index.Search().Where("Billing.City = Oslo").Take(5).Single().Document;
        Assert.Equal(30, hit.Billing.Terms);
        Assert.Equal(["x", "y"], hit.Billing.Notes);
    }
}
