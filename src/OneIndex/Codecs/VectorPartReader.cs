using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using OneIndex.Index;
using OneIndex.Kernels;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Codecs;

/// <summary>The full-precision vectors of one field, read block by block from the vectors file.</summary>
internal sealed class VectorFieldData : VectorValues
{
    private readonly BlockCache _cache;
    private readonly int _count;
    private readonly int _dimensions;
    private readonly int _perBlock;

    public VectorFieldData(BlockCache cache, int count, int dimensions, int perBlock)
    {
        _cache = cache;
        _count = count;
        _dimensions = dimensions;
        _perBlock = perBlock;
    }

    public override int Count => _count;

    public override int Dimensions => _dimensions;

    public override ReadOnlySpan<float> Get(int ordinal)
    {
        if ((uint)ordinal >= (uint)_count)
        {
            ThrowOrdinal();
        }

        byte[] block = _cache.Get(ordinal / _perBlock);
        int at = (ordinal % _perBlock) * _dimensions * 4;
        // The block holds whole vectors and was checked against its checksum when loaded; at + dimensions * 4 is inside it.
        return MemoryMarshal.Cast<byte, float>(block.AsSpan(at, _dimensions * 4));
    }

    private static void ThrowOrdinal() => throw new ArgumentOutOfRangeException("ordinal");
}

/// <summary>An HNSW graph read from the graph file: layer 0 by block on demand, the sparse upper layers resident.</summary>
internal sealed class HnswGraphData : HnswGraph
{
    private readonly BlockCache _layer0;
    private readonly int _count;
    private readonly int _m;
    private readonly int _recordInts;
    private readonly int _perBlock;
    private readonly int _maxLevel;
    private readonly int _entry;
    private readonly int[] _upperNodes;     // ascending ordinals of the nodes above layer 0
    private readonly byte[] _upperLevels;
    private readonly int[][] _upperLinks;   // per such node: level * (M + 1) ints, a count then the links of each layer

    public HnswGraphData(BlockCache layer0, int count, int m, int perBlock, int maxLevel, int entry, int[] upperNodes, byte[] upperLevels, int[][] upperLinks)
    {
        _layer0 = layer0;
        _count = count;
        _m = m;
        _recordInts = 2 * m + 1;
        _perBlock = perBlock;
        _maxLevel = maxLevel;
        _entry = entry;
        _upperNodes = upperNodes;
        _upperLevels = upperLevels;
        _upperLinks = upperLinks;
    }

    public override int Count => _count;

    public override int EntryPoint => _entry;

    public override int MaxLevel => _maxLevel;

    public override int MaxConnections => _m;

    public override int Level(int node)
    {
        int i = Array.BinarySearch(_upperNodes, node);
        return i < 0 ? 0 : _upperLevels[i];
    }

    public override ReadOnlySpan<int> Neighbors(int node, int level)
    {
        if (level == 0)
        {
            byte[] block = _layer0.Get(node / _perBlock);
            ReadOnlySpan<int> record = BlockCache.AsInts(block, (node % _perBlock) * _recordInts * 4, _recordInts);
            // The block was validated when loaded: the count is at most 2 M and every link is a node.
            return record.Slice(1, record[0]);
        }

        int i = Array.BinarySearch(_upperNodes, node);
        if (i < 0 || level > _upperLevels[i])
        {
            return default;
        }

        int[] links = _upperLinks[i];
        int slot = (level - 1) * (_m + 1);
        return links.AsSpan(slot + 1, links[slot]);
    }

    /// <summary>Checks a layer-0 block as it is loaded: counts within 2 M, links to existing nodes other than the node itself.</summary>
    public static void ValidateBlock(string fileName, byte[] block, int blockIndex, int count, int m, int perBlock)
    {
        int recordInts = 2 * m + 1;
        int records = block.Length / (recordInts * 4);
        if (block.Length != records * recordInts * 4)
        {
            throw new CorruptIndexException(fileName, $"graph block {blockIndex} does not hold whole records");
        }

        ReadOnlySpan<int> all = MemoryMarshal.Cast<byte, int>(block);
        for (int r = 0; r < records; r++)
        {
            ReadOnlySpan<int> record = all.Slice(r * recordInts, recordInts);
            int node = (blockIndex * perBlock) + r;
            if ((uint)record[0] > (uint)(2 * m))
            {
                throw new CorruptIndexException(fileName, $"node {node} lists {record[0]} links, more than {2 * m}");
            }

            for (int i = 1; i <= record[0]; i++)
            {
                if ((uint)record[i] >= (uint)count || record[i] == node)
                {
                    throw new CorruptIndexException(fileName, $"node {node} links to {record[i]}, which is not another node");
                }
            }
        }
    }
}

/// <summary>The compressed records of one field, read block by block from the quantized file.</summary>
internal sealed class QuantizedFieldData : QuantizedValues
{
    private readonly BlockCache _cache;
    private readonly int _count;
    private readonly int _recordBytes;
    private readonly int _perBlock;

    public QuantizedFieldData(VectorQuantization codec, BlockCache cache, int count, int recordBytes, int perBlock)
    {
        Codec = codec;
        _cache = cache;
        _count = count;
        _recordBytes = recordBytes;
        _perBlock = perBlock;
    }

    public VectorQuantization Codec { get; }

    public override int Count => _count;

    public override ReadOnlySpan<byte> Get(int ordinal)
    {
        if ((uint)ordinal >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        byte[] block = _cache.Get(ordinal / _perBlock);
        // Blocks hold whole records and were checked when loaded.
        return block.AsSpan((ordinal % _perBlock) * _recordBytes, _recordBytes);
    }

    /// <summary>Checks a block as it is loaded: plausible correction factors, or zero padding bits after the last dimension.</summary>
    public static void ValidateBlock(string fileName, byte[] block, int blockIndex, VectorQuantization codec, int dimensions, int recordBytes, int perBlock)
    {
        if (block.Length % recordBytes != 0)
        {
            throw new CorruptIndexException(fileName, $"quantized block {blockIndex} does not hold whole records");
        }

        for (int r = 0; r < block.Length / recordBytes; r++)
        {
            ReadOnlySpan<byte> record = block.AsSpan(r * recordBytes, recordBytes);
            long node = ((long)blockIndex * perBlock) + r;
            if (codec == VectorQuantization.OneBit)
            {
                int used = dimensions & 7;
                int lastByte = (dimensions - 1) >> 3;
                bool clean = used == 0 || (record[lastByte] >> used) == 0;
                for (int i = lastByte + 1; clean && i < record.Length; i++)
                {
                    clean = record[i] == 0;
                }

                if (!clean)
                {
                    throw new CorruptIndexException(fileName, $"quantized vector {node} has bits set past its last dimension");
                }
            }
            else
            {
                int codes = codec == VectorQuantization.Scalar8 ? dimensions : (dimensions + 1) / 2;
                if (!Quantizers.CorrectionsValid(record[codes..], dimensions, codec == VectorQuantization.Scalar8 ? 8 : 4))
                {
                    throw new CorruptIndexException(fileName, $"quantized vector {node} has implausible correction factors");
                }
            }
        }
    }
}

/// <summary>Nearest-neighbour search over one field of one segment.</summary>
internal sealed class VectorFieldReader : IVectorFieldReader
{
    private readonly VectorFieldData _values;
    private readonly int[]? _ordinalToDoc;  // null when every document has a vector: ordinal = document id
    private readonly HnswGraphData? _graph;
    private readonly QuantizedFieldData? _quantized;
    private readonly float _oversample;
    private readonly IvfIndex? _ivf;
    private readonly DistanceKind _kind;
    private readonly int _maxDoc;

    public VectorFieldReader(string name, VectorFieldData values, int[]? ordinalToDoc, HnswGraphData? graph, VectorMetric metric, int maxDoc,
        QuantizedFieldData? quantized = null, float oversample = 1f, IvfIndex? ivf = null)
    {
        _ivf = ivf;
        _quantized = quantized;
        _oversample = oversample;
        Name = name;
        _values = values;
        _ordinalToDoc = ordinalToDoc;
        _graph = graph;
        Metric = metric;
        _kind = metric == VectorMetric.Euclidean ? DistanceKind.SquaredL2 : DistanceKind.NegativeDot;
        _maxDoc = maxDoc;
    }

    public string Name { get; }

    public int Dimensions => _values.Dimensions;

    public VectorMetric Metric { get; }

    public int Count => _values.Count;

    public bool HasGraph => _graph is not null;

    /// <summary>Whether the field is searched through an IVF index in this segment.</summary>
    public bool HasIvf => _ivf is not null;

    internal VectorValues Values => _values;

    internal HnswGraphData? Graph => _graph;

    /// <summary>The compressed copy of this segment's vectors, or <see langword="null"/> when the field has none here.</summary>
    internal QuantizedFieldData? Quantized => _quantized;

    internal int DocOf(int ordinal) => _ordinalToDoc is null ? ordinal : _ordinalToDoc[ordinal];

    private int OrdinalOf(int doc)
    {
        if (_ordinalToDoc is null)
        {
            return (uint)doc < (uint)_values.Count ? doc : -1;
        }

        int i = Array.BinarySearch(_ordinalToDoc, doc);
        return i < 0 ? -1 : i;
    }

    public bool TryGetVector(int doc, Span<float> destination)
    {
        if (destination.Length < Dimensions)
        {
            throw new ArgumentException($"The destination holds {destination.Length} floats; the vector has {Dimensions}.", nameof(destination));
        }

        int ordinal = OrdinalOf(doc);
        if (ordinal < 0)
        {
            return false;
        }

        _values.Get(ordinal).CopyTo(destination);
        return true;
    }

    public int Search(ReadOnlySpan<float> query, int k, int ef, ILiveDocs? live, ulong[]? acceptedDocs, bool exact, Span<VectorHit> hits)
    {
        if (query.Length != Dimensions)
        {
            throw new ArgumentException($"The query has {query.Length} dimensions; the field has {Dimensions}.", nameof(query));
        }

        foreach (float x in query)
        {
            if (!float.IsFinite(x))
            {
                throw new ArgumentException("The query vector holds a NaN or an infinite value.", nameof(query));
            }
        }

        k = Math.Min(k, hits.Length);
        if (k <= 0 || Count == 0)
        {
            return 0;
        }

        if (acceptedDocs is not null && acceptedDocs.Length < ((_maxDoc + 63) >> 6))
        {
            throw new ArgumentException("The accepted-documents bit set is shorter than the segment.", nameof(acceptedDocs));
        }

        HnswScratch scratch = HnswScratch.Current;
        float[] q = scratch.QueryBuffer(Dimensions);
        query.CopyTo(q);
        if (Metric == VectorMetric.Cosine)
        {
            VectorKernels.Normalize(q.AsSpan(0, Dimensions));
        }

        FloatNodeScorer scorer = scratch.Scorer(_kind);
        scorer.Reset(_values, q.AsSpan(0, Dimensions));
        IOrdinalFilter? filter = live is null && acceptedDocs is null ? null : new DocFilter(this, live, acceptedDocs);
        int requestedEf = ef;
        ef = ef <= 0 ? DefaultEf(k) : Math.Max(ef, k);
        long accepted = acceptedDocs is null ? Count : CountBits(acceptedDocs);
        Candidate[] buffer = ArrayPool<Candidate>.Shared.Rent(k);
        try
        {
            int n;
            bool graph = _graph is not null && !exact && accepted > 0;
            if (graph && acceptedDocs is not null && accepted <= FlatSwitchFactor * Math.Max(ef, k))
            {
                // A selective filter: the filtered walk costs about the same at any selectivity, so once few enough documents pass,
                // scanning the accepted vectors in order is as cheap and exact.
                graph = false;
            }

            bool clustered = _ivf is not null && _graph is null && !exact && accepted > 0
                && !(acceptedDocs is not null && accepted <= FlatSwitchFactor * Math.Max(ef, k));
            if (clustered)
            {
                n = IvfSearch(q.AsSpan(0, Dimensions), k, requestedEf, filter, accepted, buffer);
                if (n < 0)
                {
                    n = FlatSearch.Search(_values, _kind, q.AsSpan(0, Dimensions), k, filter, buffer);
                }
            }
            else if (graph && _quantized is not null)
            {
                n = QuantizedSearch(scratch, q.AsSpan(0, Dimensions), k, ef, filter, accepted, buffer);
                if (n < 0)
                {
                    n = FlatSearch.Search(_values, _kind, q.AsSpan(0, Dimensions), k, filter, buffer);
                }
            }
            else if (graph)
            {
                n = HnswSearch.Search(_graph!, scorer, k, ef, filter, 0, buffer, out _);
                if (filter is not null && n < Math.Min(k, accepted))
                {
                    // The walk could not reach enough accepted nodes (a filter that cuts the graph apart): scan exactly.
                    n = FlatSearch.Search(_values, _kind, q.AsSpan(0, Dimensions), k, filter, buffer);
                }
            }
            else
            {
                n = FlatSearch.Search(_values, _kind, q.AsSpan(0, Dimensions), k, filter, buffer);
            }

            for (int i = 0; i < n; i++)
            {
                hits[i] = new VectorHit(DocOf(buffer[i].Id), VectorSimilarity.ToScore(Metric, buffer[i].Key));
            }

            return n;
        }
        finally
        {
            ArrayPool<Candidate>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// IVF search: the lists of the <c>nprobe</c> nearest centres are scored on their 8-bit codes, the best <c>oversample * k</c> candidates are
    /// measured against the full-precision vectors and the best <c>k</c> by true distance are returned, so the scores are exact whatever the codes.
    /// <paramref name="nprobe"/> is the number of lists to read (0: an eighth of the lists, at least 8).
    /// </summary>
    /// <returns>The number of results, or -1 when the probed lists held too few accepted vectors (the caller scans exactly).</returns>
    private int IvfSearch(ReadOnlySpan<float> q, int k, int nprobe, IOrdinalFilter? filter, long acceptedCount, Candidate[] output)
    {
        IvfIndex ivf = _ivf!;
        int probes = nprobe <= 0 ? DefaultProbes(ivf.Lists) : Math.Min(nprobe, ivf.Lists);
        int want = (int)Math.Min(Math.Ceiling(k * (double)IvfOversample), Count);
        want = Math.Max(want, k);
        IvfHit[] found = ivf.SearchApproximate(q, Math.Min(want, 10_000), probes, filter is null ? null : filter.Accepts);
        if (filter is not null && found.Length < Math.Min(k, acceptedCount))
        {
            return -1;
        }

        var scored = new Candidate[found.Length];
        for (int i = 0; i < found.Length; i++)
        {
            scored[i] = new Candidate(FloatNodeScorer.Distance(_kind, q, _values.Get(found[i].Id)), found[i].Id);
        }

        Array.Sort(scored, CandidateComparer.Instance);
        int take = Math.Min(k, scored.Length);
        Array.Copy(scored, output, take);
        return take;
    }

    /// <summary>How many candidates the IVF search re-scores exactly, as a multiple of the results asked for.</summary>
    internal const double IvfOversample = 3;

    /// <summary>The lists read when the caller gives none: an eighth of them, at least 8 (about 0.9 recall on text embeddings; docs/ivf.md).</summary>
    internal static int DefaultProbes(int lists) => Math.Min(lists, Math.Max(8, (lists + 7) / 8));

    /// <summary>
    /// Graph search on the compressed copy, then an exact rerank. The walk keeps at least <c>oversample * k</c> candidates (and at least
    /// <c>ef</c>) by compressed distance; every candidate is then measured against the full-precision vector and the best <c>k</c> by
    /// true distance are returned, so the scores are exact whatever the codec.
    /// </summary>
    /// <returns>The number of results, or -1 when a visit limit stopped the walk (the caller scans exactly).</returns>
    private int QuantizedSearch(HnswScratch scratch, ReadOnlySpan<float> q, int k, int ef, IOrdinalFilter? filter, long acceptedCount, Candidate[] output)
    {
        int candidates = (int)Math.Min(Math.Ceiling(k * (double)_oversample), Count);
        candidates = Math.Max(candidates, k);
        int width = Math.Max(ef, candidates);
        QuantizedNodeScorer scorer = scratch.QuantizedScorer;
        scorer.Reset(_quantized!, _quantized!.Codec, _kind, q);
        Candidate[] found = ArrayPool<Candidate>.Shared.Rent(width);
        try
        {
            int n = HnswSearch.Search(_graph!, scorer, width, width, filter, 0, found, out _);
            if (filter is not null && n < Math.Min(k, acceptedCount))
            {
                return -1;
            }

            // Rerank: the true distance of every candidate, best k kept (ties towards the smaller ordinal, as everywhere).
            for (int i = 0; i < n; i++)
            {
                found[i] = new Candidate(FloatNodeScorer.Distance(_kind, q, _values.Get(found[i].Id)), found[i].Id);
            }

            Array.Sort(found, 0, n, CandidateComparer.Instance);
            int take = Math.Min(k, n);
            Array.Copy(found, output, take);
            return take;
        }
        finally
        {
            ArrayPool<Candidate>.Shared.Return(found);
        }
    }

    private sealed class CandidateComparer : IComparer<Candidate>
    {
        public static readonly CandidateComparer Instance = new();

        public int Compare(Candidate x, Candidate y) => x.Key < y.Key ? -1 : x.Key > y.Key ? 1 : x.Id.CompareTo(y.Id);
    }

    /// <summary>
    /// A filter admitting at most this many times the search breadth is answered by an exact scan instead of a filtered graph walk. The
    /// walk's cost depends on the breadth and hardly on the selectivity, while a scan's depends on the number of accepted vectors; they
    /// cost the same at about 15 times the breadth (measured, docs/vectors.md), and the scan is exact, so it is taken up to 20.
    /// Settable for the measurements only.
    /// </summary>
    internal static double FlatSwitchFactor { get; set; } = 20;

    /// <summary>The search breadth used when the caller gives none: wide enough for about 0.95 recall at k = 10 on typical embeddings.</summary>
    internal static int DefaultEf(int k) => Math.Max(k, 100);

    private static long CountBits(ulong[] words)
    {
        long n = 0;
        foreach (ulong w in words)
        {
            n += BitOperations.PopCount(w);
        }

        return n;
    }

    private sealed class DocFilter(VectorFieldReader owner, ILiveDocs? live, ulong[]? accepted) : IOrdinalFilter
    {
        public bool Accepts(int ordinal)
        {
            int doc = owner.DocOf(ordinal);
            return (live is null || live.IsLive(doc)) && (accepted is null || (accepted[doc >> 6] & (1UL << (doc & 63))) != 0);
        }
    }
}

/// <summary>The vector part of a segment: every vector field's reader.</summary>
internal sealed class VectorPartReader : ISegmentPartReader, IVectorSource
{
    private readonly Dictionary<string, VectorFieldReader> _fields = new(StringComparer.Ordinal);
    private readonly List<IRangeReader> _files = [];

    public IEnumerable<string> Names => _fields.Keys;

    public IVectorFieldReader? GetVectors(string field) => _fields.GetValueOrDefault(field);

    internal VectorFieldReader? GetField(string field) => _fields.GetValueOrDefault(field);

    public void Dispose()
    {
        foreach (IRangeReader f in _files)
        {
            f.Dispose();
        }

        _files.Clear();
    }

    /// <summary>Opens the vectors file and, when present, the graph file of a segment, checking their structure.</summary>
    public static VectorPartReader Open(IRangeReader vectors, IRangeReader? graph, IRangeReader? quantized, Schema schema, int maxDoc, IndexLimits limits, CancellationToken cancellationToken)
        => Open(vectors, graph, quantized, null, schema, maxDoc, limits, cancellationToken);

    /// <summary>Opens the vectors file and, when present, the graph, compressed and IVF files of a segment, checking their structure.</summary>
    public static VectorPartReader Open(IRangeReader vectors, IRangeReader? graph, IRangeReader? quantized, IRangeReader? ivf, Schema schema, int maxDoc, IndexLimits limits, CancellationToken cancellationToken)
    {
        var part = new VectorPartReader();
        part._files.Add(vectors);
        if (ivf is not null)
        {
            part._files.Add(ivf);
        }

        if (graph is not null)
        {
            part._files.Add(graph);
        }

        if (quantized is not null)
        {
            part._files.Add(quantized);
        }

        try
        {
            part.Load(vectors, graph, quantized, ivf, schema, maxDoc, limits, cancellationToken);
        }
        catch
        {
            part.Dispose();
            throw;
        }

        return part;
    }

    private static (ByteCursorBox Directory, long DirectoryAt) ReadDirectory(IRangeReader file, uint magic, int version, int maxDoc, IndexLimits limits, CancellationToken cancellationToken)
    {
        string name = file.Name;
        long length = file.Length;
        const int minimum = IndexFile.HeaderLength + 4 + 1 + 12 + IndexFile.FooterLength;
        if (length < minimum)
        {
            throw new CorruptIndexException(name, $"{length} bytes is too short for a vector file");
        }

        IndexFile.ReadFooterChecksum(file, cancellationToken);
        Span<byte> head = stackalloc byte[IndexFile.HeaderLength + 4]; // fixed 12-byte buffer
        file.ReadExactly(0, head, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != magic)
        {
            throw new CorruptIndexException(name, "wrong magic: not a file of this type");
        }

        uint found = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        if (found == 0 || found > (uint)version)
        {
            throw new UnsupportedFormatException(name, $"format version {found} is not supported");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != (uint)maxDoc)
        {
            throw new CorruptIndexException(name, "the file covers a different number of documents than the commit says");
        }

        long trailerAt = length - IndexFile.FooterLength - 12;
        Span<byte> trailer = stackalloc byte[12]; // fixed 12-byte buffer
        file.ReadExactly(trailerAt, trailer, cancellationToken);
        ulong directoryAt = BinaryPrimitives.ReadUInt64LittleEndian(trailer);
        uint directoryCrc = BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]);
        long bodyStart = IndexFile.HeaderLength + 4;
        if (directoryAt < (ulong)bodyStart || directoryAt >= (ulong)trailerAt)
        {
            throw new CorruptIndexException(name, "the directory offset lies outside the file");
        }

        long directoryBytes = trailerAt - (long)directoryAt;
        if (directoryBytes > (long)limits.MaxFields * (limits.MaxStringBytes + 128L) + 16)
        {
            throw new CorruptIndexException(name, "the directory is larger than the limits allow");
        }

        byte[] directory = BlockCache.ReadChecked(file, (long)directoryAt, directoryBytes, directoryCrc, "the directory", limits);
        return (new ByteCursorBox(name, directory), (long)directoryAt);
    }

    // ByteCursor is a ref-like struct holder; this box lets the helper return it with its buffer.
    private readonly record struct ByteCursorBox(string Name, byte[] Bytes)
    {
        public ByteCursor Cursor() => new(Name, Bytes, 0, Bytes.Length);
    }

    private static string ReadName(string fileName, ref ByteCursor cursor, IndexLimits limits)
    {
        int length = ResidentFile.ReadCount(fileName, ref cursor, limits.MaxStringBytes, "field name length");
        try
        {
            return new UTF8Encoding(false, true).GetString(cursor.ReadBytes(length));
        }
        catch (DecoderFallbackException)
        {
            throw new CorruptIndexException(fileName, "a field name is not valid UTF-8");
        }
    }

    private void Load(IRangeReader vectors, IRangeReader? graph, IRangeReader? quantized, IRangeReader? ivf, Schema schema, int maxDoc, IndexLimits limits, CancellationToken cancellationToken)
    {
        string vname = vectors.Name;
        (ByteCursorBox dirBox, long directoryAt) = ReadDirectory(vectors, VectorSegmentCodec.VectorsMagic, CodecRegistry.NewestVersion(SegmentParts.Vectors), maxDoc, limits, cancellationToken);
        ByteCursor cursor = dirBox.Cursor();
        int fieldCount = ResidentFile.ReadCount(vname, ref cursor, limits.MaxFields, "vector field count");
        var entries = new List<(string Name, FieldDefinition Def, int Count, bool Sparse, VectorFieldData Data, int[]? OrdDoc)>();
        string? previous = null;
        long expected = IndexFile.HeaderLength + 4;
        for (int i = 0; i < fieldCount; i++)
        {
            string name = ReadName(vname, ref cursor, limits);
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw new CorruptIndexException(vname, "vector field names are not in strictly increasing order");
            }

            previous = name;
            if (!schema.TryGetField(name, out FieldDefinition? def) || def.Type != FieldType.Vector)
            {
                throw new CorruptIndexException(vname, $"the file holds vectors for '{name}', which the schema does not declare as a vector field");
            }

            ulong dims = cursor.ReadVarUInt();
            byte metric = cursor.ReadByte();
            byte encoding = cursor.ReadByte();
            ulong count = cursor.ReadVarUInt();
            byte flags = cursor.ReadByte();
            ulong dataOffset = cursor.ReadVarUInt();
            ulong perBlock = cursor.ReadVarUInt();
            ulong ordDocOffset = cursor.ReadVarUInt();
            ulong crcOffset = cursor.ReadVarUInt();
            uint ordDocCrc = (uint)cursor.ReadInt64();
            uint crcTableCrc = (uint)cursor.ReadInt64();
            if (dims != (ulong)def.Dimensions || metric != (byte)def.Metric)
            {
                throw new CorruptIndexException(vname, $"vectors of '{name}' were written with dimensions {dims} and metric {metric}, the schema says {def.Dimensions} and {(byte)def.Metric}");
            }

            if (encoding != 0 || flags > 1)
            {
                throw new CorruptIndexException(vname, $"vectors of '{name}' use an unknown encoding or flag");
            }

            bool sparse = flags == 1;
            if (count == 0 || count > (ulong)maxDoc || sparse == (count == (ulong)maxDoc))
            {
                throw new CorruptIndexException(vname, $"vectors of '{name}' claim {count} vectors for {maxDoc} documents");
            }

            int recordBytes = checked(def.Dimensions * 4);
            long dataLength = checked((long)count * recordBytes);
            if (perBlock != (ulong)VectorFiles.RecordsPerBlock(recordBytes))
            {
                throw new CorruptIndexException(vname, $"vectors of '{name}' have an unexpected block size");
            }

            long blocks = ((long)count + (long)perBlock - 1) / (long)perBlock;
            long ordDocLength = sparse ? (long)count * 4 : 0;
            if (dataOffset != (ulong)expected || (sparse ? ordDocOffset != (ulong)(expected + dataLength) : ordDocOffset != 0)
                || crcOffset != (ulong)(expected + dataLength + ordDocLength))
            {
                throw new CorruptIndexException(vname, $"the sections of '{name}' do not follow each other");
            }

            expected = (long)crcOffset + (blocks * 4);
            if (expected > directoryAt)
            {
                throw new CorruptIndexException(vname, $"the sections of '{name}' run past the directory");
            }

            int[]? ordDoc = null;
            if (sparse)
            {
                byte[] raw = BlockCache.ReadChecked(vectors, (long)ordDocOffset, ordDocLength, ordDocCrc, "the ordinal to document table", limits);
                ordDoc = new int[(int)count];
                int last = -1;
                for (int j = 0; j < ordDoc.Length; j++)
                {
                    int doc = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(j * 4));
                    if (doc <= last || doc >= maxDoc)
                    {
                        throw new CorruptIndexException(vname, $"the ordinal to document table of '{name}' is not increasing and within the segment");
                    }

                    ordDoc[j] = last = doc;
                }
            }

            byte[] crcBytes = BlockCache.ReadChecked(vectors, (long)crcOffset, blocks * 4, crcTableCrc, "the block checksum table", limits);
            var crcs = new uint[blocks];
            for (int j = 0; j < crcs.Length; j++)
            {
                crcs[j] = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes.AsSpan(j * 4));
            }

            var cache = new BlockCache(vectors, (long)dataOffset, dataLength, (int)perBlock * recordBytes, crcs, null, limits);
            entries.Add((name, def, (int)count, sparse, new VectorFieldData(cache, (int)count, def.Dimensions, (int)perBlock), ordDoc));
        }

        if (expected != directoryAt || cursor.Remaining != 0)
        {
            throw new CorruptIndexException(vname, "the vector sections and directory do not tile the file");
        }

        Dictionary<string, HnswGraphData> graphs = graph is null ? [] : LoadGraphs(graph, schema, maxDoc, limits, entries.ToDictionary(e => e.Name, e => e.Count, StringComparer.Ordinal), cancellationToken);
        Dictionary<string, QuantizedFieldData> compressed = quantized is null ? [] : LoadQuantized(quantized, schema, maxDoc, limits, entries.ToDictionary(e => e.Name, e => e.Count, StringComparer.Ordinal), cancellationToken);
        Dictionary<string, IvfIndex> clusters = ivf is null ? [] : LoadIvf(ivf, schema, maxDoc, limits, entries.ToDictionary(e => e.Name, e => e.Count, StringComparer.Ordinal), cancellationToken);
        foreach (var e in entries)
        {
            // A compressed copy is used only while it is the codec the schema asks for and there is a graph to walk it with; otherwise
            // (a segment written before the codec was chosen, say) the field is searched on the full-precision vectors.
            HnswGraphData? g = graphs.GetValueOrDefault(e.Name);
            QuantizedFieldData? qd = g is not null && compressed.TryGetValue(e.Name, out QuantizedFieldData? c) && c.Codec == e.Def.Quantization ? c : null;
            float oversample = e.Def.Oversample > 0 ? e.Def.Oversample : VectorQuantizer.DefaultOversample(e.Def.Quantization);
            _fields[e.Name] = new VectorFieldReader(e.Name, e.Data, e.OrdDoc, g, e.Def.Metric, maxDoc, qd, oversample, clusters.GetValueOrDefault(e.Name));
        }
    }

    private static Dictionary<string, IvfIndex> LoadIvf(IRangeReader file, Schema schema, int maxDoc, IndexLimits limits, Dictionary<string, int> counts, CancellationToken cancellationToken)
    {
        string iname = file.Name;
        (ByteCursorBox dirBox, long directoryAt) = ReadDirectory(file, VectorSegmentCodec.IvfMagic, CodecRegistry.NewestVersion(SegmentParts.Ivf), maxDoc, limits, cancellationToken);
        ByteCursor cursor = dirBox.Cursor();
        int fieldCount = ResidentFile.ReadCount(iname, ref cursor, limits.MaxFields, "IVF field count");
        var result = new Dictionary<string, IvfIndex>(StringComparer.Ordinal);
        string? previous = null;
        long expected = IndexFile.HeaderLength + 4;
        for (int i = 0; i < fieldCount; i++)
        {
            string name = ReadName(iname, ref cursor, limits);
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw new CorruptIndexException(iname, "IVF field names are not in strictly increasing order");
            }

            previous = name;
            if (!schema.TryGetField(name, out FieldDefinition? def) || def.Type != FieldType.Vector || !counts.TryGetValue(name, out int count))
            {
                throw new CorruptIndexException(iname, $"the file holds an IVF index for '{name}', which is not a vector field of this segment");
            }

            ulong dims = cursor.ReadVarUInt();
            ulong vectors = cursor.ReadVarUInt();
            ulong offset = cursor.ReadVarUInt();
            ulong length = cursor.ReadVarUInt();
            if (dims != (ulong)def.Dimensions || vectors != (ulong)count || offset != (ulong)expected || length == 0 || length > (ulong)directoryAt || expected + (long)length > directoryAt)
            {
                throw new CorruptIndexException(iname, $"the IVF index of '{name}' disagrees with the vectors, the schema or the file layout");
            }

            expected += (long)length;
            // The index checks its own header, directory and list sizes against the window; the footer checksum of the whole file is the commit's check.
            IvfIndex index = IvfIndex.Open(new SliceReader(file, (long)offset, (long)length), verifyChecksum: false, cancellationToken);
            if (index.Count != count || index.Dimensions != def.Dimensions || index.Metric != def.Metric || index.Storage != IvfStorage.EightBitCodes)
            {
                throw new CorruptIndexException(iname, $"the IVF index of '{name}' was built for other vectors than the segment holds");
            }

            result[name] = index;
        }

        if (expected != directoryAt || cursor.Remaining != 0)
        {
            throw new CorruptIndexException(iname, "the IVF sections and directory do not tile the file");
        }

        return result;
    }

    private static Dictionary<string, HnswGraphData> LoadGraphs(IRangeReader graph, Schema schema, int maxDoc, IndexLimits limits, Dictionary<string, int> counts, CancellationToken cancellationToken)
    {
        string gname = graph.Name;
        (ByteCursorBox dirBox, long directoryAt) = ReadDirectory(graph, VectorSegmentCodec.GraphMagic, CodecRegistry.NewestVersion(SegmentParts.Graph), maxDoc, limits, cancellationToken);
        ByteCursor cursor = dirBox.Cursor();
        int fieldCount = ResidentFile.ReadCount(gname, ref cursor, limits.MaxFields, "graph count");
        var result = new Dictionary<string, HnswGraphData>(StringComparer.Ordinal);
        string? previous = null;
        long expected = IndexFile.HeaderLength + 4;
        for (int i = 0; i < fieldCount; i++)
        {
            string name = ReadName(gname, ref cursor, limits);
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw new CorruptIndexException(gname, "graph field names are not in strictly increasing order");
            }

            previous = name;
            if (!counts.TryGetValue(name, out int count) || !schema.TryGetField(name, out FieldDefinition? def))
            {
                throw new CorruptIndexException(gname, $"the file holds a graph for '{name}', which has no vectors in the segment");
            }

            ulong nodes = cursor.ReadVarUInt();
            ulong m = cursor.ReadVarUInt();
            ulong maxLevel = cursor.ReadVarUInt();
            ulong entry = cursor.ReadVarUInt();
            ulong level0Offset = cursor.ReadVarUInt();
            ulong perBlock = cursor.ReadVarUInt();
            ulong crcOffset = cursor.ReadVarUInt();
            uint crcTableCrc = (uint)cursor.ReadInt64();
            ulong upperOffset = cursor.ReadVarUInt();
            ulong upperLength = cursor.ReadVarUInt();
            uint upperCrc = (uint)cursor.ReadInt64();
            if (nodes != (ulong)count || m != (ulong)def.HnswM || maxLevel > 64 || entry >= nodes)
            {
                throw new CorruptIndexException(gname, $"the graph of '{name}' disagrees with the vectors or the schema");
            }

            int recordInts = (2 * (int)m) + 1;
            long level0Length = (long)count * recordInts * 4;
            if (perBlock != (ulong)VectorFiles.RecordsPerBlock(recordInts * 4))
            {
                throw new CorruptIndexException(gname, $"the graph of '{name}' has an unexpected block size");
            }

            long blocks = ((long)count + (long)perBlock - 1) / (long)perBlock;
            if (level0Offset != (ulong)expected || crcOffset != (ulong)(expected + level0Length) || upperOffset != crcOffset + (ulong)(blocks * 4))
            {
                throw new CorruptIndexException(gname, $"the sections of the graph of '{name}' do not follow each other");
            }

            expected = (long)upperOffset + (long)upperLength;
            if (upperLength > (ulong)directoryAt || expected > directoryAt)
            {
                throw new CorruptIndexException(gname, $"the sections of the graph of '{name}' run past the directory");
            }

            byte[] crcBytes = BlockCache.ReadChecked(graph, (long)crcOffset, blocks * 4, crcTableCrc, "the block checksum table", limits);
            var crcs = new uint[blocks];
            for (int j = 0; j < crcs.Length; j++)
            {
                crcs[j] = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes.AsSpan(j * 4));
            }

            byte[] upper = BlockCache.ReadChecked(graph, (long)upperOffset, (long)upperLength, upperCrc, "the upper layers", limits);
            (int[] upperNodes, byte[] upperLevels, int[][] upperLinks) = ParseUpper(gname, upper, count, (int)m, (int)maxLevel, (int)entry);
            int mInt = (int)m;
            int perBlockInt = (int)perBlock;
            var cache = new BlockCache(graph, (long)level0Offset, level0Length, perBlockInt * recordInts * 4, crcs, (b, idx) => HnswGraphData.ValidateBlock(gname, b, idx, count, mInt, perBlockInt), limits);
            result[name] = new HnswGraphData(cache, count, mInt, perBlockInt, (int)maxLevel, (int)entry, upperNodes, upperLevels, upperLinks);
        }

        if (expected != directoryAt || cursor.Remaining != 0)
        {
            throw new CorruptIndexException(gname, "the graph sections and directory do not tile the file");
        }

        return result;
    }

    private static Dictionary<string, QuantizedFieldData> LoadQuantized(IRangeReader file, Schema schema, int maxDoc, IndexLimits limits, Dictionary<string, int> counts, CancellationToken cancellationToken)
    {
        string qname = file.Name;
        (ByteCursorBox dirBox, long directoryAt) = ReadDirectory(file, VectorSegmentCodec.QuantizedMagic, CodecRegistry.NewestVersion(SegmentParts.Quantized), maxDoc, limits, cancellationToken);
        ByteCursor cursor = dirBox.Cursor();
        int fieldCount = ResidentFile.ReadCount(qname, ref cursor, limits.MaxFields, "quantized field count");
        var result = new Dictionary<string, QuantizedFieldData>(StringComparer.Ordinal);
        string? previous = null;
        long expected = IndexFile.HeaderLength + 4;
        for (int i = 0; i < fieldCount; i++)
        {
            string name = ReadName(qname, ref cursor, limits);
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0)
            {
                throw new CorruptIndexException(qname, "quantized field names are not in strictly increasing order");
            }

            previous = name;
            if (!counts.TryGetValue(name, out int count) || !schema.TryGetField(name, out FieldDefinition? def))
            {
                throw new CorruptIndexException(qname, $"the file holds compressed vectors for '{name}', which has no vectors in the segment");
            }

            ulong dims = cursor.ReadVarUInt();
            byte codecByte = cursor.ReadByte();
            ulong nodes = cursor.ReadVarUInt();
            ulong recordBytes = cursor.ReadVarUInt();
            ulong perBlock = cursor.ReadVarUInt();
            ulong dataOffset = cursor.ReadVarUInt();
            ulong crcOffset = cursor.ReadVarUInt();
            uint crcTableCrc = (uint)cursor.ReadInt64();
            if (codecByte is 0 or > (byte)VectorQuantization.OneBit || dims != (ulong)def.Dimensions || nodes != (ulong)count)
            {
                throw new CorruptIndexException(qname, $"the compressed vectors of '{name}' disagree with the vectors or the schema");
            }

            var codec = (VectorQuantization)codecByte;
            int expectedRecord = VectorQuantizer.RecordBytes(codec, def.Dimensions);
            if (recordBytes != (ulong)expectedRecord || perBlock != (ulong)VectorFiles.RecordsPerBlock(expectedRecord))
            {
                throw new CorruptIndexException(qname, $"the compressed vectors of '{name}' have an unexpected record or block size");
            }

            long dataLength = (long)count * expectedRecord;
            long blocks = ((long)count + (long)perBlock - 1) / (long)perBlock;
            if (dataOffset != (ulong)expected || crcOffset != (ulong)(expected + dataLength))
            {
                throw new CorruptIndexException(qname, $"the sections of the compressed vectors of '{name}' do not follow each other");
            }

            expected = (long)crcOffset + (blocks * 4);
            if (expected > directoryAt)
            {
                throw new CorruptIndexException(qname, $"the sections of the compressed vectors of '{name}' run past the directory");
            }

            byte[] crcBytes = BlockCache.ReadChecked(file, (long)crcOffset, blocks * 4, crcTableCrc, "the block checksum table", limits);
            var crcs = new uint[blocks];
            for (int j = 0; j < crcs.Length; j++)
            {
                crcs[j] = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes.AsSpan(j * 4));
            }

            int dimensions = def.Dimensions;
            int perBlockInt = (int)perBlock;
            var cache = new BlockCache(file, (long)dataOffset, dataLength, perBlockInt * expectedRecord, crcs, (b, idx) => QuantizedFieldData.ValidateBlock(qname, b, idx, codec, dimensions, expectedRecord, perBlockInt), limits);
            result[name] = new QuantizedFieldData(codec, cache, count, expectedRecord, perBlockInt);
        }

        if (expected != directoryAt || cursor.Remaining != 0)
        {
            throw new CorruptIndexException(qname, "the compressed vector sections and directory do not tile the file");
        }

        return result;
    }

    private static (int[] Nodes, byte[] Levels, int[][] Links) ParseUpper(string fileName, byte[] bytes, int count, int m, int maxLevel, int entry)
    {
        var cursor = new ByteCursor(fileName, bytes, 0, bytes.Length);
        int nodeCount = ResidentFile.ReadCount(fileName, ref cursor, count, "upper node count");
        var nodes = new int[nodeCount];
        var levels = new byte[nodeCount];
        var links = new int[nodeCount][];
        long previous = -1;
        int top = 0;
        int entryLevel = -1;
        for (int i = 0; i < nodeCount; i++)
        {
            long node = previous + 1 + (long)cursor.ReadVarUInt();
            if (node >= count)
            {
                throw new CorruptIndexException(fileName, "an upper-layer node lies outside the graph");
            }

            previous = node;
            int level = cursor.ReadByte();
            if (level < 1 || level > maxLevel)
            {
                throw new CorruptIndexException(fileName, $"node {node} has level {level}, outside 1 to {maxLevel}");
            }

            var own = new int[level * (m + 1)];
            for (int l = 0; l < level; l++)
            {
                int n = ResidentFile.ReadCount(fileName, ref cursor, m, "upper link count");
                own[l * (m + 1)] = n;
                for (int j = 0; j < n; j++)
                {
                    ulong target = cursor.ReadVarUInt();
                    if (target >= (ulong)count || target == (ulong)node)
                    {
                        throw new CorruptIndexException(fileName, $"node {node} links to {target}, which is not another node");
                    }

                    own[(l * (m + 1)) + 1 + j] = (int)target;
                }
            }

            nodes[i] = (int)node;
            levels[i] = (byte)level;
            links[i] = own;
            top = Math.Max(top, level);
            if (node == entry)
            {
                entryLevel = level;
            }
        }

        if (cursor.Remaining != 0 || top != maxLevel || (maxLevel > 0 && entryLevel != maxLevel))
        {
            throw new CorruptIndexException(fileName, "the upper layers are inconsistent with the graph header");
        }

        // A link on an upper layer must point to a node that is in that layer.
        for (int i = 0; i < nodeCount; i++)
        {
            for (int l = 0; l < levels[i]; l++)
            {
                for (int j = 0; j < links[i][l * (m + 1)]; j++)
                {
                    int target = links[i][(l * (m + 1)) + 1 + j];
                    int at = Array.BinarySearch(nodes, target);
                    if (at < 0 || levels[at] < l + 1)
                    {
                        throw new CorruptIndexException(fileName, $"node {nodes[i]} links on layer {l + 1} to {target}, which is not in that layer");
                    }
                }
            }
        }

        return (nodes, levels, links);
    }
}
