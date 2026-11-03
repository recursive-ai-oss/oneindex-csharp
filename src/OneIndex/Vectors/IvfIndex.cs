using System.Buffers.Binary;
using OneIndex.Index;
using OneIndex.Kernels;
using OneIndex.Storage;

namespace OneIndex.Vectors;

/// <summary>A neighbour found by <see cref="IvfIndex"/>.</summary>
/// <param name="Id">The vector's number in the order it was built (0 for the first row).</param>
/// <param name="Score">The similarity, larger is nearer: the cosine or dot product, or <c>1 / (1 + d²)</c> for Euclidean.</param>
public readonly record struct IvfHit(int Id, float Score);

/// <summary>
/// A reader of an IVF index file (see <see cref="IvfBuilder"/> and docs/ivf.md). Opening reads the header, the centroids and the list
/// directory; a search then reads only the lists of the <c>nprobe</c> nearest centroids, plus, for 8-bit lists, the full-precision rows of
/// the candidates it re-scores. Every read is a range read from the storage, so the same code serves local files and HTTP.
/// </summary>
public sealed class IvfIndex : IDisposable
{
    private readonly IRangeReader _reader;
    private readonly float[] _centroids;
    private readonly long[] _listOffset;
    private readonly int[] _listCount;
    private readonly int[] _listFirst;
    private readonly long _rerankStart;

    private IvfIndex(IRangeReader reader, int dims, VectorMetric metric, IvfStorage storage, int count, float[] centroids, long[] offsets, int[] counts, int[] firsts, long rerankStart)
    {
        _reader = reader;
        Dimensions = dims;
        Metric = metric;
        Storage = storage;
        Count = count;
        _centroids = centroids;
        _listOffset = offsets;
        _listCount = counts;
        _listFirst = firsts;
        _rerankStart = rerankStart;
    }

    /// <summary>The vector length.</summary>
    public int Dimensions { get; }

    /// <summary>The metric the index was built with.</summary>
    public VectorMetric Metric { get; }

    /// <summary>How the lists hold vectors.</summary>
    public IvfStorage Storage { get; }

    /// <summary>The number of vectors.</summary>
    public int Count { get; }

    /// <summary>The number of lists (clusters).</summary>
    public int Lists => _listCount.Length;

    /// <summary>Bytes read from the file since it was opened, for reporting what a query costs over a network.</summary>
    public long BytesRead { get; private set; }

    /// <summary>Opens an index file.</summary>
    /// <param name="reader">The file. The index owns it and disposes it.</param>
    /// <param name="verifyChecksum">Reads the whole file once and checks its CRC-32C. Default true; pass false to open over a network without downloading it all.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The index.</returns>
    /// <exception cref="CorruptIndexException">The file is not a valid index: bad magic, version, sizes, offsets or checksum.</exception>
    public static IvfIndex Open(IRangeReader reader, bool verifyChecksum = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            var header = new byte[IvfBuilder.HeaderBytes];
            Require(reader, header.Length);
            reader.ReadExactly(0, header, cancellationToken);
            (int dims, VectorMetric metric, int nlist, int n, IvfStorage storage, long rerank) = ParseHeader(reader, header);
            long bodyLength = ((long)nlist * dims * 4) + ((long)nlist * IvfBuilder.DirectoryEntryBytes);
            if (IvfBuilder.HeaderBytes + bodyLength + 8 > reader.Length)
            {
                throw new CorruptIndexException(reader.Name, "the centroids and list directory run past the end of the file");
            }

            var body = new byte[bodyLength];
            reader.ReadExactly(IvfBuilder.HeaderBytes, body, cancellationToken);
            IvfIndex index = Finish(reader, dims, metric, nlist, n, storage, rerank, body);
            index.BytesRead = IvfBuilder.HeaderBytes + bodyLength;
            if (verifyChecksum)
            {
                index.VerifyChecksum(cancellationToken);
            }

            return index;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Opens an index file; the async twin of <see cref="Open"/>.</summary>
    /// <param name="reader">The file. The index owns it and disposes it.</param>
    /// <param name="verifyChecksum">As for <see cref="Open"/>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The index.</returns>
    /// <exception cref="CorruptIndexException">The file is not a valid index.</exception>
    public static async ValueTask<IvfIndex> OpenAsync(IRangeReader reader, bool verifyChecksum = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            var header = new byte[IvfBuilder.HeaderBytes];
            Require(reader, header.Length);
            await reader.ReadExactlyAsync(0, header, cancellationToken).ConfigureAwait(false);
            (int dims, VectorMetric metric, int nlist, int n, IvfStorage storage, long rerank) = ParseHeader(reader, header);
            long bodyLength = ((long)nlist * dims * 4) + ((long)nlist * IvfBuilder.DirectoryEntryBytes);
            if (IvfBuilder.HeaderBytes + bodyLength + 8 > reader.Length)
            {
                throw new CorruptIndexException(reader.Name, "the centroids and list directory run past the end of the file");
            }

            var body = new byte[bodyLength];
            await reader.ReadExactlyAsync(IvfBuilder.HeaderBytes, body, cancellationToken).ConfigureAwait(false);
            IvfIndex index = Finish(reader, dims, metric, nlist, n, storage, rerank, body);
            index.BytesRead = IvfBuilder.HeaderBytes + bodyLength;
            if (verifyChecksum)
            {
                await index.VerifyChecksumAsync(cancellationToken).ConfigureAwait(false);
            }

            return index;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static void Require(IRangeReader reader, int bytes)
    {
        if (reader.Length < bytes + 8)
        {
            throw new CorruptIndexException(reader.Name, "the file is too short to be an IVF index");
        }
    }

    private static (int Dims, VectorMetric Metric, int NList, int N, IvfStorage Storage, long Rerank) ParseHeader(IRangeReader reader, ReadOnlySpan<byte> h)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != IvfBuilder.Magic)
        {
            throw new CorruptIndexException(reader.Name, "not an IVF index (bad magic)");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        if (version != IvfBuilder.Version)
        {
            throw new UnsupportedFormatException(reader.Name, $"IVF format version {version} is not supported");
        }

        uint dims = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
        uint metric = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        uint nlist = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        uint storage = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        long rerank = BinaryPrimitives.ReadInt64LittleEndian(h[32..]);
        if (dims is < 1 or > 65536 || metric > 2 || nlist is < 1 or > 4096 || n > int.MaxValue || nlist > n || storage > 1)
        {
            throw new CorruptIndexException(reader.Name, "the IVF header holds an impossible size or setting");
        }

        return ((int)dims, (VectorMetric)metric, (int)nlist, (int)n, (IvfStorage)storage, rerank);
    }

    private static IvfIndex Finish(IRangeReader reader, int dims, VectorMetric metric, int nlist, int n, IvfStorage storage, long rerank, byte[] body)
    {
        var centroids = new float[nlist * dims];
        for (int i = 0; i < centroids.Length; i++)
        {
            centroids[i] = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(i * 4, 4));
        }

        var offsets = new long[nlist];
        var counts = new int[nlist];
        var firsts = new int[nlist];
        int dir = nlist * dims * 4;
        long total = 0;
        int codeBytes = storage == IvfStorage.EightBit ? 4 + dims : dims * 4;
        long expected = IvfBuilder.HeaderBytes + (long)body.Length;
        for (int c = 0; c < nlist; c++)
        {
            ReadOnlySpan<byte> e = body.AsSpan(dir + (c * IvfBuilder.DirectoryEntryBytes), IvfBuilder.DirectoryEntryBytes);
            offsets[c] = BinaryPrimitives.ReadInt64LittleEndian(e);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (count > n || first != total || offsets[c] != expected)
            {
                throw new CorruptIndexException(reader.Name, $"list {c} of the IVF index is out of place");
            }

            counts[c] = (int)count;
            firsts[c] = (int)first;
            total += count;
            expected += (long)count * (4 + codeBytes);
        }

        long rerankExpected = storage == IvfStorage.EightBit ? expected : 0;
        long end = expected + (storage == IvfStorage.EightBit ? (long)n * dims * 4 : 0) + 8;
        if (total != n || rerank != rerankExpected || end != reader.Length)
        {
            throw new CorruptIndexException(reader.Name, "the IVF index's lists do not add up to its size");
        }

        return new IvfIndex(reader, dims, metric, storage, n, centroids, offsets, counts, firsts, rerank);
    }

    /// <summary>Reads the whole file and checks its checksum.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="CorruptIndexException">The checksum or the closing magic differs.</exception>
    public void VerifyChecksum(CancellationToken cancellationToken = default)
    {
        uint crc = 0;
        var buffer = new byte[64 * 1024];
        long end = _reader.Length - 8;
        for (long at = 0; at < end; at += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, end - at);
            _reader.ReadExactly(at, buffer.AsSpan(0, n), cancellationToken);
            BytesRead += n;
            crc = Crc32C.Update(crc, buffer.AsSpan(0, n));
        }

        var tail = new byte[8];
        _reader.ReadExactly(end, tail, cancellationToken);
        CheckTail(crc, tail);
    }

    /// <summary>Async twin of <see cref="VerifyChecksum"/>.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task that completes when the file has been checked.</returns>
    public async ValueTask VerifyChecksumAsync(CancellationToken cancellationToken = default)
    {
        uint crc = 0;
        var buffer = new byte[64 * 1024];
        long end = _reader.Length - 8;
        for (long at = 0; at < end; at += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, end - at);
            await _reader.ReadExactlyAsync(at, buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            BytesRead += n;
            crc = Crc32C.Update(crc, buffer.AsSpan(0, n));
        }

        var tail = new byte[8];
        await _reader.ReadExactlyAsync(end, tail, cancellationToken).ConfigureAwait(false);
        CheckTail(crc, tail);
    }

    private void CheckTail(uint crc, ReadOnlySpan<byte> tail)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(tail) != crc || BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]) != IvfBuilder.Magic)
        {
            throw new CorruptIndexException(_reader.Name, "the IVF index's checksum does not match");
        }
    }

    /// <summary>Finds the nearest vectors.</summary>
    /// <param name="query">The query vector, <see cref="Dimensions"/> finite floats (scaled to length 1 for cosine).</param>
    /// <param name="k">How many neighbours, 1 to 10,000.</param>
    /// <param name="nprobe">How many lists to read, 1 to <see cref="Lists"/> (more is slower and finds more). Default 8.</param>
    /// <param name="accept">Keeps only ids for which it returns true (a filter applied while scanning); null keeps all.</param>
    /// <param name="oversample">For 8-bit lists: how many candidates, as a multiple of <paramref name="k"/>, are re-scored from full precision. Default 4.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>Up to <paramref name="k"/> hits, nearest first; ties go to the smaller id.</returns>
    /// <exception cref="ArgumentException">The query has the wrong length or is not finite.</exception>
    /// <exception cref="CorruptIndexException">A list is damaged.</exception>
    public IvfHit[] Search(ReadOnlySpan<float> query, int k, int nprobe = 8, Func<int, bool>? accept = null, int oversample = 4, CancellationToken cancellationToken = default)
    {
        float[] q = PrepareQuery(query, k, ref nprobe);
        int[] probes = Probes(q, nprobe);
        var candidates = new Candidates(Storage == IvfStorage.EightBit ? Math.Min(Count, Math.Max(k, k * Math.Max(1, oversample))) : k);
        float queryScale = Storage == IvfStorage.EightBit ? Scale(q) : 0;
        foreach (int list in probes)
        {
            if (_listCount[list] == 0)
            {
                continue;
            }

            byte[] bytes = new byte[ListBytes(list)];
            _reader.ReadExactly(_listOffset[list], bytes, cancellationToken);
            BytesRead += bytes.Length;
            ScanList(list, bytes, q, queryScale, accept, candidates);
        }

        if (Storage != IvfStorage.EightBit)
        {
            return candidates.Result(k);
        }

        var rows = new byte[Dimensions * 4];
        var rescored = new Candidates(k);
        var row = new float[Dimensions];
        foreach ((int id, int position, float _) in candidates.Items())
        {
            _reader.ReadExactly(_rerankStart + ((long)position * Dimensions * 4), rows, cancellationToken);
            BytesRead += rows.Length;
            rescored.Offer(id, position, Exact(q, rows, row));
        }

        return rescored.Result(k);
    }

    /// <summary>Async twin of <see cref="Search"/>.</summary>
    /// <param name="query">The query vector.</param>
    /// <param name="k">How many neighbours.</param>
    /// <param name="nprobe">How many lists to read.</param>
    /// <param name="accept">The filter.</param>
    /// <param name="oversample">The re-score multiple for 8-bit lists.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The hits, nearest first.</returns>
    public async ValueTask<IvfHit[]> SearchAsync(ReadOnlyMemory<float> query, int k, int nprobe = 8, Func<int, bool>? accept = null, int oversample = 4, CancellationToken cancellationToken = default)
    {
        float[] q = PrepareQuery(query.Span, k, ref nprobe);
        int[] probes = Probes(q, nprobe);
        var candidates = new Candidates(Storage == IvfStorage.EightBit ? Math.Min(Count, Math.Max(k, k * Math.Max(1, oversample))) : k);
        float queryScale = Storage == IvfStorage.EightBit ? Scale(q) : 0;
        foreach (int list in probes)
        {
            if (_listCount[list] == 0)
            {
                continue;
            }

            byte[] bytes = new byte[ListBytes(list)];
            await _reader.ReadExactlyAsync(_listOffset[list], bytes, cancellationToken).ConfigureAwait(false);
            BytesRead += bytes.Length;
            ScanList(list, bytes, q, queryScale, accept, candidates);
        }

        if (Storage != IvfStorage.EightBit)
        {
            return candidates.Result(k);
        }

        var rows = new byte[Dimensions * 4];
        var rescored = new Candidates(k);
        var row = new float[Dimensions];
        foreach ((int id, int position, float _) in candidates.Items())
        {
            await _reader.ReadExactlyAsync(_rerankStart + ((long)position * Dimensions * 4), rows, cancellationToken).ConfigureAwait(false);
            BytesRead += rows.Length;
            rescored.Offer(id, position, Exact(q, rows, row));
        }

        return rescored.Result(k);
    }

    private static float Scale(float[] q) => IvfBuilder.Scale(q);

    private float[] PrepareQuery(ReadOnlySpan<float> query, int k, ref int nprobe)
    {
        if (query.Length != Dimensions)
        {
            throw new ArgumentException($"The query must have {Dimensions} values.", nameof(query));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(k, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(nprobe, 1);
        nprobe = Math.Min(nprobe, Lists);
        float[] q = query.ToArray();
        foreach (float x in q)
        {
            if (!float.IsFinite(x))
            {
                throw new ArgumentException("The query holds a NaN or an infinity.", nameof(query));
            }
        }

        if (Metric == VectorMetric.Cosine)
        {
            VectorKernels.Normalize(q);
        }

        return q;
    }

    private int[] Probes(float[] q, int nprobe)
    {
        var scores = new (float Score, int List)[Lists];
        for (int c = 0; c < scores.Length; c++)
        {
            ReadOnlySpan<float> cen = _centroids.AsSpan(c * Dimensions, Dimensions);
            scores[c] = (Metric == VectorMetric.Euclidean ? -VectorKernels.SquaredL2(q, cen) : VectorKernels.Dot(q, cen), c);
        }

        Array.Sort(scores, static (a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.List.CompareTo(b.List));
        var probes = new int[nprobe];
        for (int i = 0; i < nprobe; i++)
        {
            probes[i] = scores[i].List;
        }

        return probes;
    }

    private int ListBytes(int list) => _listCount[list] * (4 + (Storage == IvfStorage.EightBit ? 4 + Dimensions : Dimensions * 4));

    private void ScanList(int list, byte[] bytes, float[] q, float queryScale, Func<int, bool>? accept, Candidates into)
    {
        int count = _listCount[list];
        int dims = Dimensions;
        int vecStart = count * 4;
        var vec = new float[dims];
        sbyte[]? qcode = null;
        if (Storage == IvfStorage.EightBit)
        {
            qcode = new sbyte[dims];
            for (int d = 0; d < dims; d++)
            {
                qcode[d] = IvfBuilder.Quantize(q[d], queryScale);
            }
        }

        var code = new sbyte[dims];
        for (int i = 0; i < count; i++)
        {
            int id = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
            if (id >= Count)
            {
                throw new CorruptIndexException(_reader.Name, $"list {list} names vector {id} of {Count}");
            }

            if (accept is not null && !accept(id))
            {
                continue;
            }

            float score;
            if (qcode is not null)
            {
                int at = vecStart + (i * (4 + dims));
                float scale = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at, 4));
                for (int d = 0; d < dims; d++)
                {
                    code[d] = (sbyte)bytes[at + 4 + d];
                }

                float dot = VectorKernels.DotInt8(qcode, code) * scale * queryScale;
                score = Metric == VectorMetric.Euclidean ? ApproxEuclid(q, code, scale, vec) : dot;
            }
            else
            {
                int at = vecStart + (i * dims * 4);
                for (int d = 0; d < dims; d++)
                {
                    vec[d] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + (d * 4), 4));
                }

                score = Metric == VectorMetric.Euclidean ? 1f / (1f + VectorKernels.SquaredL2(q, vec)) : VectorKernels.Dot(q, vec);
            }

            into.Offer(id, _listFirst[list] + i, score);
        }
    }

    private static float ApproxEuclid(float[] q, sbyte[] code, float scale, float[] scratch)
    {
        for (int d = 0; d < code.Length; d++)
        {
            scratch[d] = code[d] * scale;
        }

        return 1f / (1f + VectorKernels.SquaredL2(q, scratch));
    }

    private float Exact(float[] q, byte[] rows, float[] scratch)
    {
        for (int d = 0; d < Dimensions; d++)
        {
            scratch[d] = BinaryPrimitives.ReadSingleLittleEndian(rows.AsSpan(d * 4, 4));
        }

        return Metric == VectorMetric.Euclidean ? 1f / (1f + VectorKernels.SquaredL2(q, scratch)) : VectorKernels.Dot(q, scratch);
    }

    /// <inheritdoc />
    public void Dispose() => _reader.Dispose();

    /// <summary>The best k offers: higher score first, smaller id on ties.</summary>
    private sealed class Candidates(int capacity)
    {
        private readonly List<(int Id, int Position, float Score)> _items = [];

        public void Offer(int id, int position, float score)
        {
            if (_items.Count == capacity)
            {
                (int worstId, _, float worst) = _items[^1];
                if (score < worst || (score == worst && id > worstId))
                {
                    return;
                }
            }

            int at = _items.Count;
            _items.Add((id, position, score));
            while (at > 0 && Better(_items[at], _items[at - 1]))
            {
                (_items[at], _items[at - 1]) = (_items[at - 1], _items[at]);
                at--;
            }

            if (_items.Count > capacity)
            {
                _items.RemoveAt(_items.Count - 1);
            }
        }

        private static bool Better((int Id, int Position, float Score) a, (int Id, int Position, float Score) b)
            => a.Score > b.Score || (a.Score == b.Score && a.Id < b.Id);

        public List<(int Id, int Position, float Score)> Items() => _items;

        public IvfHit[] Result(int k)
        {
            int n = Math.Min(k, _items.Count);
            var hits = new IvfHit[n];
            for (int i = 0; i < n; i++)
            {
                hits[i] = new IvfHit(_items[i].Id, _items[i].Score);
            }

            return hits;
        }
    }
}
