using OneIndex.Index;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Tests;

public class IvfTests
{
    private static float[] Clustered(int n, int dims, int clusters, int seed)
    {
        var rng = new Random(seed);
        var centers = new float[clusters][];
        for (int c = 0; c < clusters; c++)
        {
            centers[c] = new float[dims];
            for (int d = 0; d < dims; d++)
            {
                centers[c][d] = (float)(rng.NextDouble() * 2 - 1);
            }
        }

        var data = new float[n * dims];
        for (int i = 0; i < n; i++)
        {
            float[] cen = centers[rng.Next(clusters)];
            for (int d = 0; d < dims; d++)
            {
                data[(i * dims) + d] = cen[d] + (float)((rng.NextDouble() - 0.5) * 0.6);
            }
        }

        return data;
    }

    private static int[] Exact(float[] data, int dims, float[] q, int k, VectorMetric metric)
    {
        int n = data.Length / dims;
        float qn = MathF.Sqrt(q.Sum(static x => x * x));
        var scores = new (double S, int I)[n];
        for (int i = 0; i < n; i++)
        {
            double dot = 0, nv = 0, d2 = 0;
            for (int d = 0; d < dims; d++)
            {
                double v = data[(i * dims) + d];
                dot += v * q[d];
                nv += v * v;
                d2 += (v - q[d]) * (v - q[d]);
            }

            scores[i] = (metric switch { VectorMetric.Cosine => dot / (Math.Sqrt(nv) * qn), VectorMetric.DotProduct => dot, _ => -d2 }, i);
        }

        return [.. scores.OrderByDescending(static s => s.S).ThenBy(static s => s.I).Take(k).Select(static s => s.I)];
    }

    [Theory]
    [InlineData(VectorMetric.Cosine, IvfStorage.FullPrecision)]
    [InlineData(VectorMetric.DotProduct, IvfStorage.FullPrecision)]
    [InlineData(VectorMetric.Euclidean, IvfStorage.FullPrecision)]
    [InlineData(VectorMetric.Cosine, IvfStorage.EightBit)]
    public void FullProbeEqualsExactAndRecallGrowsWithProbes(VectorMetric metric, IvfStorage storage)
    {
        const int dims = 32;
        float[] data = Clustered(3000, dims, 40, 7);
        byte[] image = IvfBuilder.Build(data, dims, new IvfOptions { Metric = metric, Storage = storage, Lists = 40, Seed = 3 });
        var mem = new MemoryStorage();
        using (IStorageWriter w = mem.Create("v.ivf"))
        {
            w.Write(image);
        }

        using IvfIndex index = IvfIndex.Open(mem.OpenRead("v.ivf"));
        Assert.Equal(3000, index.Count);
        Assert.Equal(40, index.Lists);

        var rng = new Random(11);
        double recallLow = 0, recallFull = 0;
        const int queries = 40;
        for (int t = 0; t < queries; t++)
        {
            int row = rng.Next(3000);
            var q = new float[dims];
            for (int d = 0; d < dims; d++)
            {
                q[d] = data[(row * dims) + d] + (float)((rng.NextDouble() - 0.5) * 0.2);
            }

            int[] truth = Exact(data, dims, q, 10, metric);
            recallLow += truth.Intersect(index.Search(q, 10, nprobe: 2).Select(static h => h.Id)).Count() / 10.0;
            int[] full = index.Search(q, 10, nprobe: 40, oversample: 8).Select(static h => h.Id).ToArray();
            recallFull += truth.Intersect(full).Count() / 10.0;
            if (storage == IvfStorage.FullPrecision)
            {
                Assert.Equal(truth, full);
            }
        }

        Assert.True(recallFull / queries >= (storage == IvfStorage.FullPrecision ? 0.999 : 0.95), $"full-probe recall {recallFull / queries}");
        Assert.True(recallLow / queries <= recallFull / queries + 1e-9);
    }

    [Fact]
    public async Task AsyncMatchesSyncAndFilterIsApplied()
    {
        const int dims = 16;
        float[] data = Clustered(800, dims, 10, 5);
        var mem = new MemoryStorage();
        IvfBuilder.Write(mem, "a.ivf", data, dims, new IvfOptions { Storage = IvfStorage.EightBit });
        using IvfIndex index = await IvfIndex.OpenAsync(await mem.OpenReadAsync("a.ivf"));
        float[] q = data.AsSpan(0, dims).ToArray();
        IvfHit[] sync = index.Search(q, 10, 5);
        IvfHit[] async = await index.SearchAsync(q, 10, 5);
        Assert.Equal(sync, async);
        IvfHit[] odd = index.Search(q, 10, 10, static id => id % 2 == 1);
        Assert.NotEmpty(odd);
        Assert.All(odd, static h => Assert.True(h.Id % 2 == 1));
        Assert.True(index.BytesRead > 0);
    }

    [Fact]
    public void BuildsAreDeterministic()
    {
        float[] data = Clustered(500, 8, 5, 2);
        Assert.Equal(IvfBuilder.Build(data, 8), IvfBuilder.Build(data, 8));
    }

    [Fact]
    public void DamagedFilesAreRefusedWithTypedExceptions()
    {
        float[] data = Clustered(300, 8, 5, 4);
        byte[] image = IvfBuilder.Build(data, 8);
        static IvfIndex OpenBytes(byte[] b)
        {
            var mem = new MemoryStorage();
            using (IStorageWriter w = mem.Create("x"))
            {
                w.Write(b);
            }

            return IvfIndex.Open(mem.OpenRead("x"));
        }

        OpenBytes(image).Dispose();
        foreach (int cut in new[] { 0, 7, 40, image.Length / 2, image.Length - 1 })
        {
            Assert.IsAssignableFrom<CorruptIndexException>(Record.Exception(() => OpenBytes(image.AsSpan(0, cut).ToArray())));
        }

        var rng = new Random(9);
        for (int i = 0; i < 300; i++)
        {
            byte[] bad = (byte[])image.Clone();
            bad[rng.Next(bad.Length)] ^= (byte)(1 << rng.Next(8));
            Exception? e = Record.Exception(() => OpenBytes(bad).Dispose());
            Assert.True(e is CorruptIndexException, $"flip {i}: {e?.GetType().Name ?? "no exception"}");
        }
    }
}

public class MaxSimTests
{
    private sealed record Doc(string Id, byte[]? Tokens);

    [Fact]
    public void MatchesBruteForceOnGeneratedData()
    {
        var rng = new Random(21);
        int cases = 0;
        for (int t = 0; t < 200; t++)
        {
            int dim = 1 + rng.Next(12);
            int nq = 1 + rng.Next(6);
            var query = Enumerable.Range(0, nq).Select(_ => Vec(rng, dim)).ToList();
            var docs = new List<float[][]>();
            for (int d = 0; d < 1 + rng.Next(8); d++)
            {
                docs.Add([.. Enumerable.Range(0, rng.Next(0, 9)).Select(_ => Vec(rng, dim))]);
            }

            for (int d = 0; d < docs.Count; d++)
            {
                double expect = 0;
                foreach (float[] q in query)
                {
                    double best = double.NegativeInfinity;
                    foreach (float[] v in docs[d])
                    {
                        best = Math.Max(best, Cos(q, v));
                    }

                    if (docs[d].Length > 0)
                    {
                        expect += best;
                    }
                }

                float[] flatQ = query.SelectMany(static v => v).ToArray();
                float[] flatD = docs[d].SelectMany(static v => v).ToArray();
                Assert.Equal(expect, MaxSim.Score(flatQ, flatD, dim), 4);
                float[] round = MultiVector.Unpack(MultiVector.Pack(docs[d]), out int count, out int dd);
                Assert.Equal(docs[d].Length, count);
                Assert.Equal(flatD, round);
                _ = dd;
                cases++;
            }
        }

        Assert.True(cases > 500);
    }

    private static float[] Vec(Random rng, int dim) => [.. Enumerable.Range(0, dim).Select(_ => (float)((rng.NextDouble() * 2) - 1))];

    private static double Cos(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    [Fact]
    public void UnpackRefusesDamagedBytes()
    {
        byte[] packed = MultiVector.Pack([[1f, 2f], [3f, 4f]]);
        Assert.Throws<FormatException>(() => MultiVector.Unpack(packed.AsSpan(0, 10).ToArray(), out _, out _));
        Assert.Throws<FormatException>(() => MultiVector.Unpack(packed.AsSpan(0, 4).ToArray(), out _, out _));
        packed[0] = 99;
        Assert.Throws<FormatException>(() => MultiVector.Unpack(packed, out _, out _));
    }
}
