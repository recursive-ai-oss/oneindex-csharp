using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;

namespace OneIndex.EntityFrameworkCore;

/// <summary>
/// How far one index has applied the log, stored as the index commit's applied position: a version byte, the log id, three 64-bit counters
/// (big-endian) and the gaps. At most <see cref="MaxSize"/> bytes, the engine's limit for the marker.
/// </summary>
/// <param name="LogId">The log the index was built against.</param>
/// <param name="Seq">The high-water mark: every row of this entity type at or below it is reflected in the commit, except rows at <paramref name="Gaps"/>.</param>
/// <param name="Base">The position of the last full rebuild; rows up to it came from reading the tables, not from the log.</param>
/// <param name="Count">How many log rows of this entity type were applied after <paramref name="Base"/> and at or below <paramref name="Seq"/> (a row that was missing counts when it arrives, if it is above the base).</param>
/// <param name="Gaps">Ascending positions at or below <paramref name="Seq"/> (all distinct) that had no visible row when the mark passed them: transactions that had not committed yet, or that rolled back. A row that appears at one is applied then.</param>
internal sealed record OutboxPosition(Guid LogId, long Seq, long Base, long Count, long[] Gaps)
{
    public const int MaxSize = 1_024;
    private const byte Version = 2;
    private const int Fixed = 1 + 16 + 24;

    public byte[] Encode()
    {
        var bytes = new List<byte>(Fixed + 8 + Gaps.Length * 2);
        bytes.Add(Version);
        byte[] buf = new byte[24];
        LogId.TryWriteBytes(buf.AsSpan(0, 16), bigEndian: true, out _);
        bytes.AddRange(buf.AsSpan(0, 16).ToArray());
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(), Seq);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(8), Base);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(16), Count);
        bytes.AddRange(buf);
        WriteVarint(bytes, (ulong)Gaps.Length);
        long previous = Seq;
        for (int i = Gaps.Length - 1; i >= 0; i--)
        {
            WriteVarint(bytes, (ulong)(previous - Gaps[i]));
            previous = Gaps[i];
        }

        return [.. bytes];
    }

    /// <summary>The encoded size of a position with the given mark and ascending gaps (an upper bound if the mark later drops).</summary>
    public static int SizeOf(long seq, List<long> gapsAscending)
    {
        int size = Fixed + VarintSize((ulong)gapsAscending.Count);
        long previous = seq;
        for (int i = gapsAscending.Count - 1; i >= 0; i--)
        {
            size += VarintSize((ulong)(previous - gapsAscending[i]));
            previous = gapsAscending[i];
        }

        return size;
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, out OutboxPosition position)
    {
        position = null!;
        if (bytes.Length < Fixed + 1 || bytes.Length > MaxSize || bytes[0] != Version)
        {
            return false;
        }

        var logId = new Guid(bytes.Slice(1, 16), bigEndian: true);
        long seq = BinaryPrimitives.ReadInt64BigEndian(bytes[17..]);
        long baseSeq = BinaryPrimitives.ReadInt64BigEndian(bytes[25..]);
        long count = BinaryPrimitives.ReadInt64BigEndian(bytes[33..]);
        int at = Fixed;
        if (!ReadVarint(bytes, ref at, out ulong n) || n > (ulong)bytes.Length)
        {
            return false;
        }

        var gaps = new long[(int)n];
        long previous = seq;
        for (int i = gaps.Length - 1; i >= 0; i--)
        {
            if (!ReadVarint(bytes, ref at, out ulong delta) || (delta == 0 && i != gaps.Length - 1) || delta > (ulong)previous)
            {
                return false;
            }

            previous -= (long)delta;
            gaps[i] = previous;
        }

        if (at != bytes.Length || seq < 0 || baseSeq < 0 || count < 0)
        {
            return false;
        }

        position = new OutboxPosition(logId, seq, baseSeq, count, gaps);
        return true;
    }

    private static void WriteVarint(List<byte> to, ulong v)
    {
        while (v >= 0x80)
        {
            to.Add((byte)(v | 0x80));
            v >>= 7;
        }

        to.Add((byte)v);
    }

    private static int VarintSize(ulong v)
    {
        int n = 1;
        while (v >= 0x80)
        {
            v >>= 7;
            n++;
        }

        return n;
    }

    private static bool ReadVarint(ReadOnlySpan<byte> b, ref int at, out ulong v)
    {
        v = 0;
        for (int shift = 0; shift < 63; shift += 7)
        {
            if (at >= b.Length)
            {
                return false;
            }

            byte x = b[at++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>The log rows (of every entity type) after <paramref name="after"/> up to <paramref name="upTo"/>, in position order, at most <paramref name="take"/>.</summary>
    public static List<OutboxEntry> ReadWindow(DbContext db, long after, long upTo, int take)
        => [.. db.Set<OutboxEntry>().AsNoTracking().Where(e => e.Seq > after && e.Seq <= upTo).OrderBy(e => e.Seq).Take(take)];

    public static async ValueTask<List<OutboxEntry>> ReadWindowAsync(DbContext db, long after, long upTo, int take, CancellationToken cancellationToken)
        => await db.Set<OutboxEntry>().AsNoTracking().Where(e => e.Seq > after && e.Seq <= upTo).OrderBy(e => e.Seq).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The log rows at the given positions that are visible now.</summary>
    public static List<OutboxEntry> ReadBySeq(DbContext db, IReadOnlyList<long> seqs)
    {
        var rows = new List<OutboxEntry>();
        for (int i = 0; i < seqs.Count; i += 400)
        {
            List<long> chunk = [.. seqs.Skip(i).Take(400)];
            rows.AddRange(db.Set<OutboxEntry>().AsNoTracking().Where(e => chunk.Contains(e.Seq)));
        }

        return rows;
    }

    public static async ValueTask<List<OutboxEntry>> ReadBySeqAsync(DbContext db, IReadOnlyList<long> seqs, CancellationToken cancellationToken)
    {
        var rows = new List<OutboxEntry>();
        for (int i = 0; i < seqs.Count; i += 400)
        {
            List<long> chunk = [.. seqs.Skip(i).Take(400)];
            rows.AddRange(await db.Set<OutboxEntry>().AsNoTracking().Where(e => chunk.Contains(e.Seq)).ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        return rows;
    }

    /// <summary>The positions that have a row, after <paramref name="after"/> up to <paramref name="upTo"/>.</summary>
    public static List<long> ReadSeqs(DbContext db, long after, long upTo)
        => [.. db.Set<OutboxEntry>().AsNoTracking().Where(e => e.Seq > after && e.Seq <= upTo).Select(e => e.Seq)];

    public static async ValueTask<List<long>> ReadSeqsAsync(DbContext db, long after, long upTo, CancellationToken cancellationToken)
        => await db.Set<OutboxEntry>().AsNoTracking().Where(e => e.Seq > after && e.Seq <= upTo).Select(e => e.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Deletes all but the newest <paramref name="retain"/> positions and records the mark in one transaction.</summary>
    public static long Prune(DbContext db, long retain)
    {
        LogState state = ReadState(db);
        long cutoff = state.MaxSeq - retain;
        if (cutoff <= state.PrunedThrough)
        {
            return 0;
        }

        return PruneTo(db, cutoff);
    }

    /// <summary>
    /// Deletes the rows at or below <paramref name="cutoff"/> and raises the pruning mark to it. The mark only moves forward: a prune that decided on an
    /// older state must not lower a newer prune's mark, or an index behind the newer mark would not notice that rows are gone.
    /// </summary>
    public static long PruneTo(DbContext db, long cutoff)
    {
        using var tx = db.Database.BeginTransaction();
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
