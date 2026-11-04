using Microsoft.EntityFrameworkCore;
using OneIndex.EfDurability;

namespace OneIndex.EntityFrameworkCore.Tests;

/// <summary>
/// Inline apply as the documentation's sample configures it: the context is built from its options and nothing has asked for the runtime before
/// the first SaveChanges. (The other tests call <c>OneIndexRuntime.For</c> when they open a context, which hid the case.)
/// </summary>
public class InlineApplyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneindex-inline-" + Guid.NewGuid().ToString("N"));

    public InlineApplyTests()
    {
        Directory.CreateDirectory(_dir);
        using AppDb init = AppDb.Create(Path.Combine(_dir, "app.db"), null);
        init.Initialize();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // Left for the temp cleaner.
        }
    }

    [Fact]
    public async Task TheDocumentedSampleIndexesTheFirstSaveChanges()
    {
        // Copied from docs/entity-framework.md; only the paths differ.
        var options = new DbContextOptionsBuilder<AppDb>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "app.db")}")
            .UseOneIndex(Path.Combine(_dir, "search"), o => { o.Index<Invoice>(); o.ApplyInline = true; })
            .Options;

        using var db = new AppDb(options);
        var inv = new Invoice { Id = "inv-1", Title = "overdue acme invoice", Status = "open", Amount = 150, CustomerId = 7 };
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        try
        {
            Assert.Null(rt.LastApplyError);
            var list = await db.Invoices.Search("overdue acme").Where(x => x.CustomerId == 7 && x.Amount > 100).Take(10).ToListAsync();
            Assert.Equal(["inv-1"], list.Select(i => i.Id));

            // The synchronous twin of the first save, on a fresh runtime, behaves the same.
            db.Invoices.Add(new Invoice { Id = "inv-2", Title = "second acme invoice", CustomerId = 7 });
            db.SaveChanges();
            Assert.Null(rt.LastApplyError);
            Assert.Equal(["inv-1", "inv-2"], rt.Index<Invoice>().CommittedIds().Order(StringComparer.Ordinal));
        }
        finally
        {
            rt.Dispose();
        }
    }

    [Fact]
    public void TheFirstSynchronousSaveChangesIsIndexedToo()
    {
        var options = new DbContextOptionsBuilder<AppDb>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "app.db")}")
            .UseOneIndex(Path.Combine(_dir, "search-sync"), o => { o.Index<Invoice>(); o.ApplyInline = true; })
            .Options;
        using var db = new AppDb(options);
        db.Invoices.Add(new Invoice { Id = "a", Title = "alpha" });
        db.SaveChanges();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        try
        {
            Assert.Null(rt.LastApplyError);
            Assert.Equal(["a"], rt.Index<Invoice>().CommittedIds());
        }
        finally
        {
            rt.Dispose();
        }
    }

    [Fact]
    public async Task AnApplyThatFailsOnceIsCaughtUpByTheNextApply()
    {
        // What a user of the earlier preview has: a save whose apply failed. The outbox still holds the row, so the next apply finds it.
        int calls = 0;
        string dbFile = Path.Combine(_dir, "app.db");
        string root = Path.Combine(_dir, "search-fail");
        AppDb Make() => AppDb.Create(dbFile, root, o =>
        {
            o.ApplyInline = true;
            o.ContextFactory = () => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("first apply fails") : AppDb.Create(dbFile, null);
        });

        using AppDb db = Make();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        try
        {
            db.Invoices.Add(new Invoice { Id = "a", Title = "alpha" });
            await db.SaveChangesAsync();
            Assert.NotNull(rt.LastApplyError);
            Assert.Empty(rt.Index<Invoice>().CommittedIds());

            db.Invoices.Add(new Invoice { Id = "b", Title = "beta" });
            await db.SaveChangesAsync();
            Assert.Equal(["a", "b"], rt.Index<Invoice>().CommittedIds().Order(StringComparer.Ordinal));
        }
        finally
        {
            rt.Dispose();
        }
    }

    [Fact]
    public async Task AContextTheRuntimeCannotRebuildIsRefusedBeforeAnythingIsSaved()
    {
        string dbFile = Path.Combine(_dir, "app.db");
        var options = new DbContextOptionsBuilder<NoCtorDb>()
            .UseSqlite($"Data Source={dbFile}")
            .UseOneIndex(Path.Combine(_dir, "search-noctor"), o => { o.Index<Invoice>(); o.ApplyInline = true; })
            .Options;
        using var db = new NoCtorDb(options, 1);
        db.Invoices.Add(new Invoice { Id = "a", Title = "alpha" });
        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("ContextFactory", e.Message);

        // Nothing was written: the database has no invoice and no log row.
        using AppDb check = AppDb.Create(dbFile, null);
        Assert.Empty(check.Invoices.ToList());
        Assert.Empty(check.Set<OutboxEntry>().ToList());
        OneIndexRuntime.For(db).Dispose();
    }
}

/// <summary>A context with no public constructor that takes only its options, so the runtime cannot construct one.</summary>
public sealed class NoCtorDb(DbContextOptions<NoCtorDb> options, int unused) : DbContext(options)
{
    public int Unused { get; } = unused;

    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseOneIndexOutbox();
        modelBuilder.Entity<Invoice>(b =>
        {
            b.HasKey(i => i.Id);
            b.OwnsOne(i => i.Billing, o => o.ToJson());
            b.Property(i => i.Meta).HasConversion(
                v => v.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : v.GetRawText(),
                s => System.Text.Json.JsonDocument.Parse(s, default).RootElement.Clone());
        });
    }
}
