using System.Globalization;
using System.Text;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Aggregations;

// The execution tree. An AggNode holds the state of one aggregation across all segments (and, for sub-aggregations, of one bucket);
// an AggLeaf is its per-segment collector, built when the segment starts, fed matching documents in increasing order and finished once.
// Segment-local keyword ordinals are never compared across segments: they are turned into the keyword at the end of the segment.

internal abstract class AggLeaf
{
    public abstract void Collect(int doc);

    public virtual void Finish()
    {
    }
}

internal sealed class NoOpLeaf : AggLeaf
{
    public static readonly NoOpLeaf Instance = new();

    public override void Collect(int doc)
    {
    }
}

internal abstract class AggNode
{
    public abstract AggLeaf StartLeaf(LeafContext leaf);

    public abstract AggregationResult Build();
}

internal sealed class BucketNode(AggNode[] subs)
{
    public long DocCount;

    public AggNode[] Subs { get; } = subs;
}

internal static class SubNodes
{
    public static AggLeaf[] Start(AggNode[] subs, LeafContext leaf)
    {
        var leaves = new AggLeaf[subs.Length];
        for (int i = 0; i < subs.Length; i++)
        {
            leaves[i] = subs[i].StartLeaf(leaf);
        }

        return leaves;
    }

    public static void Collect(AggLeaf[] leaves, int doc)
    {
        for (int i = 0; i < leaves.Length; i++)
        {
            leaves[i].Collect(doc);
        }
    }

    public static void Finish(AggLeaf[] leaves)
    {
        for (int i = 0; i < leaves.Length; i++)
        {
            leaves[i].Finish();
        }
    }

    public static AggregationResultSet Build(AggNode[] subs)
    {
        if (subs.Length == 0)
        {
            return AggregationResultSet.Empty;
        }

        var results = new AggregationResult[subs.Length];
        for (int i = 0; i < subs.Length; i++)
        {
            results[i] = subs[i].Build();
        }

        return new AggregationResultSet(results);
    }
}

internal static class AggNodes
{
    public static AggNode[] CreateAll(IReadOnlyList<Aggregation> aggregations, Schema schema)
    {
        var nodes = new AggNode[aggregations.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Length; i++)
        {
            Aggregation a = aggregations[i] ?? throw new ArgumentException("An aggregation is null.", nameof(aggregations));
            if (!names.Add(a.Name))
            {
                throw new ArgumentException($"Two aggregations are named '{a.Name}'.", nameof(aggregations));
            }

            nodes[i] = Create(a, schema);
        }

        return nodes;
    }

    private static FieldDefinition Field(Aggregation a, Schema schema, params FieldType[] allowed)
    {
        if (!schema.TryGetField(a.Field, out FieldDefinition? field))
        {
            throw new ArgumentException($"Aggregation '{a.Name}': the schema has no field '{a.Field}'.");
        }

        if (!field.Column && !field.Facet)
        {
            throw new ArgumentException($"Aggregation '{a.Name}': field '{a.Field}' keeps no column; mark it Facet, Filterable or Sortable.");
        }

        if (Array.IndexOf(allowed, field.Type) < 0)
        {
            throw new ArgumentException($"Aggregation '{a.Name}' ({a.GetType().Name}) cannot read field '{a.Field}', a {field.Type} field; it needs {string.Join(", ", allowed)}.");
        }

        return field;
    }

    private static Func<AggNode[]> Subs(BucketAggregation a, Schema schema)
    {
        IReadOnlyList<Aggregation> subs = a.SubAggregations ?? [];
        CreateAll(subs, schema); // validate once, now
        return subs.Count == 0 ? static () => [] : () => CreateAll(subs, schema);
    }

    public static AggNode Create(Aggregation a, Schema schema)
    {
        const FieldType Key = FieldType.Keyword;
        switch (a)
        {
            case TermsAggregation t:
            {
                FieldDefinition f = Field(a, schema, Key, FieldType.Bool, FieldType.Integral, FieldType.Fractional, FieldType.Date);
                if (t.Size < 1 || t.MinDocCount < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(a), $"Aggregation '{a.Name}': Size and MinDocCount must be at least 1.");
                }

                Func<AggNode[]> subs = Subs(t, schema);
                return f.Type == Key ? new KeywordTermsNode(t, subs) : new NumericTermsNode(t, subs, f.Type);
            }

            case RangeAggregation r:
            {
                Field(a, schema, FieldType.Integral, FieldType.Fractional, FieldType.Date);
                if (r.Ranges.Count == 0)
                {
                    throw new ArgumentException($"Aggregation '{a.Name}': at least one range is needed.");
                }

                return new RangeNode(r, Subs(r, schema), schema.TryGetField(a.Field, out FieldDefinition? rf) && rf.Type == FieldType.Fractional);
            }

            case HistogramAggregation h:
            {
                FieldDefinition f = Field(a, schema, FieldType.Integral, FieldType.Fractional);
                if (!(h.Interval > 0) || double.IsInfinity(h.Interval) || double.IsNaN(h.Offset) || double.IsInfinity(h.Offset) || h.MinDocCount < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(a), $"Aggregation '{a.Name}': Interval must be finite and greater than 0, Offset finite, MinDocCount not negative.");
                }

                return new HistogramNode(h, Subs(h, schema), new HistogramKeyer(h.Interval, h.Offset, f.Type == FieldType.Fractional), h.MinDocCount);
            }

            case DateHistogramAggregation d:
            {
                FieldDefinition f = Field(a, schema, FieldType.Date);
                return new HistogramNode(d, Subs(d, schema), DateHistogramKeyer.Create(d), d.MinDocCount);
            }

            case StatsAggregation:
            {
                FieldDefinition f = Field(a, schema, FieldType.Integral, FieldType.Fractional, FieldType.Date);
                return new StatsNode(a, f.Type == FieldType.Fractional);
            }

            case CardinalityAggregation c:
            {
                FieldDefinition f = Field(a, schema, Key, FieldType.Bool, FieldType.Integral, FieldType.Fractional, FieldType.Date);
                if (c.Precision is < 4 or > 18)
                {
                    throw new ArgumentOutOfRangeException(nameof(a), $"Aggregation '{a.Name}': Precision must be from 4 to 18.");
                }

                return new CardinalityNode(c, f.Type);
            }

            case PercentilesAggregation p:
            {
                FieldDefinition f = Field(a, schema, FieldType.Integral, FieldType.Fractional, FieldType.Date);
                if (p.Percents.Count == 0 || p.Percents.Any(x => !(x >= 0 && x <= 100)))
                {
                    throw new ArgumentOutOfRangeException(nameof(a), $"Aggregation '{a.Name}': Percents must be a non-empty list of numbers from 0 to 100.");
                }

                if (!(p.Compression >= 20 && p.Compression <= 10_000))
                {
                    throw new ArgumentOutOfRangeException(nameof(a), $"Aggregation '{a.Name}': Compression must be from 20 to 10,000.");
                }

                return new PercentilesNode(p, f.Type == FieldType.Fractional);
            }

            default:
                throw new NotSupportedException($"Unknown aggregation type {a.GetType().Name}.");
        }
    }
}

internal static class KeyText
{
    public static string Number(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    public static string Iso(long ms)
        => ms is >= -62135596800000L and <= 253402300799999L
            ? DateTime.UnixEpoch.AddMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : ms.ToString(CultureInfo.InvariantCulture);

    public static BucketKey ForRaw(FieldType type, long raw)
    {
        switch (type)
        {
            case FieldType.Fractional:
            {
                double d = SortableBits.Decode(raw);
                return new BucketKey(Number(d), 0, d);
            }

            case FieldType.Bool:
                return new BucketKey(raw != 0 ? "true" : "false", raw, raw);
            case FieldType.Date:
                return new BucketKey(Iso(raw), raw, raw);
            default:
                return new BucketKey(raw.ToString(CultureInfo.InvariantCulture), raw, raw);
        }
    }
}

// ---- terms over keywords ----

internal sealed class KeywordTermsNode(TermsAggregation spec, Func<AggNode[]> subs) : AggNode
{
    private readonly Dictionary<string, BucketNode> _buckets = new(StringComparer.Ordinal);

    public bool HasSubs { get; } = spec.SubAggregations.Count > 0;

    public BucketNode Resolve(ReadOnlySpan<byte> term)
    {
        string key = Encoding.UTF8.GetString(term);
        if (!_buckets.TryGetValue(key, out BucketNode? b))
        {
            b = new BucketNode(subs());
            _buckets.Add(key, b);
        }

        return b;
    }

    public override AggLeaf StartLeaf(LeafContext leaf)
    {
        IColumnReader? col = leaf.Reader.GetColumn(spec.Field);
        if (col is null)
        {
            return NoOpLeaf.Instance;
        }

        return new Leaf(this, col as SegmentColumn ?? throw new NotSupportedException("Keyword aggregations need the segment column reader."), leaf);
    }

    public override AggregationResult Build()
    {
        var all = new List<(string Key, BucketNode Node)>(_buckets.Count);
        foreach (KeyValuePair<string, BucketNode> kv in _buckets)
        {
            if (kv.Value.DocCount >= spec.MinDocCount)
            {
                all.Add((kv.Key, kv.Value));
            }
        }

        all.Sort((a, b) => TermsOrdering.Compare(spec.Order, a.Node.DocCount, b.Node.DocCount, string.CompareOrdinal(a.Key, b.Key)));
        int take = Math.Min(spec.Size, all.Count);
        var buckets = new List<Bucket>(take);
        long other = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (i < take)
            {
                buckets.Add(new Bucket(new BucketKey(all[i].Key, 0, double.NaN), all[i].Node.DocCount, SubNodes.Build(all[i].Node.Subs)));
            }
            else
            {
                other += all[i].Node.DocCount;
            }
        }

        return new BucketsResult(spec.Name, buckets, other);
    }

    private sealed class Leaf : AggLeaf
    {
        private readonly KeywordTermsNode _node;
        private readonly SegmentColumn _col;
        private readonly LeafContext _leaf;
        private readonly int[] _counts;
        private readonly int[]? _stamps;
        private readonly BucketNode?[]? _buckets;
        private readonly AggLeaf[]?[]? _subLeaves;
        private bool _finished;

        public Leaf(KeywordTermsNode node, SegmentColumn col, LeafContext leaf)
        {
            _node = node;
            _col = col;
            _leaf = leaf;
            _counts = new int[col.TermCount];
            _stamps = col.IsMultiValued ? new int[col.TermCount] : null;
            if (node.HasSubs)
            {
                _buckets = new BucketNode?[col.TermCount];
                _subLeaves = new AggLeaf[col.TermCount][];
            }
        }

        public override void Collect(int doc)
        {
            int n = _col.ValueCount(doc);
            for (int i = 0; i < n; i++)
            {
                int ord = _col.GetOrdinal(doc, i);
                if (_stamps is not null)
                {
                    // A document counts once per distinct value, however often the value is listed.
                    if (_stamps[ord] == doc + 1)
                    {
                        continue;
                    }

                    _stamps[ord] = doc + 1;
                }

                _counts[ord]++;
                if (_subLeaves is not null)
                {
                    AggLeaf[]? sub = _subLeaves[ord];
                    if (sub is null)
                    {
                        BucketNode b = _node.Resolve(_col.GetTerm(ord));
                        _buckets![ord] = b;
                        sub = SubNodes.Start(b.Subs, _leaf);
                        _subLeaves[ord] = sub;
                    }

                    SubNodes.Collect(sub, doc);
                }
            }
        }

        public override void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            for (int ord = 0; ord < _counts.Length; ord++)
            {
                if (_counts[ord] == 0)
                {
                    continue;
                }

                BucketNode b = _buckets?[ord] ?? _node.Resolve(_col.GetTerm(ord));
                b.DocCount += _counts[ord];
                if (_subLeaves?[ord] is { } sub)
                {
                    SubNodes.Finish(sub);
                }
            }
        }
    }
}

internal static class TermsOrdering
{
    // Count descending breaks ties by key ascending, so the order is total.
    public static int Compare(TermsOrder order, long countA, long countB, int keyComparison)
        => order switch
        {
            TermsOrder.KeyAscending => keyComparison,
            TermsOrder.KeyDescending => -keyComparison,
            _ => countA != countB ? countB.CompareTo(countA) : keyComparison,
        };
}

// ---- numeric buckets: terms over numbers, histograms ----

internal abstract class LongKeyer
{
    public abstract bool TryKey(IColumnReader col, int doc, int index, out long key);

    public abstract BucketKey MakeKey(long key);
}

internal sealed class RawKeyer(FieldType type) : LongKeyer
{
    public override bool TryKey(IColumnReader col, int doc, int index, out long key)
    {
        if (type == FieldType.Fractional)
        {
            double d = col.GetFloat64(doc, index);
            key = SortableBits.Encode(d == 0 ? 0.0 : d); // -0 and +0 are one value
        }
        else
        {
            key = col.GetInt64(doc, index);
        }

        return true;
    }

    public override BucketKey MakeKey(long key) => KeyText.ForRaw(type, key);
}

internal sealed class HistogramKeyer(double interval, double offset, bool isFloat) : LongKeyer
{
    public override bool TryKey(IColumnReader col, int doc, int index, out long key)
    {
        double v = isFloat ? col.GetFloat64(doc, index) : col.GetInt64(doc, index);
        double id = Math.Floor((v - offset) / interval);
        if (!(Math.Abs(id) < 4.6e18)) // NaN, infinities and indexes that would overflow
        {
            key = 0;
            return false;
        }

        key = (long)id;
        return true;
    }

    public override BucketKey MakeKey(long key)
    {
        double k = (key * interval) + offset;
        return new BucketKey(KeyText.Number(k), 0, k);
    }
}

internal sealed class DateHistogramKeyer : LongKeyer
{
    private const long MinMs = -62135596800000L;
    private const long MaxMs = 253402300799999L;
    private const long TicksPerMs = TimeSpan.TicksPerMillisecond;
    private static readonly long EpochTicks = DateTime.UnixEpoch.Ticks;

    private readonly long _length; // 0 for month, quarter and year
    private readonly long _offset;
    private readonly DateInterval _unit;

    private DateHistogramKeyer(long length, long offset, DateInterval unit)
    {
        _length = length;
        _offset = offset;
        _unit = unit;
    }

    public static DateHistogramKeyer Create(DateHistogramAggregation d)
    {
        if ((d.CalendarInterval is null) == (d.FixedInterval is null))
        {
            throw new ArgumentException($"Aggregation '{d.Name}': set exactly one of CalendarInterval and FixedInterval.");
        }

        if (d.FixedInterval is { } fixedInterval)
        {
            long ms = fixedInterval.Ticks / TicksPerMs;
            if (fixedInterval.Ticks % TicksPerMs != 0 || ms < 1 || d.Offset.Ticks % TicksPerMs != 0)
            {
                throw new ArgumentException($"Aggregation '{d.Name}': FixedInterval and Offset must be whole milliseconds, the interval at least one.");
            }

            return new DateHistogramKeyer(ms, d.Offset.Ticks / TicksPerMs, DateInterval.Second);
        }

        if (d.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"Aggregation '{d.Name}': Offset applies to a FixedInterval only.");
        }

        DateInterval unit = d.CalendarInterval!.Value;
        return unit switch
        {
            DateInterval.Second => new DateHistogramKeyer(1000, 0, unit),
            DateInterval.Minute => new DateHistogramKeyer(60_000, 0, unit),
            DateInterval.Hour => new DateHistogramKeyer(3_600_000, 0, unit),
            DateInterval.Day => new DateHistogramKeyer(86_400_000, 0, unit),

            // 1970-01-01 was a Thursday; the Monday before it is three days earlier.
            DateInterval.Week => new DateHistogramKeyer(7 * 86_400_000L, -3 * 86_400_000L, unit),
            DateInterval.Month or DateInterval.Quarter or DateInterval.Year => new DateHistogramKeyer(0, 0, unit),
            _ => throw new ArgumentOutOfRangeException(nameof(d), "Unknown calendar interval."),
        };
    }

    public override bool TryKey(IColumnReader col, int doc, int index, out long key)
    {
        long ms = col.GetInt64(doc, index);
        if (_length != 0)
        {
            long shifted = ms - _offset;
            long q = shifted / _length;
            key = (shifted % _length) < 0 ? q - 1 : q; // floor division
            return true;
        }

        if (ms is < MinMs or > MaxMs)
        {
            key = 0;
            return false;
        }

        DateTime dt = new(EpochTicks + (ms * TicksPerMs), DateTimeKind.Utc);
        key = _unit switch
        {
            DateInterval.Month => (dt.Year * 12L) + dt.Month - 1,
            DateInterval.Quarter => (dt.Year * 4L) + ((dt.Month - 1) / 3),
            _ => dt.Year,
        };
        return true;
    }

    public override BucketKey MakeKey(long key)
    {
        long ms;
        if (_length != 0)
        {
            ms = (key * _length) + _offset;
        }
        else
        {
            DateTime start = _unit switch
            {
                DateInterval.Month => new DateTime((int)(key / 12), (int)(key % 12) + 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DateInterval.Quarter => new DateTime((int)(key / 4), (int)(key % 4 * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc),
                _ => new DateTime((int)key, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            ms = (start.Ticks - EpochTicks) / TicksPerMs;
        }

        return new BucketKey(KeyText.Iso(ms), ms, ms);
    }
}

/// <summary>An open-addressing map from 64-bit keys to dense slot numbers 0, 1, 2, ...; grows by doubling, so a lookup of a known key allocates nothing.</summary>
internal sealed class LongIndex
{
    private long[] _keys = new long[16];
    private int[] _slots = new int[32]; // slot + 1; 0 is empty
    private int _count;

    public int Count => _count;

    public long KeyOf(int slot) => _keys[slot];

    public int GetOrAdd(long key)
    {
        int mask = _slots.Length - 1;
        int h = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 33) & mask;
        while (true)
        {
            int s = _slots[h];
            if (s == 0)
            {
                break;
            }

            if (_keys[s - 1] == key)
            {
                return s - 1;
            }

            h = (h + 1) & mask;
        }

        if (_count == _keys.Length)
        {
            Array.Resize(ref _keys, _count * 2);
        }

        _keys[_count] = key;
        _slots[h] = ++_count;
        if (_count * 2 > _slots.Length)
        {
            Rehash();
        }

        return _count - 1;
    }

    private void Rehash()
    {
        _slots = new int[_slots.Length * 2];
        int mask = _slots.Length - 1;
        for (int slot = 0; slot < _count; slot++)
        {
            int h = (int)(((ulong)_keys[slot] * 0x9E3779B97F4A7C15UL) >> 33) & mask;
            while (_slots[h] != 0)
            {
                h = (h + 1) & mask;
            }

            _slots[h] = slot + 1;
        }
    }
}

internal abstract class LongKeyedNode(BucketAggregation spec, Func<AggNode[]> subs, LongKeyer keyer) : AggNode
{
    protected BucketAggregation Spec { get; } = spec;

    protected Dictionary<long, BucketNode> Buckets { get; } = [];

    public bool HasSubs { get; } = spec.SubAggregations.Count > 0;

    public LongKeyer Keyer { get; } = keyer;

    public BucketNode Resolve(long key)
    {
        if (!Buckets.TryGetValue(key, out BucketNode? b))
        {
            b = NewBucket();
            Buckets.Add(key, b);
        }

        return b;
    }

    protected BucketNode NewBucket() => new(subs());

    public override AggLeaf StartLeaf(LeafContext leaf)
    {
        IColumnReader? col = leaf.Reader.GetColumn(Spec.Field);
        return col is null ? NoOpLeaf.Instance : new Leaf(this, col, leaf);
    }

    private sealed class Leaf : AggLeaf
    {
        private readonly LongKeyedNode _node;
        private readonly IColumnReader _col;
        private readonly LeafContext _leaf;
        private readonly LongIndex _index = new();
        private long[] _counts = new long[16];
        private int[] _stamps = new int[16];
        private AggLeaf[]?[]? _subLeaves;
        private bool _finished;

        public Leaf(LongKeyedNode node, IColumnReader col, LeafContext leaf)
        {
            _node = node;
            _col = col;
            _leaf = leaf;
            if (node.HasSubs)
            {
                _subLeaves = new AggLeaf[16][];
            }
        }

        public override void Collect(int doc)
        {
            int n = _col.ValueCount(doc);
            for (int i = 0; i < n; i++)
            {
                if (!_node.Keyer.TryKey(_col, doc, i, out long key))
                {
                    continue;
                }

                int slot = _index.GetOrAdd(key);
                if (slot == _counts.Length)
                {
                    Array.Resize(ref _counts, slot * 2);
                    Array.Resize(ref _stamps, slot * 2);
                    if (_subLeaves is not null)
                    {
                        Array.Resize(ref _subLeaves, slot * 2);
                    }
                }

                if (_stamps[slot] == doc + 1)
                {
                    continue; // already counted for this document
                }

                _stamps[slot] = doc + 1;
                _counts[slot]++;
                if (_subLeaves is not null)
                {
                    AggLeaf[]? sub = _subLeaves[slot];
                    if (sub is null)
                    {
                        sub = SubNodes.Start(_node.Resolve(key).Subs, _leaf);
                        _subLeaves[slot] = sub;
                    }

                    SubNodes.Collect(sub, doc);
                }
            }
        }

        public override void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            for (int slot = 0; slot < _index.Count; slot++)
            {
                _node.Resolve(_index.KeyOf(slot)).DocCount += _counts[slot];
                if (_subLeaves?[slot] is { } sub)
                {
                    SubNodes.Finish(sub);
                }
            }
        }
    }
}

internal sealed class NumericTermsNode(TermsAggregation spec, Func<AggNode[]> subs, FieldType type) : LongKeyedNode(spec, subs, new RawKeyer(type))
{
    private TermsAggregation Terms => (TermsAggregation)Spec;

    public override AggregationResult Build()
    {
        var all = new List<KeyValuePair<long, BucketNode>>(Buckets.Count);
        foreach (KeyValuePair<long, BucketNode> kv in Buckets)
        {
            if (kv.Value.DocCount >= Terms.MinDocCount)
            {
                all.Add(kv);
            }
        }

        // Raw values order like the numbers they stand for (see SortableBits).
        all.Sort((a, b) => TermsOrdering.Compare(Terms.Order, a.Value.DocCount, b.Value.DocCount, a.Key.CompareTo(b.Key)));
        int take = Math.Min(Terms.Size, all.Count);
        var buckets = new List<Bucket>(take);
        long other = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (i < take)
            {
                buckets.Add(new Bucket(Keyer.MakeKey(all[i].Key), all[i].Value.DocCount, SubNodes.Build(all[i].Value.Subs)));
            }
            else
            {
                other += all[i].Value.DocCount;
            }
        }

        return new BucketsResult(Terms.Name, buckets, other);
    }
}

internal sealed class HistogramNode(BucketAggregation spec, Func<AggNode[]> subs, LongKeyer keyer, long minDocCount)
    : LongKeyedNode(spec, subs, keyer)
{
    public override AggregationResult Build()
    {
        var ids = new List<long>(Buckets.Count);
        foreach (KeyValuePair<long, BucketNode> kv in Buckets)
        {
            ids.Add(kv.Key);
        }

        ids.Sort();
        var buckets = new List<Bucket>();
        if (ids.Count > 0)
        {
            long first = ids[0];
            long last = ids[^1];
            if (minDocCount == 0)
            {
                // The empty buckets between the first and the last used one are part of the answer.
                if ((ulong)(last - first) >= (ulong)AggregationCollector.MaxBuckets)
                {
                    throw new TooManyBucketsException(Spec.Name, (last - first) + 1);
                }

                for (long id = first; id <= last; id++)
                {
                    BucketNode b = Buckets.TryGetValue(id, out BucketNode? existing) ? existing : NewBucket();
                    buckets.Add(new Bucket(Keyer.MakeKey(id), b.DocCount, SubNodes.Build(b.Subs)));
                }
            }
            else
            {
                foreach (long id in ids)
                {
                    BucketNode b = Buckets[id];
                    if (b.DocCount >= minDocCount)
                    {
                        buckets.Add(new Bucket(Keyer.MakeKey(id), b.DocCount, SubNodes.Build(b.Subs)));
                    }
                }

                if (buckets.Count > AggregationCollector.MaxBuckets)
                {
                    throw new TooManyBucketsException(Spec.Name, buckets.Count);
                }
            }
        }

        return new BucketsResult(Spec.Name, buckets, 0);
    }
}

// ---- ranges ----

internal sealed class RangeNode : AggNode
{
    private readonly RangeAggregation _spec;
    private readonly BucketNode[] _buckets;
    private readonly bool _isFloat;

    public RangeNode(RangeAggregation spec, Func<AggNode[]> subs, bool isFloat)
    {
        _spec = spec;
        _isFloat = isFloat;
        _buckets = new BucketNode[spec.Ranges.Count];
        for (int i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new BucketNode(subs());
        }
    }

    public override AggLeaf StartLeaf(LeafContext leaf)
    {
        IColumnReader? col = leaf.Reader.GetColumn(_spec.Field);
        return col is null ? NoOpLeaf.Instance : new Leaf(this, col, leaf);
    }

    public override AggregationResult Build()
    {
        var buckets = new List<Bucket>(_buckets.Length);
        for (int i = 0; i < _buckets.Length; i++)
        {
            AggregationRange r = _spec.Ranges[i];
            string label = r.Key ?? ((r.From is { } f ? KeyText.Number(f) : "*") + "-" + (r.To is { } t ? KeyText.Number(t) : "*"));
            buckets.Add(new Bucket(new BucketKey(label, 0, double.NaN), _buckets[i].DocCount, SubNodes.Build(_buckets[i].Subs), r.From, r.To));
        }

        return new BucketsResult(_spec.Name, buckets, 0);
    }

    private sealed class Leaf : AggLeaf
    {
        private readonly RangeNode _node;
        private readonly IColumnReader _col;
        private readonly double[] _from;
        private readonly double[] _to;
        private readonly bool[] _hasTo;
        private readonly long[] _counts;
        private readonly int[] _stamps;
        private readonly AggLeaf[][] _subLeaves;
        private bool _finished;

        public Leaf(RangeNode node, IColumnReader col, LeafContext leaf)
        {
            _node = node;
            _col = col;
            int n = node._buckets.Length;
            _from = new double[n];
            _to = new double[n];
            _hasTo = new bool[n];
            _counts = new long[n];
            _stamps = new int[n];
            _subLeaves = new AggLeaf[n][];
            for (int i = 0; i < n; i++)
            {
                AggregationRange r = node._spec.Ranges[i];
                _from[i] = r.From ?? double.NegativeInfinity;
                _hasTo[i] = r.To is not null;
                _to[i] = r.To ?? 0;
                _subLeaves[i] = SubNodes.Start(node._buckets[i].Subs, leaf);
            }
        }

        public override void Collect(int doc)
        {
            int n = _col.ValueCount(doc);
            for (int i = 0; i < n; i++)
            {
                double v = _node._isFloat ? _col.GetFloat64(doc, i) : _col.GetInt64(doc, i);
                if (double.IsNaN(v))
                {
                    continue;
                }

                for (int r = 0; r < _counts.Length; r++)
                {
                    if (v >= _from[r] && (!_hasTo[r] || v < _to[r]) && _stamps[r] != doc + 1)
                    {
                        _stamps[r] = doc + 1;
                        _counts[r]++;
                        SubNodes.Collect(_subLeaves[r], doc);
                    }
                }
            }
        }

        public override void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            for (int r = 0; r < _counts.Length; r++)
            {
                _node._buckets[r].DocCount += _counts[r];
                SubNodes.Finish(_subLeaves[r]);
            }
        }
    }
}
