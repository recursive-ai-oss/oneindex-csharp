using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OneIndex.EntityFrameworkCore;

namespace OneIndex.EfDurability;

/// <summary>The entity of the Entity Framework tests: both an EF entity and a OneIndex document.</summary>
public class Invoice
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText(Boost = 2)]
    public string Title { get; set; } = "";

    [Keyword, Facet]
    public string Status { get; set; } = "";

    [Filterable, Sortable]
    public int Amount { get; set; }

    /// <summary>Incremented by the application on every update: an index that applied a row twice or skipped one disagrees with the database.</summary>
    [Filterable]
    public int Version { get; set; }

    /// <summary>Not indexed: a predicate on it can only run in the database.</summary>
    public int CustomerId { get; set; }

    [Dynamic]
    public JsonElement Meta { get; set; }

    /// <summary>An owned type stored in a JSON column; its changes reach the index through the owner.</summary>
    [Dynamic]
    public Billing Billing { get; set; } = new();
}

public class Billing
{
    public string Contact { get; set; } = "";

    public string City { get; set; } = "";

    public int Terms { get; set; }

    public List<string> Notes { get; set; } = [];
}

/// <summary>The application's context.</summary>
public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseOneIndexOutbox();
        modelBuilder.Entity<Invoice>(b =>
        {
            b.HasKey(i => i.Id);
            b.OwnsOne(i => i.Billing, o => o.ToJson());
            b.Property(i => i.Meta).HasConversion(
                v => v.ValueKind == JsonValueKind.Undefined ? "{}" : v.GetRawText(),
                s => JsonDocument.Parse(s, default).RootElement.Clone());
        });
    }

    /// <summary>Builds a context over a SQLite file with indexes under <paramref name="indexRoot"/>.</summary>
    public static AppDb Create(string databaseFile, string? indexRoot, Action<OneIndexOptions>? configure = null)
    {
        // A value starting with Host= is a PostgreSQL connection string; anything else is the path of a SQLite file.
        var builder = databaseFile.StartsWith("Host=", StringComparison.Ordinal)
            ? new DbContextOptionsBuilder<AppDb>().UseNpgsql(databaseFile + ";Maximum Pool Size=30")
            : new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={databaseFile};Default Timeout=60;Pooling=False");
        builder
            // The campaign opens a fresh runtime (and so a fresh interceptor) for every check; a real app has one.
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
        if (indexRoot is not null)
        {
            builder.UseOneIndex(indexRoot, o =>
            {
                o.Index<Invoice>();
                configure?.Invoke(o);
            });
        }

        return new AppDb(builder.Options);
    }

    /// <summary>Creates the tables and switches the file to write-ahead logging so that readers and the two writers coexist.</summary>
    public void Initialize()
    {
        if (Database.IsNpgsql())
        {
            Database.EnsureDeleted();
            Database.EnsureCreated();
            return;
        }

        Database.EnsureCreated();
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }
}
