using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace OneIndex.EntityFrameworkCore;

/// <summary>One indexed entity type of a runtime: its index, its identifier and the catch-up and rebuild loops over the log.</summary>
internal abstract class IndexedEntity : IDisposable
{
    public abstract Type ClrType { get; }

    public string Name => ClrType.FullName!;

    /// <summary>The search identifier of an entity instance.</summary>
    public abstract string? IdOf(object entity);

    public abstract int CatchUp(OneIndexRuntime runtime, CancellationToken cancellationToken);

    public abstract ValueTask<int> CatchUpAsync(OneIndexRuntime runtime, CancellationToken cancellationToken);

    public abstract void Reindex(OneIndexRuntime runtime, CancellationToken cancellationToken);

    public abstract ValueTask ReindexAsync(OneIndexRuntime runtime, CancellationToken cancellationToken);

    public abstract void Dispose();
}

internal sealed class IndexedEntity<T> : IndexedEntity
    where T : class
{
    private const int LoadChunk = 400;
    private SearchIndex<T>? _index;
    private string? _idName;

    public override Type ClrType => typeof(T);

    public string IdName => _idName ??= SearchMappings.Require<T>().Schema.IdField ?? throw new InvalidOperationException($"{typeof(T).FullName} has no [SearchId] member.");

    public override string? IdOf(object entity) => SearchMappings.Require<T>().GetId((T)entity);

    public SearchIndex<T> Index(OneIndexRuntime runtime)
    {
        lock (this)
        {
            return _index ??= SearchIndex.Open<T>(Path.Combine(runtime.Path, typeof(T).Name));
        }
    }

    /// <summary>Entities of the given identifiers, from the database as it is now, in no particular order.</summary>
    public static List<T> Load(DbContext db, IReadOnlyList<string> ids, string idName, IEnumerable<Expression<Func<T, bool>>>? extra, bool track)
    {
        IQueryable<T> q = db.Set<T>();
        if (!track)
        {
            q = q.AsNoTracking();
        }

        q = q.Where(e => ids.Contains(EF.Property<string>(e, idName)));
        if (extra is not null)
        {
            foreach (Expression<Func<T, bool>> p in extra)
            {
                q = q.Where(p);
            }
        }

        return [.. q];
    }

    public static async ValueTask<List<T>> LoadAsync(DbContext db, IReadOnlyList<string> ids, string idName, IEnumerable<Expression<Func<T, bool>>>? extra, bool track, CancellationToken cancellationToken)
    {
        IQueryable<T> q = db.Set<T>();
        if (!track)
        {
            q = q.AsNoTracking();
        }

        q = q.Where(e => ids.Contains(EF.Property<string>(e, idName)));
        if (extra is not null)
        {
            foreach (Expression<Func<T, bool>> p in extra)
            {
                q = q.Where(p);
            }
        }

        return await q.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    // The index is trusted only when it was built against this log and has applied a position the log can still continue from.
    private static bool Continuable(SearchIndex<T> index, LogState state, out OutboxPosition position)
        => OutboxPosition.TryParse(index.AppliedPosition.Span, out position)
           && position.LogId == state.LogId
           && position.Seq >= state.PrunedThrough
           && position.Seq <= state.MaxSeq;

    // Applies a batch: entities found in the database are upserted, identifiers without one are deleted (the log says "changed", the database says how).
    private void Apply(SearchIndex<T> index, List<string> ids, List<T> found, CancellationToken cancellationToken)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (T e in found)
        {
            present.Add(IdOf(e)!);
        }

        index.Upsert(found, cancellationToken);
        foreach (string id in ids)
        {
            if (!present.Contains(id))
            {
                index.Delete(id);
            }
        }
    }

    private static List<string> DistinctIds(List<OutboxEntry> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (OutboxEntry r in rows)
        {
            if (seen.Add(r.EntityId))
            {
                ids.Add(r.EntityId);
            }
        }

        return ids;
    }

    public override int CatchUp(OneIndexRuntime runtime, CancellationToken cancellationToken)
    {
        using DbContext db = runtime.CreateContext();
        SearchIndex<T> index = Index(runtime);
        LogState state = OutboxStore.ReadState(db);
        if (!Continuable(index, state, out OutboxPosition position))
        {
            Reindex(runtime, db, cancellationToken);
            return 0;
        }

        int applied = 0;
        int batch = runtime.Options.BatchSize;
        while (position.Seq < state.MaxSeq)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<OutboxEntry> rows = OutboxStore.ReadRows(db, Name, position.Seq, state.MaxSeq, batch);
            if (OutboxStore.ReadPruned(db) > position.Seq)
            {
                // Retention removed rows between the check and the read: the rows read may have a hole in them.
                Reindex(runtime, db, cancellationToken);
                return applied;
            }

            long next = rows.Count == batch ? rows[^1].Seq : state.MaxSeq;
            if (rows.Count > 0)
            {
                runtime.Probe("apply.read");
                List<string> ids = DistinctIds(rows);
                var found = new List<T>(ids.Count);
                for (int i = 0; i < ids.Count; i += LoadChunk)
                {
                    found.AddRange(Load(db, ids.GetRange(i, Math.Min(LoadChunk, ids.Count - i)), IdName, null, track: false));
                }

                Apply(index, ids, found, cancellationToken);
                runtime.Probe("apply.before-commit");
            }

            position = position with { Seq = next, Count = position.Count + rows.Count };
            index.Commit(position.Encode(), cancellationToken);
            runtime.Probe("apply.after-commit");
            applied += rows.Count;
        }

        return applied;
    }

    public override async ValueTask<int> CatchUpAsync(OneIndexRuntime runtime, CancellationToken cancellationToken)
    {
        await using DbContext db = runtime.CreateContext();
        SearchIndex<T> index = Index(runtime);
        LogState state = await OutboxStore.ReadStateAsync(db, cancellationToken).ConfigureAwait(false);
        if (!Continuable(index, state, out OutboxPosition position))
        {
            await ReindexAsync(runtime, db, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        int applied = 0;
        int batch = runtime.Options.BatchSize;
        while (position.Seq < state.MaxSeq)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<OutboxEntry> rows = await OutboxStore.ReadRowsAsync(db, Name, position.Seq, state.MaxSeq, batch, cancellationToken).ConfigureAwait(false);
            if (await OutboxStore.ReadPrunedAsync(db, cancellationToken).ConfigureAwait(false) > position.Seq)
            {
                await ReindexAsync(runtime, db, cancellationToken).ConfigureAwait(false);
                return applied;
            }

            long next = rows.Count == batch ? rows[^1].Seq : state.MaxSeq;
            if (rows.Count > 0)
            {
                runtime.Probe("apply.read");
                List<string> ids = DistinctIds(rows);
                var found = new List<T>(ids.Count);
                for (int i = 0; i < ids.Count; i += LoadChunk)
                {
                    found.AddRange(await LoadAsync(db, ids.GetRange(i, Math.Min(LoadChunk, ids.Count - i)), IdName, null, track: false, cancellationToken).ConfigureAwait(false));
                }

                Apply(index, ids, found, cancellationToken);
                runtime.Probe("apply.before-commit");
            }

            position = position with { Seq = next, Count = position.Count + rows.Count };
            index.Commit(position.Encode(), cancellationToken);
            runtime.Probe("apply.after-commit");
            applied += rows.Count;
        }

        return applied;
    }

    public override void Reindex(OneIndexRuntime runtime, CancellationToken cancellationToken)
    {
        using DbContext db = runtime.CreateContext();
        Reindex(runtime, db, cancellationToken);
    }

    public override async ValueTask ReindexAsync(OneIndexRuntime runtime, CancellationToken cancellationToken)
    {
        await using DbContext db = runtime.CreateContext();
        await ReindexAsync(runtime, db, cancellationToken).ConfigureAwait(false);
    }

    // A full rebuild reads the position first and the tables after, so every change after the position is still in the log and is replayed
    // (replaying a change the tables already show is harmless: the log says "changed", not what to).
    private void Reindex(OneIndexRuntime runtime, DbContext db, CancellationToken cancellationToken)
    {
        SearchIndex<T> index = Index(runtime);
        LogState state = OutboxStore.ReadState(db);
        runtime.Probe("reindex.start");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? last = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? after = last;
            string idName = IdName;
            IQueryable<T> q = db.Set<T>().AsNoTracking();
            if (after is not null)
            {
                q = q.Where(e => EF.Property<string>(e, idName).CompareTo(after) > 0);
            }

            List<T> page = [.. q.OrderBy(e => EF.Property<string>(e, idName)).Take(runtime.Options.BatchSize)];
            if (page.Count == 0)
            {
                break;
            }

            foreach (T e in page)
            {
                seen.Add(IdOf(e)!);
            }

            index.Upsert(page, cancellationToken);
            last = IdOf(page[^1]);
            runtime.Probe("reindex.page");
        }

        FinishReindex(index, state, seen, runtime, cancellationToken);
    }

    private async ValueTask ReindexAsync(OneIndexRuntime runtime, DbContext db, CancellationToken cancellationToken)
    {
        SearchIndex<T> index = Index(runtime);
        LogState state = await OutboxStore.ReadStateAsync(db, cancellationToken).ConfigureAwait(false);
        runtime.Probe("reindex.start");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? last = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? after = last;
            string idName = IdName;
            IQueryable<T> q = db.Set<T>().AsNoTracking();
            if (after is not null)
            {
                q = q.Where(e => EF.Property<string>(e, idName).CompareTo(after) > 0);
            }

            List<T> page = await q.OrderBy(e => EF.Property<string>(e, idName)).Take(runtime.Options.BatchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (T e in page)
            {
                seen.Add(IdOf(e)!);
            }

            index.Upsert(page, cancellationToken);
            last = IdOf(page[^1]);
            runtime.Probe("reindex.page");
        }

        FinishReindex(index, state, seen, runtime, cancellationToken);
    }

    private static void FinishReindex(SearchIndex<T> index, LogState state, HashSet<string> seen, OneIndexRuntime runtime, CancellationToken cancellationToken)
    {
        foreach (string id in index.CommittedIds())
        {
            if (!seen.Contains(id))
            {
                index.Delete(id);
            }
        }

        runtime.Probe("reindex.before-commit");
        index.Commit(new OutboxPosition(state.LogId, state.MaxSeq, state.MaxSeq, 0).Encode(), cancellationToken);
        runtime.Probe("reindex.after-commit");
    }

    /// <summary>Re-reads the given entities and upserts them, leaving the position alone.</summary>
    public void Refresh(OneIndexRuntime runtime, List<T> entities, CancellationToken cancellationToken)
    {
        SearchIndex<T> index = Index(runtime);
        index.Upsert(entities, cancellationToken);
        index.Commit(index.AppliedPosition.Span, cancellationToken);
    }

    public override void Dispose()
    {
        _index?.Dispose();
        _index = null;
    }
}
