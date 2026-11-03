using Microsoft.EntityFrameworkCore;

namespace OneIndex.EntityFrameworkCore;

/// <summary>What happened to an entity, as recorded in the outbox log. The applier decides from the database's current state, so the value is informational.</summary>
public enum OutboxOperation : byte
{
    /// <summary>The entity was added or modified.</summary>
    Upsert = 1,

    /// <summary>The entity was deleted.</summary>
    Delete = 2,
}

/// <summary>
/// One row of the outbox log: "entity <see cref="EntityId"/> of type <see cref="EntityType"/> changed". Rows are inserted by
/// <c>SaveChanges</c> in the same transaction as the change and read, in <see cref="Seq"/> order, by each index's applier.
/// </summary>
public sealed class OutboxEntry
{
    /// <summary>The position in the log. The database assigns it; it grows with every row and is never reused.</summary>
    public long Seq { get; set; }

    /// <summary>The CLR full name of the entity type.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The entity's search identifier.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>What happened to the entity.</summary>
    public OutboxOperation Operation { get; set; }
}

/// <summary>The log's single bookkeeping row: which log this is and how far it has been pruned.</summary>
public sealed class OutboxState
{
    /// <summary>Always 1.</summary>
    public int Id { get; set; }

    /// <summary>A random identifier of this log. An index that was built against another log (a restored or replaced database) rebuilds itself.</summary>
    public string LogId { get; set; } = string.Empty;

    /// <summary>Every row with a position up to this one has been deleted by retention; an index that has not applied them rebuilds itself.</summary>
    public long PrunedThrough { get; set; }
}

/// <summary>Adds the outbox tables to an EF model.</summary>
public static class OutboxModelBuilderExtensions
{
    /// <summary>Default name of the outbox table.</summary>
    public const string DefaultTableName = "OneIndexOutbox";

    /// <summary>
    /// Maps <see cref="OutboxEntry"/> to <paramref name="table"/> and <see cref="OutboxState"/> to <paramref name="table"/> + "State". Call it from
    /// <c>OnModelCreating</c>; <c>dotnet ef migrations add</c> then creates the tables like any others.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="table">The outbox table name; defaults to <see cref="DefaultTableName"/>.</param>
    /// <param name="schema">The database schema, or <see langword="null"/> for the provider's default.</param>
    /// <returns>The model builder.</returns>
    public static ModelBuilder UseOneIndexOutbox(this ModelBuilder modelBuilder, string table = DefaultTableName, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrEmpty(table);
        modelBuilder.Entity<OutboxEntry>(b =>
        {
            b.ToTable(table, schema);
            b.HasKey(e => e.Seq);
            b.Property(e => e.Seq).ValueGeneratedOnAdd();
            b.Property(e => e.EntityType).IsRequired().HasMaxLength(400);
            b.Property(e => e.EntityId).IsRequired().HasMaxLength(400);
            b.HasIndex(e => new { e.EntityType, e.Seq });
        });
        modelBuilder.Entity<OutboxState>(b =>
        {
            b.ToTable(table + "State", schema);
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.LogId).IsRequired().HasMaxLength(64);
        });
        return modelBuilder;
    }
}
