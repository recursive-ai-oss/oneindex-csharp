using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>
/// Keeps the <c>count</c> first documents in a sort order. The order is total and deterministic: the sort keys in turn, then the document's
/// identifier in byte order (or its global document id when the schema has no identifier field), so the same snapshot always gives the
/// same page and an identifier-ordered tie-break survives merges. Every match is visited, so the total is exact; scores are computed only
/// when a <see cref="ScoreSort"/> key is present. With a <see cref="SortCursor"/> only documents that sort strictly after it are kept
/// (Elasticsearch's <c>search_after</c>). Collection allocates only when a document enters the kept set.
/// </summary>
public sealed class TopFieldCollector : Collector
{
    private readonly SortCriterion[] _keys;
    private readonly int _capacity;
    private readonly SortCursor? _after;
    private readonly string? _tieBreakField;

    private readonly long[] _numbers;
    private readonly byte[]?[] _texts;
    private readonly bool[] _missing;
    private readonly byte[]?[] _ids;
    private readonly int[] _docs;
    private readonly float[] _scores;
    private int _size;
    private long _totalHits;

    /// <summary>Creates the collector.</summary>
    /// <param name="keys">The sort keys, at least one and at most 16.</param>
    /// <param name="count">How many documents to keep, at least 1.</param>
    /// <param name="after">Keep only documents after this cursor, or <see langword="null"/> for the first page.</param>
    /// <param name="tieBreakField">The keyword field that identifies documents (the schema's identifier field), used to break ties and by cursors; <see langword="null"/> to break ties by global document id.</param>
    /// <exception cref="ArgumentException">There are no keys or too many, or the cursor was made for a different sort.</exception>
    public TopFieldCollector(IReadOnlyList<SortCriterion> keys, int count, SortCursor? after = null, string? tieBreakField = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (keys.Count is < 1 or > 16)
        {
            throw new ArgumentException("A sort has 1 to 16 keys.", nameof(keys));
        }

        if (after is not null && (after.Fingerprint != SortSpec.Fingerprint(keys) || after.Values.Count != keys.Count))
        {
            throw new ArgumentException("The cursor was made for a different sort.", nameof(after));
        }

        if (after is not null && (after.Id is null) != (tieBreakField is null))
        {
            throw new ArgumentException("The cursor and the search disagree about whether documents have identifiers.", nameof(after));
        }

        _keys = [.. keys];
        _capacity = count;
        _after = after;
        _tieBreakField = tieBreakField;
        _numbers = new long[count * _keys.Length];
        _texts = new byte[count * _keys.Length][];
        _missing = new bool[count * _keys.Length];
        _ids = new byte[count][];
        _docs = new int[count];
        _scores = new float[count];
    }

    /// <inheritdoc/>
    public override ScoreMode ScoreMode => Array.Exists(_keys, k => k is ScoreSort) ? ScoreMode.Complete : ScoreMode.CompleteNoScores;

    /// <inheritdoc/>
    public override LeafCollector GetLeafCollector(LeafContext leaf) => new Leaf(this, leaf);

    /// <summary>The documents kept so far, in sort order.</summary>
    /// <returns>The result; the total counts every match.</returns>
    public TopFieldDocs GetTopDocs()
    {
        int[] order = new int[_size];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => CompareSlots(a, b));
        var hits = new FieldDoc[_size];
        int k = _keys.Length;
        for (int i = 0; i < hits.Length; i++)
        {
            int slot = order[i];
            var values = new SortValue[k];
            for (int j = 0; j < k; j++)
            {
                int at = (slot * k) + j;
                values[j] = _missing[at] ? SortValue.Missing : _texts[at] is { } t ? SortValue.FromText(t) : SortValue.FromNumber(_numbers[at]);
            }

            hits[i] = new FieldDoc(_docs[slot], _scores[slot], values, _ids[slot]);
        }

        return new TopFieldDocs(_totalHits, hits);
    }

    // One key's order of two values: negative when a sorts first. Missing values are placed by the key, not by the direction.
    private static int CompareValue(SortCriterion key, bool missingA, long numberA, ReadOnlySpan<byte> textA, bool missingB, long numberB, ReadOnlySpan<byte> textB, bool isText)
    {
        if (missingA || missingB)
        {
            if (missingA && missingB)
            {
                return 0;
            }

            bool first = key.Missing == MissingValuePlacement.First;
            return missingA ? (first ? -1 : 1) : (first ? 1 : -1);
        }

        int c = isText ? textA.SequenceCompareTo(textB) : numberA.CompareTo(numberB);
        return key.Direction == SortDirection.Ascending ? c : -c;
    }

    private int CompareSlots(int a, int b)
    {
        int k = _keys.Length;
        for (int j = 0; j < k; j++)
        {
            int x = (a * k) + j;
            int y = (b * k) + j;
            int c = CompareValue(_keys[j], _missing[x], _numbers[x], _texts[x], _missing[y], _numbers[y], _texts[y], _texts[x] is not null || _texts[y] is not null);
            if (c != 0)
            {
                return c;
            }
        }

        return _tieBreakField is null ? _docs[a].CompareTo(_docs[b]) : _ids[a].AsSpan().SequenceCompareTo(_ids[b]);
    }

    private void Offer(Leaf leaf, int globalDoc, float score)
    {
        _totalHits++;
        if (_after is not null && !IsAfterCursor(leaf, globalDoc))
        {
            return;
        }

        if (_size < _capacity)
        {
            int slot = _size++;
            Store(leaf, slot, globalDoc, score);
            SiftUp(slot);
            return;
        }

        // The root is the worst kept document; the current one must sort before it to replace it.
        if (CompareToSlot(leaf, globalDoc, 0) >= 0)
        {
            return;
        }

        Store(leaf, 0, globalDoc, score);
        SiftDown(0);
    }

    private void Store(Leaf leaf, int slot, int globalDoc, float score)
    {
        int k = _keys.Length;
        for (int j = 0; j < k; j++)
        {
            int at = (slot * k) + j;
            _missing[at] = leaf.Missing[j];
            _numbers[at] = leaf.Numbers[j];
            _texts[at] = leaf.IsText[j] && !leaf.Missing[j] ? leaf.Text(j).ToArray() : null;
        }

        _docs[slot] = globalDoc;
        _scores[slot] = score;
        _ids[slot] = _tieBreakField is null ? null : leaf.Id.ToArray();
    }

    // The current document against a kept slot: negative when the current one sorts first.
    private int CompareToSlot(Leaf leaf, int globalDoc, int slot)
    {
        int k = _keys.Length;
        for (int j = 0; j < k; j++)
        {
            int y = (slot * k) + j;
            int c = CompareValue(_keys[j], leaf.Missing[j], leaf.Numbers[j], leaf.Text(j), _missing[y], _numbers[y], _texts[y], leaf.IsText[j] || _texts[y] is not null);
            if (c != 0)
            {
                return c;
            }
        }

        return _tieBreakField is null ? globalDoc.CompareTo(_docs[slot]) : leaf.Id.SequenceCompareTo(_ids[slot]);
    }

    private bool IsAfterCursor(Leaf leaf, int globalDoc)
    {
        SortCursor after = _after!;
        int k = _keys.Length;
        for (int j = 0; j < k; j++)
        {
            SortValue v = after.Values[j];
            int c = CompareValue(_keys[j], leaf.Missing[j], leaf.Numbers[j], leaf.Text(j), v.IsMissing, v.Number, v.Text, leaf.IsText[j] || v.IsText);
            if (c != 0)
            {
                return c > 0;
            }
        }

        return _tieBreakField is null ? globalDoc > after.DocId : leaf.Id.SequenceCompareTo(after.Id) > 0;
    }

    // The kept documents form a heap whose root sorts last (the worst kept one).
    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (CompareSlots(i, parent) <= 0)
            {
                break;
            }

            SwapSlots(i, parent);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        int n = _size;
        while (true)
        {
            int left = (2 * i) + 1;
            if (left >= n)
            {
                break;
            }

            int worst = left;
            if (left + 1 < n && CompareSlots(left + 1, left) > 0)
            {
                worst = left + 1;
            }

            if (CompareSlots(worst, i) <= 0)
            {
                break;
            }

            SwapSlots(i, worst);
            i = worst;
        }
    }

    private void SwapSlots(int a, int b)
    {
        int k = _keys.Length;
        for (int j = 0; j < k; j++)
        {
            int x = (a * k) + j;
            int y = (b * k) + j;
            (_numbers[x], _numbers[y]) = (_numbers[y], _numbers[x]);
            (_texts[x], _texts[y]) = (_texts[y], _texts[x]);
            (_missing[x], _missing[y]) = (_missing[y], _missing[x]);
        }

        (_docs[a], _docs[b]) = (_docs[b], _docs[a]);
        (_scores[a], _scores[b]) = (_scores[b], _scores[a]);
        (_ids[a], _ids[b]) = (_ids[b], _ids[a]);
    }

    private sealed class Leaf : LeafCollector
    {
        private readonly TopFieldCollector _owner;
        private readonly int _docBase;
        private readonly KeyReader?[] _readers;
        private readonly IColumnReader? _idColumn;
        private byte[] _id = new byte[32];
        private int _idLength;
        private Scorer? _scorer;

        public Leaf(TopFieldCollector owner, LeafContext leaf)
        {
            _owner = owner;
            _docBase = leaf.DocBase;
            int k = owner._keys.Length;
            Missing = new bool[k];
            Numbers = new long[k];
            IsText = new bool[k];
            _readers = new KeyReader?[k];
            for (int j = 0; j < k; j++)
            {
                _readers[j] = KeyReader.Create(owner._keys[j], leaf.Reader);
                IsText[j] = _readers[j] is KeywordReader;
            }

            if (owner._tieBreakField is { } idField)
            {
                _idColumn = leaf.Reader.GetColumn(idField)
                    ?? throw new QueryException($"The identifier field '{idField}' keeps no column in a segment, so documents cannot be tie-broken by identifier.");
            }
        }

        public bool[] Missing { get; }

        public long[] Numbers { get; }

        public bool[] IsText { get; }

        public ReadOnlySpan<byte> Id => _id.AsSpan(0, _idLength);

        public ReadOnlySpan<byte> Text(int key) => _readers[key] is KeywordReader r ? r.Text.AsSpan(0, r.Length) : default;

        public override void SetScorer(Scorer scorer) => _scorer = scorer;

        public override void Collect(int docId)
        {
            float score = 0;
            for (int j = 0; j < _readers.Length; j++)
            {
                if (_owner._keys[j] is ScoreSort)
                {
                    score = _scorer!.Score();
                    double s = float.IsNaN(score) ? double.NegativeInfinity : score;
                    Numbers[j] = SortableBits.Encode(s + 0.0);
                    Missing[j] = false;
                }
                else if (_readers[j] is { } r)
                {
                    Missing[j] = !r.Read(docId, _owner._keys[j].Direction == SortDirection.Ascending, out Numbers[j]);
                }
                else
                {
                    Missing[j] = true; // the segment has no values for the field
                }
            }

            if (_idColumn is not null)
            {
                ReadOnlySpan<byte> id = _idColumn.GetKeyword(docId);
                if (id.Length > _id.Length)
                {
                    _id = new byte[Math.Max(id.Length, _id.Length * 2)];
                }

                id.CopyTo(_id);
                _idLength = id.Length;
            }

            _owner.Offer(this, _docBase + docId, score);
        }
    }

    // Reads one key's value of a document from a segment's column. Keyword text is copied into a buffer the reader owns, so nothing
    // allocates per document and the value stays valid while the collector compares it.
    private abstract class KeyReader
    {
        public static KeyReader? Create(SortCriterion key, ISegmentReader segment)
        {
            switch (key)
            {
                case ScoreSort:
                    return null;
                case FieldSort f:
                    IColumnReader? column = segment.GetColumn(f.Field);
                    return column is null ? null : column.Type switch
                    {
                        FieldType.Keyword => new KeywordReader(column),
                        FieldType.Integral or FieldType.Date or FieldType.Bool => new IntegerReader(column),
                        FieldType.Fractional => new FractionalReader(column),
                        _ => throw new QueryException($"Cannot sort by '{f.Field}': it is {column.Type}."),
                    };
                case GeoDistanceSort g:
                    IColumnReader? geo = segment.GetColumn(g.Field);
                    return geo is null ? null : geo.Type == FieldType.GeoPoint ? new DistanceReader(geo, g.Latitude, g.Longitude) : throw new QueryException($"Cannot sort by distance on '{g.Field}': it is {geo.Type}, not a geo point.");
                default:
                    throw new QueryException($"Unknown sort key {key.GetType().Name}.");
            }
        }

        public abstract bool Read(int doc, bool ascending, out long number);
    }

    private sealed class IntegerReader(IColumnReader column) : KeyReader
    {
        public override bool Read(int doc, bool ascending, out long number)
        {
            number = 0;
            int n = column.ValueCount(doc);
            if (n == 0)
            {
                return false;
            }

            number = column.GetInt64(doc, 0);
            for (int i = 1; i < n; i++)
            {
                long v = column.GetInt64(doc, i);
                number = ascending ? Math.Min(number, v) : Math.Max(number, v);
            }

            return true;
        }
    }

    private sealed class FractionalReader(IColumnReader column) : KeyReader
    {
        public override bool Read(int doc, bool ascending, out long number)
        {
            number = 0;
            int n = column.ValueCount(doc);
            if (n == 0)
            {
                return false;
            }

            // + 0.0 turns -0.0 into 0.0, so the two zeros tie as they do in a numeric comparison.
            number = SortableBits.Encode(column.GetFloat64(doc, 0) + 0.0);
            for (int i = 1; i < n; i++)
            {
                long v = SortableBits.Encode(column.GetFloat64(doc, i) + 0.0);
                number = ascending ? Math.Min(number, v) : Math.Max(number, v);
            }

            return true;
        }
    }

    private sealed class DistanceReader(IColumnReader column, double latitude, double longitude) : KeyReader
    {
        public override bool Read(int doc, bool ascending, out long number)
        {
            number = 0;
            int n = column.ValueCount(doc);
            if (n == 0)
            {
                return false;
            }

            double best = double.NaN;
            for (int i = 0; i < n; i++)
            {
                (double la, double lo) = column.GetGeoPoint(doc, i);
                double d = GeoMath.HaversineMeters(latitude, longitude, la, lo);
                if (i == 0 || (ascending ? d < best : d > best))
                {
                    best = d;
                }
            }

            number = SortableBits.Encode(best + 0.0);
            return true;
        }
    }

    private sealed class KeywordReader(IColumnReader column) : KeyReader
    {
        public byte[] Text { get; private set; } = new byte[32];

        public int Length { get; private set; }

        public override bool Read(int doc, bool ascending, out long number)
        {
            number = 0;
            int n = column.ValueCount(doc);
            if (n == 0)
            {
                return false;
            }

            ReadOnlySpan<byte> best = column.GetKeyword(doc, 0);
            for (int i = 1; i < n; i++)
            {
                ReadOnlySpan<byte> v = column.GetKeyword(doc, i);
                int c = v.SequenceCompareTo(best);
                if (ascending ? c < 0 : c > 0)
                {
                    best = v;
                }
            }

            if (best.Length > Text.Length)
            {
                Text = new byte[Math.Max(best.Length, Text.Length * 2)];
            }

            best.CopyTo(Text);
            Length = best.Length;
            return true;
        }
    }
}
