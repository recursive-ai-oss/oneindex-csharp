using Microsoft.EntityFrameworkCore;
using OneIndex.EntityFrameworkCore;

namespace OneIndex.Samples.Ef;

/// <summary>An EF entity and a OneIndex document at once.</summary>
public class Invoice
{
    [SearchId] public string Id { get; set; } = "";
    [FullText(Boost = 2)] public string Title { get; set; } = "";
    [Filterable, Sortable] public int Amount { get; set; }
    public int CustomerId { get; set; }                  // not indexed: only the database can filter on it
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.UseOneIndexOutbox();   // the log tables
}

public static class EfSample
{
    /// <summary>Runs the Entity Framework sample of the design document over a SQLite file; returns the ids found.</summary>
    public static async Task<string[]> RunAsync(string databaseFile, string indexDirectory)
    {
        var options = new DbContextOptionsBuilder<AppDb>()
            .UseSqlite($"Data Source={databaseFile};Pooling=False")      // any provider: UseSqlServer(cs), UseNpgsql(cs), ...
            .UseOneIndex(indexDirectory, o => o.Index<Invoice>())          // the entities to keep in the index
            .Options;

        await using var db = new AppDb(options);
        await db.Database.EnsureCreatedAsync();
        db.Invoices.Add(new Invoice { Id = "a", Title = "Overdue notice for Acme", Amount = 250, CustomerId = 7 });
        db.Invoices.Add(new Invoice { Id = "b", Title = "Overdue notice for Acme", Amount = 250, CustomerId = 8 });
        db.Invoices.Add(new Invoice { Id = "c", Title = "Lunch", Amount = 12, CustomerId = 7 });
        await db.SaveChangesAsync();                                       // the outbox rows are written in the same transaction

        var list = await db.Invoices.Search("overdue acme")                // the index finds, the database filters and loads
                           .Where(x => x.CustomerId == 7 && x.Amount > 100)
                           .AfterPendingChanges()                          // read your own writes: apply the log first
                           .Take(10)
                           .ToListAsync();
        await OneIndexRuntime.For(db).DisposeAsync();                      // an app does this at shutdown: it closes the index files
        return [.. list.Select(i => i.Id)];
    }
}
