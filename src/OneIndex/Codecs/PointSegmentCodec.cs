using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// The point codec: owns the <c>points</c> part, a one-dimensional index of (value, document) pairs for every indexed numeric,
/// date and bool field (docs/points.md). Pairs are sorted by value and cut into leaves of at most 512; a resident directory of
/// leaf minima and maxima finds the leaves a range touches, and a leaf is read from storage only when a query needs it. The tree is
/// the degenerate one-dimensional case of a block KD-tree, so a second dimension for geo adds splits without changing the leaves.
/// </summary>
public sealed class PointSegmentCodec : ISegmentCodec
{
    internal static readonly uint Magic = IndexFile.Magic("OIXR");

    /// <inheritdoc/>
    public string Name => "points";

    /// <inheritdoc/>
    public IReadOnlyList<string> Parts { get; } = [SegmentParts.Points];

    /// <inheritdoc/>
    public void Flush(SegmentFlushContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        List<FieldDefinition> fields = PointFields(context.Schema);
        if (fields.Count == 0 || context.Documents.Count == 0)
        {
            return;
        }

        var entries = new Dictionary<string, PointEntries>(StringComparer.Ordinal);
        foreach (FieldDefinition f in fields)
        {
            entries[f.Name] = new PointEntries(f.Type == FieldType.GeoPoint);
        }

        for (int d = 0; d < context.Documents.Count; d++)
        {
            if ((d & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (DocumentField v in context.Documents[d].Fields)
            {
                if (entries.TryGetValue(v.Name, out PointEntries? e))
                {
                    context.Schema.TryGetField(v.Name, out FieldDefinition? def);
                    if (def!.Type == FieldType.GeoPoint)
                    {
                        (double lat, double lon) = v.Value.AsGeoPoint();
                        e.Add(GeoKey.Interleave(GeoKey.QuantizeLatitude(lat), GeoKey.QuantizeLongitude(lon)), d);
                    }
                    else
                    {
                        e.Add(NumericTerms.Ordered(def.Type, v.Value), d);
                    }
                }
            }
        }

        Write(context.Documents.Count, fields.Select(f => (f.Name, entries[f.Name])), sink, cancellationToken);
    }

    /// <inheritdoc/>
    public void Merge(SegmentMergeContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        List<FieldDefinition> fields = PointFields(context.Schema);
        if (fields.Count == 0 || context.DocumentCount == 0)
        {
            return;
        }

        var all = new List<(string, PointEntries)>();
        long[] values = new long[PointFormat.LeafEntries];
        int[] docs = new int[PointFormat.LeafEntries];
        foreach (FieldDefinition f in fields)
        {
            var e = new PointEntries(f.Type == FieldType.GeoPoint);
            for (int s = 0; s < context.Sources.Count; s++)
            {
                MergeSource source = context.Sources[s];
                if (source.Reader is not PointPartReader part || part.GetPoints(f.Name) is not SegmentPoints points)
                {
                    continue;
                }

                for (int leaf = 0; leaf < points.LeafCount; leaf++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = points.ReadLeaf(leaf, values, docs);
                    for (int i = 0; i < n; i++)
                    {
                        int newDoc = docs[i] < source.MaxDoc ? source.DocMap[docs[i]] : throw new CorruptIndexException(points.FileName, "a point names a document outside the segment");
                        if (newDoc >= 0)
                        {
                            e.Add(values[i], newDoc);
                        }
                    }
                }
            }

            all.Add((f.Name, e));
        }

        Write(context.DocumentCount, all, sink, cancellationToken);
    }

    /// <inheritdoc/>
    public ISegmentPartReader? OpenReader(SegmentReaderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IRangeReader? reader = context.OpenPart(SegmentParts.Points, out _, cancellationToken);
        return reader is null ? null : PointPartReader.Open(reader, context.Info.DocCount, context.Limits, cancellationToken);
    }

    private static List<FieldDefinition> PointFields(Schema schema)
    {
        var fields = schema.Fields.Where(f => f.Indexed && (NumericTerms.IsNumeric(f.Type) || f.Type == FieldType.GeoPoint)).ToList();
        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields;
    }

    private static void Write(int maxDoc, IEnumerable<(string Name, PointEntries Entries)> fields, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        var present = fields.Where(f => f.Entries.Count > 0).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        if (present.Count == 0)
        {
            return;
        }

        using SegmentFileBuilder file = sink.Create(SegmentParts.Points, "OIXR", CodecRegistry.NewestVersion(SegmentParts.Points), cancellationToken);
        var head = new ByteBuilder(16);
        head.WriteUInt32((uint)maxDoc);
        file.Write(head.Span, cancellationToken);
        var directory = new ByteBuilder(256);
        directory.WriteVarUInt((uint)present.Count);
        var tree = new ByteBuilder(1 << 16);
        foreach ((string name, PointEntries entries) in present)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Sort();
            tree.Clear();
            entries.WriteTree(tree);
            long start = file.Position;
            file.Write(tree.Span, cancellationToken);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            directory.WriteVarUInt((uint)nameBytes.Length);
            directory.Write(nameBytes);
            directory.WriteByte((byte)(entries.Geo ? 1 : 0));
            directory.WriteVarUInt((uint)entries.Count);
            directory.WriteUInt64((ulong)entries.MinValue);
            directory.WriteUInt64((ulong)entries.MaxValue);
            directory.WriteVarUInt((ulong)start);
            directory.WriteVarUInt((ulong)tree.Length);
        }

        long directoryAt = file.Position;
        file.Write(directory.Span, cancellationToken);
        var trailer = new ByteBuilder(8);
        trailer.WriteUInt64((ulong)directoryAt);
        file.Write(trailer.Span, cancellationToken);
        file.Complete(cancellationToken);
    }
}

internal static class PointFormat
{
    public const int LeafEntries = 512;
    public const int RecordSize = 32;
    public const int TreeHeader = 8;
}

/// <summary>The (value, document) pairs of one field while a segment is written.</summary>
internal sealed class PointEntries
{
    private (long Value, int Doc)[] _items = new (long, int)[64];
    private int _count;

    public PointEntries(bool geo) => Geo = geo;

    /// <summary>Whether the values are geo keys (<see cref="GeoKey"/>), which also record each leaf's bounding cells.</summary>
    public bool Geo { get; }

    public int Count => _count;

    public long MinValue => _items[0].Value;

    public long MaxValue => _items[_count - 1].Value;

    public void Add(long value, int doc)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[_count++] = (value, doc);
    }

    /// <summary>Sorts by value, then document, so equal inputs give equal bytes however the pairs were gathered.</summary>
    public void Sort() => Array.Sort(_items, 0, _count, Comparer.Instance);

    public void WriteTree(ByteBuilder destination)
    {
        int leaves = (_count + PointFormat.LeafEntries - 1) / PointFormat.LeafEntries;
        var records = new byte[leaves * PointFormat.RecordSize];
        var data = new ByteBuilder(_count * 4);
        byte[] pack = new byte[(PointFormat.LeafEntries * 8) + 8];
        for (int l = 0; l < leaves; l++)
        {
            int from = l * PointFormat.LeafEntries;
            int n = Math.Min(PointFormat.LeafEntries, _count - from);
            long min = _items[from].Value;
            long max = _items[from + n - 1].Value;
            int maxLeafDoc = 0;
            for (int i = 0; i < n; i++)
            {
                maxLeafDoc = Math.Max(maxLeafDoc, _items[from + i].Doc);
            }

            int vw = IntStreamFormat.BitsFor(unchecked((ulong)(max - min)));
            int dw = IntStreamFormat.BitsFor((ulong)maxLeafDoc);
            Span<byte> r = records.AsSpan(l * PointFormat.RecordSize, PointFormat.RecordSize);
            BinaryPrimitives.WriteInt64LittleEndian(r, min);
            BinaryPrimitives.WriteInt64LittleEndian(r[8..], max);
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(r[20..], (ushort)n);
            r[22] = (byte)vw;
            r[23] = (byte)dw;
            if (Geo)
            {
                WriteLeafBox(r[24..], from, n);
            }

            int vBytes = (int)IntStreamFormat.PackedBytes(n, vw);
            Array.Clear(pack, 0, vBytes + 8);
            for (int i = 0; i < n; i++)
            {
                PackedBits.Put(pack, i, vw, unchecked((ulong)(_items[from + i].Value - min)));
            }

            data.Write(pack.AsSpan(0, vBytes));
            int dBytes = (int)IntStreamFormat.PackedBytes(n, dw);
            Array.Clear(pack, 0, dBytes + 8);
            for (int i = 0; i < n; i++)
            {
                PackedBits.Put(pack, i, dw, (ulong)_items[from + i].Doc);
            }

            data.Write(pack.AsSpan(0, dBytes));
        }

        destination.WriteUInt32((uint)leaves);
        destination.WriteUInt32((uint)data.Length);
        destination.Write(records);
        destination.Write(data.Span);
    }

    // The leaf's bounding cells (top 16 bits of each coordinate quantum), four little-endian u16: latitude low and high, longitude low and high.
    private void WriteLeafBox(Span<byte> destination, int from, int n)
    {
        long latLo = long.MaxValue;
        long latHi = 0;
        long lngLo = long.MaxValue;
        long lngHi = 0;
        for (int i = 0; i < n; i++)
        {
            GeoKey.Deinterleave(_items[from + i].Value, out long lat, out long lng);
            latLo = Math.Min(latLo, lat);
            latHi = Math.Max(latHi, lat);
            lngLo = Math.Min(lngLo, lng);
            lngHi = Math.Max(lngHi, lng);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)(latLo >> GeoKey.CellShift));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)(latHi >> GeoKey.CellShift));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], (ushort)(lngLo >> GeoKey.CellShift));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)(lngHi >> GeoKey.CellShift));
    }

    private sealed class Comparer : IComparer<(long Value, int Doc)>
    {
        public static readonly Comparer Instance = new();

        public int Compare((long Value, int Doc) x, (long Value, int Doc) y)
            => x.Value != y.Value ? x.Value.CompareTo(y.Value) : x.Doc.CompareTo(y.Doc);
    }
}

/// <summary>A segment's point indexes, opened. Implements <see cref="IPointSource"/>.</summary>
internal sealed class PointPartReader : ISegmentPartReader, IPointSource
{
    private readonly Dictionary<string, SegmentPoints> _fields = new(StringComparer.Ordinal);
    private IRangeReader? _file;

    public static PointPartReader Open(IRangeReader file, int maxDoc, IndexLimits limits, CancellationToken cancellationToken)
    {
        var reader = new PointPartReader { _file = file };
        try
        {
            reader.Load(file, maxDoc, limits, cancellationToken);
            return reader;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public IEnumerable<string> Names => _fields.Keys;

    public IPointIndexReader? GetPoints(string field) => _fields.GetValueOrDefault(field);

    public void Dispose() => _file?.Dispose();

    private void Load(IRangeReader file, int maxDoc, IndexLimits limits, CancellationToken cancellationToken)
    {
        string name = file.Name;
        long length = file.Length;
        const int minimum = IndexFile.HeaderLength + 4 + 1 + 8 + IndexFile.FooterLength;
        if (length < minimum)
        {
            throw new CorruptIndexException(name, $"{length} bytes is too short for a point index");
        }

        IndexFile.ReadFooterChecksum(file, cancellationToken);
        Span<byte> head = stackalloc byte[IndexFile.HeaderLength + 4]; // fixed 12-byte buffer
        file.ReadExactly(0, head, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != PointSegmentCodec.Magic)
        {
            throw new CorruptIndexException(name, "wrong magic: not a point index");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        if (version == 0 || version > (uint)CodecRegistry.NewestVersion(SegmentParts.Points))
        {
            throw new UnsupportedFormatException(name, $"format version {version} is not supported");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != (uint)maxDoc)
        {
            throw new CorruptIndexException(name, "the point index covers a different number of documents than the commit says");
        }

        long trailerAt = length - IndexFile.FooterLength - 8;
        Span<byte> trailer = stackalloc byte[8]; // fixed 8-byte buffer
        file.ReadExactly(trailerAt, trailer, cancellationToken);
        ulong directoryAt = BinaryPrimitives.ReadUInt64LittleEndian(trailer);
        long sectionsStart = IndexFile.HeaderLength + 4;
        if (directoryAt < (ulong)sectionsStart || directoryAt >= (ulong)trailerAt)
        {
            throw new CorruptIndexException(name, "the directory offset lies outside the file");
        }

        long directoryBytes = trailerAt - (long)directoryAt;
        if (directoryBytes > (long)limits.MaxFields * (limits.MaxStringBytes + 64L) + 16)
        {
            throw new CorruptIndexException(name, "the directory is larger than the limits allow");
        }

        var directory = new byte[directoryBytes];
        file.ReadExactly((long)directoryAt, directory, cancellationToken);
        var cursor = new ByteCursor(name, directory, 0, directory.Length);
        int count = ResidentFile.ReadCount(name, ref cursor, limits.MaxFields, "field count");
        string? previous = null;
        long expected = sectionsStart;
        for (int i = 0; i < count; i++)
        {
            int nameLength = ResidentFile.ReadCount(name, ref cursor, limits.MaxStringBytes, "field name length");
            string field;
            try
            {
                field = new UTF8Encoding(false, true).GetString(cursor.ReadBytes(nameLength));
            }
            catch (DecoderFallbackException)
            {
                throw new CorruptIndexException(name, "a field name is not valid UTF-8");
            }

            if (previous is not null && string.CompareOrdinal(previous, field) >= 0)
            {
                throw new CorruptIndexException(name, "field names are not in strictly increasing order");
            }

            previous = field;
            bool geo = false;
            if (version >= 2)
            {
                byte kind = cursor.ReadByte();
                if (kind > 1)
                {
                    throw new CorruptIndexException(name, $"field '{field}' has an unknown tree kind {kind}");
                }

                geo = kind == 1;
            }

            ulong entries = cursor.ReadVarUInt();
            long min = cursor.ReadInt64();
            long max = cursor.ReadInt64();
            ulong start = cursor.ReadVarUInt();
            ulong size = cursor.ReadVarUInt();
            if (entries == 0 || entries > (ulong)limits.MaxDocsPerSegment * 64UL || start != (ulong)expected || size > directoryAt - start || min > max)
            {
                throw new CorruptIndexException(name, $"field '{field}' has an invalid directory entry");
            }

            expected = (long)(start + size);
            _fields[field] = SegmentPoints.Open(file, field, geo, maxDoc, (long)start, (long)size, (long)entries, min, max, cancellationToken);
        }

        if (expected != (long)directoryAt || cursor.Remaining != 0)
        {
            throw new CorruptIndexException(name, "the trees and directory do not tile the file");
        }
    }
}

/// <summary>
/// The point index of one field in one segment. <see cref="GetRange"/> finds the leaves a range touches in the resident directory,
/// reads each from storage and returns the matching documents in increasing order. A document with several values in the range is
/// returned once. Safe for concurrent use.
/// </summary>
public sealed class SegmentPoints : IGeoPointIndexReader
{
    private readonly IRangeReader _file;
    private readonly int _maxDoc;
    private readonly long _dataStart;
    private readonly byte[] _records;
    private readonly int _leafCount;

    private SegmentPoints(IRangeReader file, bool geo, int maxDoc, long dataStart, byte[] records, int leafCount, long entries, long min, long max)
    {
        IsGeo = geo;
        _file = file;
        _maxDoc = maxDoc;
        _dataStart = dataStart;
        _records = records;
        _leafCount = leafCount;
        EntryCount = entries;
        MinValue = min;
        MaxValue = max;
    }

    /// <summary>The number of (value, document) pairs.</summary>
    public long EntryCount { get; }

    /// <summary>The smallest indexed value (ordered integer form).</summary>
    public long MinValue { get; }

    /// <summary>The largest indexed value.</summary>
    public long MaxValue { get; }

    /// <summary>Whether the values are geo keys rather than ordered numbers.</summary>
    public bool IsGeo { get; }

    internal int LeafCount => _leafCount;

    internal string FileName => _file.Name;

    internal static SegmentPoints Open(IRangeReader file, string field, bool geo, int maxDoc, long start, long size, long entries, long min, long max, CancellationToken cancellationToken)
    {
        string name = file.Name;
        if (size < PointFormat.TreeHeader)
        {
            throw new CorruptIndexException(name, $"the tree of '{field}' is shorter than its header");
        }

        Span<byte> h = stackalloc byte[PointFormat.TreeHeader]; // fixed 8-byte buffer
        file.ReadExactly(start, h, cancellationToken);
        uint leaves = BinaryPrimitives.ReadUInt32LittleEndian(h);
        uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        long recordBytes = (long)leaves * PointFormat.RecordSize;
        if (leaves == 0 || PointFormat.TreeHeader + recordBytes + dataLength != size || (long)leaves * PointFormat.LeafEntries < entries)
        {
            throw new CorruptIndexException(name, $"the tree of '{field}' has inconsistent sizes");
        }

        var records = new byte[recordBytes];
        file.ReadExactly(start + PointFormat.TreeHeader, records, cancellationToken);
        long total = 0;
        long dataOffset = 0;
        long previousMax = long.MinValue;
        for (int l = 0; l < (int)leaves; l++)
        {
            ReadOnlySpan<byte> r = records.AsSpan(l * PointFormat.RecordSize, PointFormat.RecordSize);
            long lmin = BinaryPrimitives.ReadInt64LittleEndian(r);
            long lmax = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
            uint at = BinaryPrimitives.ReadUInt32LittleEndian(r[16..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(r[20..]);
            int vw = r[22];
            int dw = r[23];
            if (n is < 1 or > PointFormat.LeafEntries || vw > 64 || dw > 31 || lmin > lmax || lmin < previousMax || at != dataOffset)
            {
                throw new CorruptIndexException(name, $"leaf {l} of '{field}' has an invalid record");
            }

            // The width must be exactly what the range needs, so a leaf cannot claim more data than it holds.
            if (vw != IntStreamFormat.BitsFor(unchecked((ulong)(lmax - lmin))))
            {
                throw new CorruptIndexException(name, $"leaf {l} of '{field}' has a value width that disagrees with its range");
            }

            if (geo)
            {
                if (lmax > ((1L << (2 * GeoKey.Bits)) - 1) || lmin < 0 || LeafBox(r).LatLo > LeafBox(r).LatHi || LeafBox(r).LngLo > LeafBox(r).LngHi)
                {
                    throw new CorruptIndexException(name, $"leaf {l} of '{field}' has an invalid geo key or bounding box");
                }
            }
            else if (r[24..].ContainsAnyExcept((byte)0))
            {
                throw new CorruptIndexException(name, $"leaf {l} of '{field}' has nonzero reserved bytes");
            }

            dataOffset += IntStreamFormat.PackedBytes(n, vw) + IntStreamFormat.PackedBytes(n, dw);
            previousMax = lmax;
            total += n;
        }

        if (dataOffset != dataLength || total != entries || BinaryPrimitives.ReadInt64LittleEndian(records) != min || BinaryPrimitives.ReadInt64LittleEndian(records.AsSpan(((int)leaves - 1) * PointFormat.RecordSize + 8)) != max)
        {
            throw new CorruptIndexException(name, $"the leaves of '{field}' do not match its directory entry");
        }

        return new SegmentPoints(file, geo, maxDoc, start + PointFormat.TreeHeader + recordBytes, records, (int)leaves, entries, min, max);
    }

    /// <inheritdoc/>
    public DocIdSetIterator GetRange(long lower, long upper)
    {
        if (lower > upper || upper < MinValue || lower > MaxValue)
        {
            return new SortedDocIterator([], 0, null);
        }

        int first = FirstLeafWithMaxAtLeast(lower);
        long bound = 0;
        int last = first;
        for (int l = first; l < _leafCount && LeafMin(l) <= upper; l++)
        {
            bound += LeafCount_(l);
            last = l;
        }

        if (bound == 0)
        {
            return new SortedDocIterator([], 0, null);
        }

        // A bitmap costs maxDoc/8 bytes whatever matches; a list costs 4 bytes per match. Use the smaller.
        bool bitmap = bound * 32 >= _maxDoc;
        ulong[]? words = bitmap ? new ulong[(_maxDoc + 63) >> 6] : null;
        int[] list = bitmap ? [] : new int[bound];
        int n = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent((PointFormat.LeafEntries * 16) + 16);
        try
        {
            for (int l = first; l <= last; l++)
            {
                ReadOnlySpan<byte> r = _records.AsSpan(l * PointFormat.RecordSize, PointFormat.RecordSize);
                long lmin = BinaryPrimitives.ReadInt64LittleEndian(r);
                long lmax = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(r[20..]);
                int vw = r[22];
                int dw = r[23];
                int vBytes = (int)IntStreamFormat.PackedBytes(count, vw);
                int dBytes = (int)IntStreamFormat.PackedBytes(count, dw);
                bool inside = lmin >= lower && lmax <= upper;
                _file.ReadExactly(_dataStart + BinaryPrimitives.ReadUInt32LittleEndian(r[16..]), buffer.AsSpan(0, vBytes + dBytes));
                buffer.AsSpan(vBytes + dBytes, 16).Clear();
                ReadOnlySpan<byte> values = buffer.AsSpan(0, vBytes + 8);
                ReadOnlySpan<byte> docs = buffer.AsSpan(vBytes, dBytes + 8);
                for (int i = 0; i < count; i++)
                {
                    if (!inside)
                    {
                        long v = unchecked(lmin + (long)PackedBits.Get(values, i, vw));
                        if (v < lower || v > upper)
                        {
                            continue;
                        }
                    }

                    int doc = (int)PackedBits.Get(docs, i, dw);
                    if (doc >= _maxDoc)
                    {
                        throw new CorruptIndexException(_file.Name, "a point names a document outside the segment");
                    }

                    if (words is not null)
                    {
                        words[doc >> 6] |= 1UL << (doc & 63);
                    }
                    else
                    {
                        list[n++] = doc;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new SortedDocIterator(list, n, words);
    }

    // The leaf's bounding cells: the top 16 bits of the latitude and longitude quanta of its points.
    private static GeoBox LeafBox(ReadOnlySpan<byte> record)
        => new(BinaryPrimitives.ReadUInt16LittleEndian(record[24..]), BinaryPrimitives.ReadUInt16LittleEndian(record[26..]), BinaryPrimitives.ReadUInt16LittleEndian(record[28..]), BinaryPrimitives.ReadUInt16LittleEndian(record[30..]));

    /// <inheritdoc/>
    public DocIdSetIterator GetCandidates(double south, double north, double west, double east)
    {
        if (!IsGeo)
        {
            throw new InvalidOperationException($"'{_file.Name}' holds numeric points, not geo points.");
        }

        var boxes = new List<GeoBox>(2);
        GeoBox.FromDegrees(south, north, west, east, boxes);
        long bound = 0;
        for (int l = 0; l < _leafCount; l++)
        {
            if (Touches(l, boxes))
            {
                bound += LeafCount_(l);
            }
        }

        if (bound == 0)
        {
            return new SortedDocIterator([], 0, null);
        }

        bool bitmap = bound * 32 >= _maxDoc;
        ulong[]? words = bitmap ? new ulong[(_maxDoc + 63) >> 6] : null;
        int[] list = bitmap ? [] : new int[bound];
        int n = 0;
        long[] values = ArrayPool<long>.Shared.Rent(PointFormat.LeafEntries);
        int[] docs = ArrayPool<int>.Shared.Rent(PointFormat.LeafEntries);
        try
        {
            for (int l = 0; l < _leafCount; l++)
            {
                if (!Touches(l, boxes))
                {
                    continue;
                }

                int count = ReadLeaf(l, values, docs);
                bool inside = Inside(l, boxes);
                for (int i = 0; i < count; i++)
                {
                    if (!inside)
                    {
                        GeoKey.Deinterleave(values[i], out long lat, out long lng);
                        if (!AnyContains(boxes, lat, lng))
                        {
                            continue;
                        }
                    }

                    int doc = docs[i];
                    if (doc >= _maxDoc)
                    {
                        throw new CorruptIndexException(_file.Name, "a point names a document outside the segment");
                    }

                    if (words is not null)
                    {
                        words[doc >> 6] |= 1UL << (doc & 63);
                    }
                    else
                    {
                        list[n++] = doc;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(values);
            ArrayPool<int>.Shared.Return(docs);
        }

        return new SortedDocIterator(list, n, words);
    }

    private static bool AnyContains(List<GeoBox> boxes, long lat, long lng)
    {
        foreach (GeoBox b in boxes)
        {
            if (b.Contains(lat, lng))
            {
                return true;
            }
        }

        return false;
    }

    // A leaf can hold a point in a box only if its cell box and the box overlap.
    private bool Touches(int leaf, List<GeoBox> boxes)
    {
        GeoBox cell = LeafBox(_records.AsSpan(leaf * PointFormat.RecordSize, PointFormat.RecordSize));
        long latLo = cell.LatLo << GeoKey.CellShift;
        long latHi = (cell.LatHi << GeoKey.CellShift) | ((1L << GeoKey.CellShift) - 1);
        long lngLo = cell.LngLo << GeoKey.CellShift;
        long lngHi = (cell.LngHi << GeoKey.CellShift) | ((1L << GeoKey.CellShift) - 1);
        foreach (GeoBox b in boxes)
        {
            if (latLo <= b.LatHi && latHi >= b.LatLo && lngLo <= b.LngHi && lngHi >= b.LngLo)
            {
                return true;
            }
        }

        return false;
    }

    // Every point of the leaf is in one box when the leaf's whole cell box is: no per-point test needed.
    private bool Inside(int leaf, List<GeoBox> boxes)
    {
        GeoBox cell = LeafBox(_records.AsSpan(leaf * PointFormat.RecordSize, PointFormat.RecordSize));
        long latLo = cell.LatLo << GeoKey.CellShift;
        long latHi = (cell.LatHi << GeoKey.CellShift) | ((1L << GeoKey.CellShift) - 1);
        long lngLo = cell.LngLo << GeoKey.CellShift;
        long lngHi = (cell.LngHi << GeoKey.CellShift) | ((1L << GeoKey.CellShift) - 1);
        foreach (GeoBox b in boxes)
        {
            if (latLo >= b.LatLo && latHi <= b.LatHi && lngLo >= b.LngLo && lngHi <= b.LngHi)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a leaf's pairs, sorted by value then document.</summary>
    internal int ReadLeaf(int leaf, Span<long> values, Span<int> docs)
    {
        ReadOnlySpan<byte> r = _records.AsSpan(leaf * PointFormat.RecordSize, PointFormat.RecordSize);
        long lmin = BinaryPrimitives.ReadInt64LittleEndian(r);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(r[20..]);
        int vw = r[22];
        int dw = r[23];
        int vBytes = (int)IntStreamFormat.PackedBytes(count, vw);
        int dBytes = (int)IntStreamFormat.PackedBytes(count, dw);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(vBytes + dBytes + 16);
        try
        {
            _file.ReadExactly(_dataStart + BinaryPrimitives.ReadUInt32LittleEndian(r[16..]), buffer.AsSpan(0, vBytes + dBytes));
            buffer.AsSpan(vBytes + dBytes, 16).Clear();
            for (int i = 0; i < count; i++)
            {
                values[i] = unchecked(lmin + (long)PackedBits.Get(buffer.AsSpan(0, vBytes + 8), i, vw));
                docs[i] = (int)PackedBits.Get(buffer.AsSpan(vBytes, dBytes + 8), i, dw);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return count;
    }

    private long LeafMin(int leaf) => BinaryPrimitives.ReadInt64LittleEndian(_records.AsSpan(leaf * PointFormat.RecordSize));

    private int LeafCount_(int leaf) => BinaryPrimitives.ReadUInt16LittleEndian(_records.AsSpan((leaf * PointFormat.RecordSize) + 20));

    // The first leaf whose largest value is at least `value` (the tree's binary search; leaves are in value order).
    private int FirstLeafWithMaxAtLeast(long value)
    {
        int lo = 0;
        int hi = _leafCount - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (BinaryPrimitives.ReadInt64LittleEndian(_records.AsSpan((mid * PointFormat.RecordSize) + 8)) >= value)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }
}

/// <summary>Documents in increasing order, held as a sorted list or a bitmap.</summary>
internal sealed class SortedDocIterator : DocIdSetIterator
{
    private readonly int[] _docs;
    private readonly int _count;
    private readonly BitSetIterator? _bits;
    private int _index = -1;
    private int _doc = -1;

    public SortedDocIterator(int[] docs, int count, ulong[]? words)
    {
        if (words is not null)
        {
            _bits = new BitSetIterator(words);
            _docs = [];
            _count = 0;
            return;
        }

        Array.Sort(docs, 0, count);
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (n == 0 || docs[i] != docs[n - 1])
            {
                docs[n++] = docs[i];
            }
        }

        _docs = docs;
        _count = n;
    }

    public override int DocId => _bits is not null ? _bits.DocId : _doc;

    public override long Cost => _bits is not null ? _bits.Cost : _count;

    public override int NextDoc()
    {
        if (_bits is not null)
        {
            return _bits.NextDoc();
        }

        _index++;
        return _doc = _index < _count ? _docs[_index] : NoMoreDocs;
    }

    public override int Advance(int target)
    {
        if (_bits is not null)
        {
            return _bits.Advance(target);
        }

        // Galloping search from the current position, then a binary search of the bracket.
        int lo = Math.Max(_index, 0);
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        int step = 1;
        int hi = lo;
        while (hi < _count && _docs[hi] < target)
        {
            lo = hi;
            hi += step;
            step <<= 1;
        }

        hi = Math.Min(hi, _count);
        int idx = Array.BinarySearch(_docs, lo, hi - lo, target);
        if (idx < 0)
        {
            idx = ~idx;
        }

        _index = idx;
        return _doc = _index < _count ? _docs[_index] : NoMoreDocs;
    }
}
