using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;

namespace OneIndex.EntityFrameworkCore;

/// <summary>How far one index has applied the log, stored as the index commit's applied position (40 bytes, big-endian).</summary>
/// <param name="LogId">The log the index was built against.</param>
/// <param name="Seq">Every row of this entity type up to this position is reflected in the commit.</param>
/// <param name="Base">The position of the last full rebuild; rows up to it came from reading the tables, not from the log.</param>
/// <param name="Count">How many log rows of this entity type were applied after <paramref name="Base"/>. Equal to the number of such rows at or below <paramref name="Seq"/> unless the log was pruned, so a row applied twice or skipped shows.</param>
internal readonly record struct OutboxPosition(Guid LogId, long Seq, long Base, long Count)
{
    public const int Size = 40;

    public byte[] Encode()
    {
        var bytes = new byte[Size];
        LogId.TryWriteBytes(bytes.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16), Seq);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24), Base);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32), Count);
        return bytes;
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, out OutboxPosition position)
    {
        if (bytes.Length != Size)
        {
            position = default;
            return false;
        }

        position = new OutboxPosition(
            new Guid(bytes[..16], bigEndian: true),
            BinaryPrimitives.ReadInt64BigEndian(bytes[16..]),
            BinaryPrimitives.ReadInt64BigEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64BigEndian(bytes[32..]));
        return true;
    }
}

/// <summary>The log's identity and pruning mark, plus its highest position.</summary>
internal readonly record struct LogState(Guid LogId, long PrunedThrough, long MaxSeq);

/// <summary>Reads and prunes the outbox. Sync and async twins do the same queries.</summary>
internal static class OutboxStore
{
    public static LogState ReadState(DbContext db)
    {
        OutboxState? row = db.Set<OutboxState>().AsNoTracking().FirstOrDefault(s => s.Id == 1);
        if (row is null)
        {
            try
            {
                db.Set<OutboxState>().Add(new OutboxState { Id = 1, LogId = Guid.NewGuid().ToString("N"), PrunedThrough = 0 });
                db.SaveChanges();
            }
            catch (DbUpdateException)
            {
                // Another instance created the row first.
                db.ChangeTracker.Clear();
            }

            row = db.Set<OutboxState>().AsNoTracking().First(s => s.Id == 1);
        }

        long? max = db.Set<OutboxEntry>().Max(e => (long?)e.Seq);
        return new LogState(Guid.ParseExact(row.LogId, "N"), row.PrunedThrough, Math.Max(max ?? 0, row.PrunedThrough));
    }

    public static async ValueTask<LogState> ReadStateAsync(DbContext db, CancellationToken cancellationToken)
    {
        OutboxState? row = await db.Set<OutboxState>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            try
            {
                db.Set<OutboxState>().Add(new OutboxState { Id = 1, LogId = Guid.NewGuid().ToString("N"), PrunedThrough = 0 });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }

            row = await db.Set<OutboxState>().AsNoTracking().FirstAsync(s => s.Id == 1, cancellationToken).ConfigureAwait(false);
        }

        long? max = await db.Set<OutboxEntry>().MaxAsync(e => (long?)e.Seq, cancellationToken).ConfigureAwait(false);
        return new LogState(Guid.ParseExact(row.LogId, "N"), row.PrunedThrough, Math.Max(max ?? 0, row.PrunedThrough));
    }

    public static long ReadPruned(DbContext db)
        => db.Set<OutboxState>().AsNoTracking().Where(s => s.Id == 1).Select(s => s.PrunedThrough).FirstOrDefault();

    public static async ValueTask<long> ReadPrunedAsync(DbContext db, CancellationToken cancellationToken)
        => await db.Set<OutboxState>().AsNoTracking().Where(s => s.Id == 1).Select(s => s.PrunedThrough).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public static List<OutboxEntry> ReadRows(DbContext db, string entityType, long after, long upTo, int take)
        => [.. db.Set<OutboxEntry>().AsNoTracking().Where(e => e.EntityType == entityType && e.Seq > after && e.Seq <= upTo).OrderBy(e => e.Seq).Take(take)];

    public static async ValueTask<List<OutboxEntry>> ReadRowsAsync(DbContext db, string entityType, long after, long upTo, int take, CancellationToken cancellationToken)
        => await db.Set<OutboxEntry>().AsNoTracking().Where(e => e.EntityType == entityType && e.Seq > after && e.Seq <= upTo).OrderBy(e => e.Seq).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Deletes all but the newest <paramref name="retain"/> positions and records the mark in one transaction.</summary>
    public static long Prune(DbContext db, long retain)
    {
        LogState state = ReadState(db);
        long cutoff = state.MaxSeq - retain;
        if (cutoff <= state.PrunedThrough)
        {
            return 0;
        }

        using var tx = db.Database.BeginTransaction();
        // The mark only moves forward: a prune that read an older state must not lower a newer prune's mark.
        db.Set<OutboxState>().Where(s => s.Id == 1 && s.PrunedThrough < cutoff).ExecuteUpdate(u => u.SetProperty(s => s.PrunedThrough, cutoff));
        int deleted = db.Set<OutboxEntry>().Where(e => e.Seq <= cutoff).ExecuteDelete();
        tx.Commit();
        return deleted;
    }

    public static async ValueTask<long> PruneAsync(DbContext db, long retain, CancellationToken cancellationToken)
    {
        LogState state = await ReadStateAsync(db, cancellationToken).ConfigureAwait(false);
        long cutoff = state.MaxSeq - retain;
        if (cutoff <= state.PrunedThrough)
        {
            return 0;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<OutboxState>().Where(s => s.Id == 1 && s.PrunedThrough < cutoff).ExecuteUpdateAsync(u => u.SetProperty(s => s.PrunedThrough, cutoff), cancellationToken).ConfigureAwait(false);
        int deleted = await db.Set<OutboxEntry>().Where(e => e.Seq <= cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }
}
