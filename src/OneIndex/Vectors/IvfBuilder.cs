using System.Buffers.Binary;
using OneIndex.Index;
using OneIndex.Kernels;
using OneIndex.Storage;

namespace OneIndex.Vectors;

/// <summary>How an inverted-file (IVF) index stores the vectors it scans.</summary>
public enum IvfStorage : byte
{
    /// <summary>Full-precision vectors inside the lists; probed lists are scored exactly.</summary>
    FullPrecision = 0,

    /// <summary>
    /// 8-bit codes (one scale per vector) inside the lists, and the full-precision vectors in a separate region at the end of the file.
    /// Probed lists are scored on the codes, then the best candidates are re-scored from the full-precision region.
    /// </summary>
    EightBit = 1,

    /// <summary>
    /// 8-bit codes inside the lists and no full-precision region: the file is smaller, probed lists are scored on the codes and the caller
    /// re-scores candidates from vectors it keeps elsewhere (a segment's vectors file). <see cref="IvfIndex.Search"/> returns code scores.
    /// </summary>
    EightBitCodes = 2,
}

/// <summary>Settings of <see cref="IvfBuilder"/>.</summary>
public sealed class IvfOptions
{
    /// <summary>The metric. Default <see cref="VectorMetric.Cosine"/>.</summary>
    public VectorMetric Metric { get; init; } = VectorMetric.Cosine;

    /// <summary>Number of clusters; 0 (default) picks about the square root of the vector count, from 1 to 4096.</summary>
    public int Lists { get; init; }

    /// <summary>Lloyd iterations of k-means on the training sample. Default 12.</summary>
    public int Iterations { get; init; } = 12;

    /// <summary>The seed of the centroid choice, so a build is reproducible. Default 1.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>How the lists hold vectors. Default <see cref="IvfStorage.FullPrecision"/>.</summary>
    public IvfStorage Storage { get; init; }

    /// <summary>Training sample per list: k-means runs on at most this many vectors times the list count. Default 64.</summary>
    public int TrainingPerList { get; init; } = 64;
}

/// <summary>
/// Builds an IVF index: k-means centroids (Lloyd's algorithm, spherical for cosine) and one posting list of vectors per cluster, so a
/// query reads only the few lists whose centroids are nearest (Jegou, Douze and Schmid, "Product quantization for nearest neighbor search",
/// TPAMI 2011, section 2's inverted file; Sivic and Zisserman, ICCV 2003, for the clustered lists). The file layout is in docs/ivf.md.
/// </summary>
public static class IvfBuilder
{
    internal const uint Magic = 0x4656494F; // "OIVF"
    internal const uint Version = 1;
    internal const int HeaderBytes = 48;
    internal const int DirectoryEntryBytes = 16;

    /// <summary>Builds the file image of an IVF index.</summary>
    /// <param name="vectors">The vectors, row after row, <c>count * dimensions</c> finite floats.</param>
    /// <param name="dimensions">The vector length, 1 to 65536.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <returns>The bytes of the index. Vector <c>i</c> is reported as id <c>i</c>.</returns>
    /// <exception cref="ArgumentException">The length is not a multiple of <paramref name="dimensions"/>, a value is not finite, or the index would be empty.</exception>
    public static byte[] Build(ReadOnlySpan<float> vectors, int dimensions, IvfOptions? options = null)
    {
        options ??= new IvfOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 65536);
        if (vectors.Length == 0 || vectors.Length % dimensions != 0)
        {
            throw new ArgumentException("The vectors must be a non-empty whole number of rows.", nameof(vectors));
        }

        int n = vectors.Length / dimensions;
        bool cosine = options.Metric == VectorMetric.Cosine;
        float[] data = vectors.ToArray();
        for (int i = 0; i < data.Length; i++)
        {
            if (!float.IsFinite(data[i]))
            {
                throw new ArgumentException("A vector holds a NaN or an infinity.", nameof(vectors));
            }
        }

        if (cosine)
        {
            for (int i = 0; i < n; i++)
            {
                VectorKernels.Normalize(data.AsSpan(i * dimensions, dimensions));
            }
        }

        int nlist = options.Lists > 0 ? options.Lists : (int)Math.Round(Math.Sqrt(n));
        nlist = Math.Clamp(nlist, 1, Math.Min(4096, n));
        float[] centroids = Train(data, n, dimensions, nlist, options);

        // Final assignment of every vector to its nearest centroid.
        var assign = new int[n];
        var counts = new int[nlist];
        for (int i = 0; i < n; i++)
        {
            int c = Nearest(centroids, nlist, dimensions, data.AsSpan(i * dimensions, dimensions), options.Metric);
            assign[i] = c;
            counts[c]++;
        }

        // Positions: lists in order, ids ascending inside a list, so the file is deterministic.
        var firstPos = new int[nlist];
        for (int c = 1; c < nlist; c++)
        {
            firstPos[c] = firstPos[c - 1] + counts[c - 1];
        }

        var order = new int[n];
        var fill = (int[])firstPos.Clone();
        for (int i = 0; i < n; i++)
        {
            order[fill[assign[i]]++] = i;
        }

        bool int8 = options.Storage != IvfStorage.FullPrecision;
        bool rerank = options.Storage == IvfStorage.EightBit;
        int codeBytes = int8 ? 4 + dimensions : dimensions * 4;
        long listsStart = HeaderBytes + ((long)nlist * dimensions * 4) + ((long)nlist * DirectoryEntryBytes);
        long total = listsStart + ((long)n * (4 + codeBytes)) + (rerank ? (long)n * dimensions * 4 : 0) + 8;
        if (total > int.MaxValue)
        {
            throw new ArgumentException("The index would be larger than 2 GiB.", nameof(vectors));
        }

        var file = new byte[total];
        Span<byte> f = file;
        BinaryPrimitives.WriteUInt32LittleEndian(f, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], (uint)dimensions);
        BinaryPrimitives.WriteUInt32LittleEndian(f[12..], (uint)options.Metric);
        BinaryPrimitives.WriteUInt32LittleEndian(f[16..], (uint)nlist);
        BinaryPrimitives.WriteUInt32LittleEndian(f[20..], (uint)n);
        BinaryPrimitives.WriteUInt32LittleEndian(f[24..], (uint)options.Storage);
        long rerankStart = listsStart + ((long)n * (4 + codeBytes));
        BinaryPrimitives.WriteInt64LittleEndian(f[32..], rerank ? rerankStart : 0);
        int at = HeaderBytes;
        for (int i = 0; i < nlist * dimensions; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(f[at..], centroids[i]);
            at += 4;
        }

        long listOffset = listsStart;
        for (int c = 0; c < nlist; c++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(f[at..], listOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(f[(at + 8)..], (uint)counts[c]);
            BinaryPrimitives.WriteUInt32LittleEndian(f[(at + 12)..], (uint)firstPos[c]);
            at += DirectoryEntryBytes;
            listOffset += (long)counts[c] * (4 + codeBytes);
        }

        for (int c = 0; c < nlist; c++)
        {
            // A list is its ids, then its vectors (or codes), each section contiguous so one range read fetches the list.
            for (int k = 0; k < counts[c]; k++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(f[at..], (uint)order[firstPos[c] + k]);
                at += 4;
            }

            for (int k = 0; k < counts[c]; k++)
            {
                ReadOnlySpan<float> v = data.AsSpan(order[firstPos[c] + k] * dimensions, dimensions);
                if (int8)
                {
                    float scale = Scale(v);
                    BinaryPrimitives.WriteSingleLittleEndian(f[at..], scale);
                    at += 4;
                    for (int d = 0; d < dimensions; d++)
                    {
                        f[at++] = (byte)Quantize(v[d], scale);
                    }
                }
                else
                {
                    for (int d = 0; d < dimensions; d++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(f[at..], v[d]);
                        at += 4;
                    }
                }
            }
        }

        if (rerank)
        {
            for (int p = 0; p < n; p++)
            {
                ReadOnlySpan<float> v = data.AsSpan(order[p] * dimensions, dimensions);
                for (int d = 0; d < dimensions; d++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(f[at..], v[d]);
                    at += 4;
                }
            }
        }

        uint crc = Crc32C.Compute(f[..at]);
        BinaryPrimitives.WriteUInt32LittleEndian(f[at..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(f[(at + 4)..], Magic);
        return file;
    }

    /// <summary>Builds the index and writes it as a new file in a storage, synced.</summary>
    /// <param name="storage">The storage.</param>
    /// <param name="name">The new file's name.</param>
    /// <param name="vectors">As for <see cref="Build"/>.</param>
    /// <param name="dimensions">As for <see cref="Build"/>.</param>
    /// <param name="options">As for <see cref="Build"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static void Write(IStorage storage, string name, ReadOnlySpan<float> vectors, int dimensions, IvfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        byte[] image = Build(vectors, dimensions, options);
        using IStorageWriter w = storage.Create(name, cancellationToken);
        w.Write(image, cancellationToken);
        w.Sync(cancellationToken);
    }

    internal static float Scale(ReadOnlySpan<float> v)
    {
        float max = 0;
        foreach (float x in v)
        {
            max = Math.Max(max, Math.Abs(x));
        }

        return max == 0 ? 1f : max / 127f;
    }

    internal static sbyte Quantize(float x, float scale) => (sbyte)Math.Clamp((int)MathF.Round(x / scale), -127, 127);

    private static float[] Train(float[] data, int n, int dims, int nlist, IvfOptions o)
    {
        var rng = new SplitMix64(o.Seed);
        int sampleSize = (int)Math.Min(n, Math.Max(nlist, (long)o.TrainingPerList * nlist));
        var sample = new int[n];
        for (int i = 0; i < n; i++)
        {
            sample[i] = i;
        }

        for (int i = 0; i < sampleSize; i++)
        {
            int j = i + (int)(rng.Next() % (ulong)(n - i));
            (sample[i], sample[j]) = (sample[j], sample[i]);
        }

        var centroids = new float[nlist * dims];
        for (int c = 0; c < nlist; c++)
        {
            data.AsSpan(sample[c] * dims, dims).CopyTo(centroids.AsSpan(c * dims, dims));
        }

        var sums = new float[nlist * dims];
        var cnt = new int[nlist];
        for (int it = 0; it < o.Iterations; it++)
        {
            Array.Clear(sums);
            Array.Clear(cnt);
            for (int s = 0; s < sampleSize; s++)
            {
                ReadOnlySpan<float> v = data.AsSpan(sample[s] * dims, dims);
                int c = Nearest(centroids, nlist, dims, v, o.Metric);
                cnt[c]++;
                Span<float> acc = sums.AsSpan(c * dims, dims);
                for (int d = 0; d < dims; d++)
                {
                    acc[d] += v[d];
                }
            }

            for (int c = 0; c < nlist; c++)
            {
                Span<float> cen = centroids.AsSpan(c * dims, dims);
                if (cnt[c] == 0)
                {
                    // An empty cluster takes a random training vector so no list is wasted.
                    data.AsSpan(sample[(int)(rng.Next() % (ulong)sampleSize)] * dims, dims).CopyTo(cen);
                    continue;
                }

                float inv = 1f / cnt[c];
                for (int d = 0; d < dims; d++)
                {
                    cen[d] = sums[(c * dims) + d] * inv;
                }

                if (o.Metric == VectorMetric.Cosine)
                {
                    VectorKernels.Normalize(cen);
                }
            }
        }

        return centroids;
    }

    internal static int Nearest(float[] centroids, int nlist, int dims, ReadOnlySpan<float> v, VectorMetric metric)
    {
        int best = 0;
        float bestScore = float.NegativeInfinity;
        for (int c = 0; c < nlist; c++)
        {
            ReadOnlySpan<float> cen = centroids.AsSpan(c * dims, dims);
            float s = metric == VectorMetric.Euclidean ? -VectorKernels.SquaredL2(v, cen) : VectorKernels.Dot(v, cen);
            if (s > bestScore)
            {
                bestScore = s;
                best = c;
            }
        }

        return best;
    }

    /// <summary>SplitMix64 (Steele, Lea and Flood, OOPSLA 2014): a seeded generator whose output is the same on every machine.</summary>
    internal struct SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
