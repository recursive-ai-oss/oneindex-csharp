using OneIndex.Aggregations;

namespace OneIndex.Tests;

public sealed class AggregationSketchTests(ITestOutputHelper output)
{
    private static HyperLogLogSketch Fill(int precision, long from, long count)
    {
        var s = new HyperLogLogSketch(precision);
        for (long i = from; i < from + count; i++)
        {
            s.AddHash(HyperLogLogSketch.Hash(i));
        }

        return s;
    }

    [Fact]
    public void HyperLogLogIsExactWhileSparseAndWithinStandardErrorAfter()
    {
        // Sparse form (up to 2^p / 8 distinct hashes) counts exactly; dense form has standard error 1.04 / sqrt(2^p).
        foreach (int p in new[] { 8, 12, 14, 16 })
        {
            double se = 1.04 / Math.Sqrt(1 << p);
            double worst = 0;
            foreach (long n in new long[] { 1, 10, 100, (1 << p) / 8, 3L << p >> 1, 1L << (p + 2), 20L << p })
            {
                for (int trial = 0; trial < 6; trial++)
                {
                    long est = Fill(p, trial * 10_000_000L, n).Estimate();
                    double err = Math.Abs(est - n) / (double)n;
                    if (n <= (1 << p) / 8)
                    {
                        Assert.Equal(n, est);
                    }
                    else
                    {
                        worst = Math.Max(worst, err / se);
                        Assert.True(err <= 4 * se, $"p={p} n={n}: estimate {est}, error {err:P2} > 4 standard errors ({se:P2})");
                    }
                }
            }

            output.WriteLine($"p={p}: worst dense error {worst:F2} standard errors");
        }
    }

    [Fact]
    public void HyperLogLogMergeIsTheUnionAndRepeatsDoNotCount()
    {
        HyperLogLogSketch a = Fill(14, 0, 60_000);
        HyperLogLogSketch b = Fill(14, 40_000, 60_000); // 20,000 in common
        a.Merge(b);
        Assert.InRange(a.Estimate(), 100_000 * 0.97, 100_000 * 1.03);
        HyperLogLogSketch s = new(14);
        for (int rep = 0; rep < 5; rep++)
        {
            for (long i = 0; i < 500; i++)
            {
                s.AddHash(HyperLogLogSketch.Hash(i));
            }
        }

        Assert.Equal(500, s.Estimate());
        HyperLogLogSketch small = Fill(14, 0, 100);
        small.Merge(Fill(14, 50, 100));
        Assert.Equal(150, small.Estimate()); // sparse with sparse stays exact
        Assert.Equal(0, new HyperLogLogSketch(14).Estimate());
    }

    [Fact]
    public void HashOfBytesSeparatesStringsAndIsStable()
    {
        Assert.NotEqual(HyperLogLogSketch.Hash("abc"u8), HyperLogLogSketch.Hash("abd"u8));
        Assert.NotEqual(HyperLogLogSketch.Hash("a"u8), HyperLogLogSketch.Hash("a\0"u8));
        Assert.Equal(HyperLogLogSketch.Hash("日本語テキスト"u8), HyperLogLogSketch.Hash("日本語テキスト"u8));
        // Results must not vary between runs: the hash uses no per-process seed.
        Assert.Equal(HyperLogLogSketch.Hash("OneIndex"u8), HyperLogLogSketch.Hash("OneIndex"u8.ToArray()));
    }

    private static double RankError(double[] sorted, double value, double q)
    {
        int lo = Array.BinarySearch(sorted, value);
        int below;
        int atOrBelow;
        if (lo >= 0)
        {
            below = lo;
            while (below > 0 && sorted[below - 1] == value)
            {
                below--;
            }

            atOrBelow = lo + 1;
            while (atOrBelow < sorted.Length && sorted[atOrBelow] == value)
            {
                atOrBelow++;
            }
        }
        else
        {
            below = atOrBelow = ~lo;
        }

        double target = q * sorted.Length;
        double err = target < below ? below - target : target > atOrBelow ? target - atOrBelow : 0;
        return err / sorted.Length;
    }

    [Fact]
    public void TDigestKeepsRankErrorSmallAndTailsTight()
    {
        double[] qs = [0.0001, 0.001, 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 0.99, 0.999, 0.9999];
        foreach (string shape in new[] { "uniform", "lognormal", "sorted", "few-distinct" })
        {
            var r = new Random(42);
            const int N = 300_000;
            var data = new double[N];
            for (int i = 0; i < N; i++)
            {
                data[i] = shape switch
                {
                    "uniform" => r.NextDouble() * 1000,
                    "lognormal" => Math.Exp(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble())),
                    "sorted" => i * 0.5,
                    _ => r.Next(7),
                };
            }

            var digest = new TDigest(200);
            foreach (double v in data)
            {
                digest.Add(v);
            }

            double[] sorted = [.. data];
            Array.Sort(sorted);
            double worst = 0;
            double worstTail = 0;
            foreach (double q in qs)
            {
                double e = RankError(sorted, digest.Quantile(q), q);
                worst = Math.Max(worst, e);
                if (q <= 0.001 || q >= 0.999)
                {
                    worstTail = Math.Max(worstTail, e);
                }
            }

            output.WriteLine($"{shape}: worst rank error {worst:P3}, tails {worstTail:P4}");
            Assert.True(worst <= 0.005, $"{shape}: rank error {worst:P3}");
            Assert.True(worstTail <= 0.0005, $"{shape}: tail rank error {worstTail:P4}");
            Assert.Equal(sorted[0], digest.Quantile(0));
            Assert.Equal(sorted[^1], digest.Quantile(1));
        }
    }

    [Fact]
    public void TDigestMergeAndSmallInputsBehave()
    {
        var a = new TDigest(100);
        var b = new TDigest(100);
        var r = new Random(7);
        var all = new List<double>();
        for (int i = 0; i < 50_000; i++)
        {
            double v = r.NextDouble();
            (i % 2 == 0 ? a : b).Add(v);
            all.Add(v);
        }

        a.Merge(b);
        double[] sorted = [.. all.Order()];
        foreach (double q in new[] { 0.01, 0.5, 0.99 })
        {
            Assert.True(RankError(sorted, a.Quantile(q), q) < 0.01);
        }

        Assert.Equal(50_000, a.Count);
        var one = new TDigest(100);
        Assert.True(double.IsNaN(one.Quantile(0.5)));
        one.Add(5);
        Assert.Equal(5, one.Quantile(0.5));
        one.Add(double.NaN); // ignored
        Assert.Equal(1, one.Count);
        // Equal inputs give equal digests.
        var x = new TDigest(100);
        var y = new TDigest(100);
        for (int i = 0; i < 5000; i++)
        {
            x.Add((i * 37) % 101);
            y.Add((i * 37) % 101);
        }

        Assert.Equal(x.Quantile(0.3), y.Quantile(0.3));
    }
}
