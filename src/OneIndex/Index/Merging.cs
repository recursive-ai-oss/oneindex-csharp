using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>A segment the merge policy may choose, as the policy sees it.</summary>
/// <param name="Name">The segment name; names sort in creation order.</param>
/// <param name="LiveDocs">Documents not deleted.</param>
/// <param name="DeletedDocs">Documents deleted but still in the files.</param>
public readonly record struct MergeCandidate(string Name, int LiveDocs, int DeletedDocs);

/// <summary>
/// A tiered merge policy: segments are grouped by size into tiers whose size limits grow by <see cref="SegmentsPerTier"/>, and
/// when a tier holds that many segments the oldest <see cref="MaxMergeAtOnce"/> of them merge into one that lands in a higher
/// tier. This keeps the segment count logarithmic in the index size and rewrites each document only about once per tier. A
/// segment with too many deleted documents is rewritten alone to reclaim them. The policy is a pure function of the candidates,
/// so the same segments always give the same plan.
/// </summary>
public sealed class TieredMergePolicy
{
    /// <summary>How many segments of one tier trigger a merge. Default 10.</summary>
    public int SegmentsPerTier { get; init; } = 10;

    /// <summary>The most segments merged in one step. Default 10.</summary>
    public int MaxMergeAtOnce { get; init; } = 10;

    /// <summary>Segments below this many live documents count as this size, so a stream of tiny flushes merges together. Default 1,000.</summary>
    public int FloorSegmentDocs { get; init; } = 1_000;

    /// <summary>A merge never produces more live documents than this. Default 5,000,000.</summary>
    public int MaxMergedSegmentDocs { get; init; } = 5_000_000;

    /// <summary>A segment whose deleted share exceeds this percentage is rewritten alone. Default 33.</summary>
    public int DeletesPctAllowed { get; init; } = 33;

    /// <summary>Plans merges.</summary>
    /// <param name="candidates">The segments not already being merged, in any order.</param>
    /// <returns>Disjoint groups of segment names to merge, each group in name order.</returns>
    public IReadOnlyList<IReadOnlyList<string>> Select(IReadOnlyList<MergeCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ordered = new List<MergeCandidate>(candidates);
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        var plans = new List<IReadOnlyList<string>>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (MergeCandidate c in ordered)
        {
            long total = (long)c.LiveDocs + c.DeletedDocs;
            if (c.DeletedDocs > 0 && c.DeletedDocs * 100L > total * DeletesPctAllowed)
            {
                plans.Add([c.Name]);
                taken.Add(c.Name);
            }
        }

        var tiers = new SortedDictionary<int, List<MergeCandidate>>();
        foreach (MergeCandidate c in ordered)
        {
            if (taken.Contains(c.Name))
            {
                continue;
            }

            int tier = 0;
            long limit = (long)FloorSegmentDocs * SegmentsPerTier;
            while (c.LiveDocs >= limit && tier < 40)
            {
                tier++;
                limit *= SegmentsPerTier;
            }

            if (!tiers.TryGetValue(tier, out List<MergeCandidate>? list))
            {
                tiers[tier] = list = [];
            }

            list.Add(c);
        }

        foreach (List<MergeCandidate> tier in tiers.Values)
        {
            if (tier.Count < SegmentsPerTier)
            {
                continue;
            }

            var group = new List<string>();
            long docs = 0;
            foreach (MergeCandidate c in tier)
            {
                if (group.Count >= MaxMergeAtOnce || docs + c.LiveDocs > MaxMergedSegmentDocs)
                {
                    break;
                }

                group.Add(c.Name);
                docs += c.LiveDocs;
            }

            if (group.Count >= 2)
            {
                plans.Add(group);
            }
        }

        return plans;
    }
}

/// <summary>
/// Runs merges. A merge is a unit of work with no result the caller waits on; the index installs finished merges at its next
/// commit. The seam exists so that merges run on a background thread where threads exist and cooperatively where they do not
/// (browser WASM), with the same results for the same sequence of operations.
/// </summary>
public interface IMergeScheduler : IDisposable
{
    /// <summary>Queues work. It may start at once on another thread, or wait for <see cref="Poll"/> or <see cref="WaitForIdle"/>.</summary>
    /// <param name="work">The work; it receives the token that is cancelled when the index closes.</param>
    void Enqueue(Action<CancellationToken> work);

    /// <summary>Gives a cooperative scheduler a chance to run queued work on the calling thread. Threaded schedulers do nothing.</summary>
    /// <param name="cancellationToken">Cancels between work items.</param>
    void Poll(CancellationToken cancellationToken);

    /// <summary>Returns when no work is queued or running.</summary>
    /// <param name="cancellationToken">Cancels the wait (and stops cooperative work between items).</param>
    void WaitForIdle(CancellationToken cancellationToken);

    /// <summary>Called by throttled work to slow itself down. A cooperative scheduler returns at once.</summary>
    /// <param name="delay">How long the work should pause.</param>
    /// <param name="cancellationToken">Cancels the pause.</param>
    void Pause(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Runs queued merges on the calling thread, one after another, when <see cref="Poll"/> or <see cref="WaitForIdle"/> is called. It
/// needs no threads, so it is the default and the one browser WASM uses.
/// </summary>
public sealed class CooperativeMergeScheduler : IMergeScheduler
{
    private readonly Queue<Action<CancellationToken>> _queue = new();
    private readonly CancellationTokenSource _closed = new();

    /// <inheritdoc/>
    public void Enqueue(Action<CancellationToken> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_queue)
        {
            _queue.Enqueue(work);
        }
    }

    /// <inheritdoc/>
    public void Poll(CancellationToken cancellationToken) => Drain(cancellationToken);

    /// <inheritdoc/>
    public void WaitForIdle(CancellationToken cancellationToken) => Drain(cancellationToken);

    /// <inheritdoc/>
    public void Pause(TimeSpan delay, CancellationToken cancellationToken)
    {
        // There is no other thread to yield to; slowing the caller down would only slow the application.
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _closed.Cancel();
        lock (_queue)
        {
            _queue.Clear();
        }

        _closed.Dispose();
    }

    private void Drain(CancellationToken cancellationToken)
    {
        while (true)
        {
            Action<CancellationToken>? work;
            lock (_queue)
            {
                if (!_queue.TryDequeue(out work))
                {
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            work(_closed.Token);
        }
    }
}

/// <summary>Runs queued merges one at a time on a single background thread.</summary>
public sealed class ThreadedMergeScheduler : IMergeScheduler
{
    private readonly Queue<Action<CancellationToken>> _queue = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly Thread _thread;
    private bool _running;
    private bool _disposed;

    /// <summary>Starts the worker thread.</summary>
    public ThreadedMergeScheduler()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "OneIndex merge" };
        _thread.Start();
    }

    /// <inheritdoc/>
    public void Enqueue(Action<CancellationToken> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_queue)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.Enqueue(work);
            Monitor.PulseAll(_queue);
        }
    }

    /// <inheritdoc/>
    public void Poll(CancellationToken cancellationToken)
    {
        // The worker thread is already running the queue.
    }

    /// <inheritdoc/>
    public void WaitForIdle(CancellationToken cancellationToken)
    {
        lock (_queue)
        {
            while ((_queue.Count > 0 || _running) && !_disposed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_queue, 50);
            }
        }
    }

    /// <inheritdoc/>
    public void Pause(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            cancellationToken.WaitHandle.WaitOne(delay);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_queue)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.Clear();
            Monitor.PulseAll(_queue);
        }

        _closed.Cancel();
        _thread.Join();
        _closed.Dispose();
    }

    private void Loop()
    {
        while (true)
        {
            Action<CancellationToken> work;
            lock (_queue)
            {
                while (_queue.Count == 0)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    Monitor.Wait(_queue);
                }

                work = _queue.Dequeue();
                _running = true;
            }

            try
            {
                work(_closed.Token);
            }
            catch (OperationCanceledException)
            {
                // The index is closing.
            }
            finally
            {
                lock (_queue)
                {
                    _running = false;
                    Monitor.PulseAll(_queue);
                }
            }
        }
    }
}

/// <summary>
/// Counts files that something still needs and deletes a file when its last reference goes. A file that a reader in another
/// process still pins (<paramref name="pinnedByOthers"/>) is kept and deleted by a later <see cref="ReclaimDeferred"/>. Safe for concurrent use.
/// </summary>
internal sealed class FileReferences(IStorage storage, Func<HashSet<string>?>? pinnedByOthers = null)
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _deferred = new(StringComparer.Ordinal);

    public void Acquire(IEnumerable<string> names)
    {
        lock (_counts)
        {
            foreach (string n in names)
            {
                _counts[n] = _counts.GetValueOrDefault(n) + 1;
            }
        }
    }

    public void Release(IEnumerable<string> names)
    {
        List<string>? dead = null;
        lock (_counts)
        {
            foreach (string n in names)
            {
                if (!_counts.TryGetValue(n, out int c))
                {
                    continue;
                }

                if (c <= 1)
                {
                    _counts.Remove(n);
                    (dead ??= []).Add(n);
                }
                else
                {
                    _counts[n] = c - 1;
                }
            }
        }

        if (dead is null)
        {
            return;
        }

        dead.Sort(StringComparer.Ordinal);
        HashSet<string>? pinned = ScanPinned(out bool unknown);
        foreach (string n in dead)
        {
            if (unknown || (pinned is not null && pinned.Contains(n)))
            {
                lock (_deferred)
                {
                    _deferred.Add(n);
                }
            }
            else
            {
                TryDelete(n);
            }
        }
    }

    /// <summary>Marks a file as garbage that a reader in another process still pins; <see cref="ReclaimDeferred"/> deletes it later.</summary>
    public void Defer(string name)
    {
        lock (_deferred)
        {
            _deferred.Add(name);
        }
    }

    /// <summary>Whether some file is waiting for another process's reader to let go of it.</summary>
    public bool HasDeferred
    {
        get
        {
            lock (_deferred)
            {
                return _deferred.Count > 0;
            }
        }
    }

    /// <summary>Deletes deferred files that no live reader lease pins any more.</summary>
    public void ReclaimDeferred()
    {
        string[] names;
        lock (_deferred)
        {
            if (_deferred.Count == 0)
            {
                return;
            }

            names = [.. _deferred];
        }

        HashSet<string>? pinned = ScanPinned(out bool unknown);
        if (unknown)
        {
            return;
        }

        foreach (string n in names)
        {
            if (pinned is not null && pinned.Contains(n))
            {
                continue;
            }

            TryDelete(n);
            lock (_deferred)
            {
                _deferred.Remove(n);
            }
        }
    }

    // If the scan itself fails we cannot tell who reads, so every file stays deferred until a later scan works.
    private HashSet<string>? ScanPinned(out bool unknown)
    {
        unknown = false;
        try
        {
            return pinnedByOthers?.Invoke();
        }
        catch (IOException)
        {
            unknown = true;
            return null;
        }
    }

    public int Count(string name)
    {
        lock (_counts)
        {
            return _counts.GetValueOrDefault(name);
        }
    }

    /// <summary>Deletes a file, tolerating failure: a file that stays is garbage that the next open removes.</summary>
    public void TryDelete(string name)
    {
        try
        {
            storage.Delete(name);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
