namespace OneIndex.EntityFrameworkCore;

/// <summary>What one step of the catch-up decided: which entities to read again, and the position to commit with them.</summary>
internal sealed class WalkStep
{
    public List<string> Ids { get; } = [];

    public OutboxPosition? Position { get; set; }

    /// <summary>A position that an unresolved gap sits at or below the pruning mark: a row may have been deleted unseen.</summary>
    public bool Rebuild { get; set; }
}

/// <summary>
/// Walks the log for one index, tolerating rows that become visible out of position order.
/// <para>
/// A database that hands out positions at insert time but shows rows at commit time (SQL Server, PostgreSQL, MySQL under concurrent writers)
/// can show position 7 before position 6. Reading "everything after the high-water mark" would then skip 6 for good. So the walk records every
/// position below the mark that had no visible row as a *gap*, in the index's commit, and on each pass looks the gaps up again: a row that
/// has appeared is applied then. Because applying reads the entity's current state, applying a row early, late or twice gives the same index.
/// </para>
/// <para>
/// A gap that never fills (an insert that rolled back, or a process that died mid-transaction) is released by two bounded rules: when it is
/// <see cref="OneIndexOptions.LateRowWindow"/> positions behind the newest row, or when it has been waited for <see cref="OneIndexOptions.LateRowAge"/>
/// (timed from the first time this process saw it). The position has a size limit; when the gaps would not fit the mark simply stops before the
/// gap that does not fit and the rows beyond it are applied again on the next pass (correct, only repeated work) until older gaps are released.
/// Nothing is ever released early to make room.
/// </para>
/// </summary>
internal sealed class GapWalk
{
    private readonly OneIndexOptions _options;
    private readonly string _entityType;
    private readonly Dictionary<long, long> _firstSeen;
    private readonly SortedSet<long> _gaps;
    private bool _stalled;

    public GapWalk(OneIndexOptions options, string entityType, OutboxPosition start, long max, Dictionary<long, long> firstSeen)
    {
        _options = options;
        _entityType = entityType;
        _firstSeen = firstSeen;
        Position = start;
        Cursor = start.Seq;
        Max = max;
        _gaps = [.. start.Gaps];
        long now = Now();
        foreach (long g in _gaps)
        {
            _firstSeen.TryAdd(g, now);
        }
    }

    public OutboxPosition Position { get; private set; }

    /// <summary>The highest position read so far in this walk (at or above the committed mark).</summary>
    public long Cursor { get; private set; }

    public long Max { get; }

    public bool First { get; private set; } = true;

    public bool More => First || Cursor < Max;

    public IReadOnlyList<long> OpenGaps => [.. _gaps];

    private long Now() => _options.TimeProvider.GetUtcNow().UtcTicks;

    private bool Expired(long gap, long now)
        => Max - gap >= _options.LateRowWindow || (_firstSeen.TryGetValue(gap, out long seen) && now - seen >= _options.LateRowAge.Ticks);

    /// <summary>Takes the rows read for this pass: the next window of the log (all entity types) and the rows now visible at open gaps.</summary>
    public WalkStep Advance(List<OutboxEntry> window, List<OutboxEntry> late, long pruned)
    {
        First = false;
        var step = new WalkStep();
        long now = Now();
        long count = Position.Count;
        var apply = new HashSet<string>(StringComparer.Ordinal);

        // Rows that were missing and have appeared.
        foreach (OutboxEntry r in late)
        {
            if (_gaps.Remove(r.Seq))
            {
                _firstSeen.Remove(r.Seq);
                if (r.EntityType == _entityType)
                {
                    count++;
                    if (apply.Add(r.EntityId))
                    {
                        step.Ids.Add(r.EntityId);
                    }
                }
            }
        }

        // Gaps that have waited long enough.
        foreach (long g in _gaps.Where(g => Expired(g, now)).ToList())
        {
            _gaps.Remove(g);
            _firstSeen.Remove(g);
        }

        if (_gaps.Count > 0 && _gaps.Min <= pruned)
        {
            step.Rebuild = true;
            return step;
        }

        long next = window.Count >= _options.BatchSize ? window[^1].Seq : Max;
        if (next < Cursor)
        {
            next = Cursor;
        }

        long settled = Position.Seq;
        var kept = new List<long>(_gaps);
        if (!_stalled)
        {
            var present = new HashSet<long>();
            foreach (OutboxEntry r in window)
            {
                present.Add(r.Seq);
            }

            settled = next;
            for (long g = Math.Max(Cursor + 1, Max - _options.LateRowWindow + 1); g <= next; g++)
            {
                if (present.Contains(g))
                {
                    continue;
                }

                _firstSeen.TryAdd(g, now);
                if (Expired(g, now))
                {
                    continue;
                }

                kept.Add(g);
                if (OutboxPosition.SizeOf(next, kept) > OutboxPosition.MaxSize)
                {
                    kept.RemoveAt(kept.Count - 1);
                    settled = g - 1;
                    _stalled = true;
                    break;
                }
            }

            foreach (OutboxEntry r in window)
            {
                if (r.EntityType == _entityType && r.Seq <= settled)
                {
                    count++;
                }
            }
        }

        foreach (OutboxEntry r in window)
        {
            if (r.EntityType == _entityType && apply.Add(r.EntityId))
            {
                step.Ids.Add(r.EntityId);
            }
        }

        Cursor = next;
        if (!_stalled || settled > Position.Seq)
        {
            _gaps.Clear();
            foreach (long g in kept)
            {
                _gaps.Add(g);
            }
        }

        foreach (long g in _firstSeen.Keys.Where(g => g <= settled && !_gaps.Contains(g)).ToList())
        {
            _firstSeen.Remove(g);
        }

        step.Position = Position with { Seq = settled, Count = count, Gaps = [.. _gaps] };
        Position = step.Position;
        return step;
    }
}
