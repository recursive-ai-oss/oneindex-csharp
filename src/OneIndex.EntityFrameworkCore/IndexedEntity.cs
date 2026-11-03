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

    /// <summary>How many log rows this index applied only because they became visible below its high-water mark (see GapWalk).</summary>
    public long LateRowsApplied { get; protected set; }

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

    // A commit that failed (a full disk, a revoked permission) stops the index writer for good, because it cannot know whether the commit
    // became durable. The log still holds every row the failed commit would have applied, so the right recovery is to close the index and
    // open it again at the next catch-up, which finds the last commit that stands. Without this a transient error would wedge the applier
    // until the process restarted.
    private void OnIndex(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Cancellation too: a commit cancelled after its files were written stops the writer the same way.
            ResetIndex();
            throw;
        }
    }

    private void ResetIndex()
    {
        SearchIndex<T>? failed;
        lock (this)
        {
            failed = _index;
            _index = null;
        }

        try
        {
            failed?.Dispose();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The index is being replaced because it failed; closing it may fail the same way.
        }
    }

    // The index is trusted only when it was built against this log and has applied a position the log can still continue from.
    private static bool Continuable(SearchIndex<T> index, LogState state, out OutboxPosition position)
        => OutboxPosition.TryParse(index.AppliedPosition.Span, out position!)
           && position.LogId == state.LogId
           && position.Seq >= state.PrunedThrough
           && position.Seq <= state.MaxSeq
           && (position.Gaps.Length == 0 || position.Gaps[0] > state.PrunedThrough);

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

    private readonly Dictionary<long, long> _firstSeen = [];

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
        var walk = new GapWalk(runtime.Options, Name, position, state.MaxSeq, _firstSeen);
        while (walk.More)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<OutboxEntry> window = OutboxStore.ReadWindow(db, walk.Cursor, state.MaxSeq, runtime.Options.BatchSize);
            List<OutboxEntry> late = walk.OpenGaps.Count == 0 ? [] : OutboxStore.ReadBySeq(db, walk.OpenGaps);
            long pruned = OutboxStore.ReadPruned(db);
            WalkStep step = walk.Advance(window, late, pruned);
            LateRowsApplied += step.Late;
            if (step.Rebuild || pruned > walk.Position.Seq)
            {
                // Retention removed rows this index has not applied (possibly between the check and the read): the rows read may have holes.
                Reindex(runtime, db, cancellationToken);
                return applied;
            }

            if (step.Ids.Count > 0)
            {
                runtime.Probe("apply.read");
                var found = new List<T>(step.Ids.Count);
                for (int i = 0; i < step.Ids.Count; i += LoadChunk)
                {
                    found.AddRange(Load(db, step.Ids.GetRange(i, Math.Min(LoadChunk, step.Ids.Count - i)), IdName, null, track: false));
                }

                OnIndex(() => Apply(index, step.Ids, found, cancellationToken));
                runtime.Probe("apply.before-commit");
            }

            if (step.Ids.Count > 0 || step.Position is not null && !step.Position.Encode().AsSpan().SequenceEqual(index.AppliedPosition.Span))
            {
                OnIndex(() => index.Commit(step.Position!.Encode(), cancellationToken));
                runtime.Probe("apply.after-commit");
                applied += step.Ids.Count;
            }
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
        var walk = new GapWalk(runtime.Options, Name, position, state.MaxSeq, _firstSeen);
        while (walk.More)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<OutboxEntry> window = await OutboxStore.ReadWindowAsync(db, walk.Cursor, state.MaxSeq, runtime.Options.BatchSize, cancellationToken).ConfigureAwait(false);
            List<OutboxEntry> late = walk.OpenGaps.Count == 0 ? [] : await OutboxStore.ReadBySeqAsync(db, walk.OpenGaps, cancellationToken).ConfigureAwait(false);
            long pruned = await OutboxStore.ReadPrunedAsync(db, cancellationToken).ConfigureAwait(false);
            WalkStep step = walk.Advance(window, late, pruned);
            LateRowsApplied += step.Late;
            if (step.Rebuild || pruned > walk.Position.Seq)
            {
                await ReindexAsync(runtime, db, cancellationToken).ConfigureAwait(false);
                return applied;
            }

            if (step.Ids.Count > 0)
            {
                runtime.Probe("apply.read");
                var found = new List<T>(step.Ids.Count);
                for (int i = 0; i < step.Ids.Count; i += LoadChunk)
                {
                    found.AddRange(await LoadAsync(db, step.Ids.GetRange(i, Math.Min(LoadChunk, step.Ids.Count - i)), IdName, null, track: false, cancellationToken).ConfigureAwait(false));
                }

                OnIndex(() => Apply(index, step.Ids, found, cancellationToken));
                runtime.Probe("apply.before-commit");
            }

            if (step.Ids.Count > 0 || step.Position is not null && !step.Position.Encode().AsSpan().SequenceEqual(index.AppliedPosition.Span))
            {
                OnIndex(() => index.Commit(step.Position!.Encode(), cancellationToken));
                runtime.Probe("apply.after-commit");
                applied += step.Ids.Count;
            }
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
        List<long> present = OutboxStore.ReadSeqs(db, Math.Max(state.PrunedThrough, state.MaxSeq - runtime.Options.LateRowWindow), state.MaxSeq);
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

            OnIndex(() => index.Upsert(page, cancellationToken));
            last = IdOf(page[^1]);
            runtime.Probe("reindex.page");
        }

        OnIndex(() => FinishReindex(index, state, present, seen, runtime, cancellationToken));
    }

    private async ValueTask ReindexAsync(OneIndexRuntime runtime, DbContext db, CancellationToken cancellationToken)
    {
        SearchIndex<T> index = Index(runtime);
        LogState state = await OutboxStore.ReadStateAsync(db, cancellationToken).ConfigureAwait(false);
        List<long> present = await OutboxStore.ReadSeqsAsync(db, Math.Max(state.PrunedThrough, state.MaxSeq - runtime.Options.LateRowWindow), state.MaxSeq, cancellationToken).ConfigureAwait(false);
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

            OnIndex(() => index.Upsert(page, cancellationToken));
            last = IdOf(page[^1]);
            runtime.Probe("reindex.page");
        }

        OnIndex(() => FinishReindex(index, state, present, seen, runtime, cancellationToken));
    }

    private void FinishReindex(SearchIndex<T> index, LogState state, List<long> present, HashSet<string> seen, OneIndexRuntime runtime, CancellationToken cancellationToken)
    {
        foreach (string id in index.CommittedIds())
        {
            if (!seen.Contains(id))
            {
                index.Delete(id);
            }
        }

        runtime.Probe("reindex.before-commit");
        OutboxPosition start = StartAfterRebuild(state, present, runtime.Options);
        index.Commit(start.Encode(), cancellationToken);
        runtime.Probe("reindex.after-commit");
    }

    // The position a rebuild commits. The tables were read after the log's highest position was, so a change committed since then is either in the
    // tables or still in the log after the mark. A position below the mark with no visible row, though, may belong to a transaction that had not
    // committed when the tables were read, and its change may be in neither: those become gaps, exactly as in a walk.
    private OutboxPosition StartAfterRebuild(LogState state, List<long> present, OneIndexOptions options)
    {
        var have = new HashSet<long>(present);
        long now = options.TimeProvider.GetUtcNow().UtcTicks;
        var gaps = new List<long>();
        long mark = state.MaxSeq;
        _firstSeen.Clear();
        for (long g = Math.Max(state.PrunedThrough, state.MaxSeq - options.LateRowWindow) + 1; g <= state.MaxSeq; g++)
        {
            if (have.Contains(g))
            {
                continue;
            }

            gaps.Add(g);
            if (OutboxPosition.SizeOf(state.MaxSeq, gaps) > OutboxPosition.MaxSize)
            {
                // No room: stop the mark before this position; the rows after it are applied again by the next pass.
                gaps.RemoveAt(gaps.Count - 1);
                mark = g - 1;
                break;
            }

            _firstSeen[g] = now;
        }

        return new OutboxPosition(state.LogId, mark, mark, 0, [.. gaps]);
    }

    /// <summary>Re-reads the given entities and upserts them, leaving the position alone.</summary>
    public void Refresh(OneIndexRuntime runtime, List<T> entities, CancellationToken cancellationToken)
    {
        SearchIndex<T> index = Index(runtime);
        OnIndex(() =>
        {
            index.Upsert(entities, cancellationToken);
            index.Commit(index.AppliedPosition.Span, cancellationToken);
        });
    }

    public override void Dispose()
    {
        _index?.Dispose();
        _index = null;
    }
}
