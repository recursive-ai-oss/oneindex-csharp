using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OneIndex.VectorBench;

/// <summary>One index implementation under test: build over the base vectors, then answer queries at a chosen search breadth.</summary>
internal interface IEngine : IDisposable
{
    string Name { get; }

    /// <summary>Builds the index over <paramref name="count"/> row-major vectors. The build is single-threaded.</summary>
    void Build(float[] data, int count, int dimensions);

    /// <summary>The ids (row numbers) of the <paramref name="k"/> nearest vectors, nearest first, written to <paramref name="ids"/>.</summary>
    int Search(float[] query, int k, int ef, int[] ids);

    /// <summary>Bytes the index takes when saved, or -1 when the engine cannot say.</summary>
    long IndexBytes { get; }
}

internal static class Program
{
    private static int[] _efGrid = [10, 14, 20, 28, 40, 56, 80, 110, 140, 180, 220, 270, 320, 380, 450, 540, 640, 760, 900, 1100, 1300, 1800];

    private static int Main(string[] args)
    {
        string Option(string name, string fallback)
        {
            int i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        string dir = Option("dir", ".");
        string engineName = Option("engine", "oneindex");
        int count = int.Parse(Option("count", "100000"), CultureInfo.InvariantCulture);
        int queryCount = int.Parse(Option("queries", "1000"), CultureInfo.InvariantCulture);
        int m = int.Parse(Option("m", "16"), CultureInfo.InvariantCulture);
        int efc = int.Parse(Option("efc", "100"), CultureInfo.InvariantCulture);
        int passes = int.Parse(Option("passes", "3"), CultureInfo.InvariantCulture);
        string scratch = Option("scratch", Path.GetTempPath());
        const int K = 10;
        Engines.Quantization = Option("quant", "none") switch { "8" => OneIndex.Vectors.VectorQuantization.Scalar8, "4" => OneIndex.Vectors.VectorQuantization.Scalar4, "1" => OneIndex.Vectors.VectorQuantization.OneBit, _ => OneIndex.Vectors.VectorQuantization.None };
        OneIndex.Vectors.HnswBuilder.KeepPruned = Option("keep-pruned", "0") == "1";
        OneIndex.Vectors.HnswBuilder.Layer0Select = int.Parse(Option("select0", "0"), CultureInfo.InvariantCulture);
        OneIndex.Vectors.FloatNodeScorer.TouchLines = int.Parse(Option("touch", "1"), CultureInfo.InvariantCulture);
        OneIndex.Vectors.HnswBuilder.SimpleSelection = Option("simple", "0") == "1";

        JsonElement manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))).RootElement;
        int dims = manifest.GetProperty("dimensions").GetInt32();
        float[] data = ReadFloats(Path.Combine(dir, "base.f32"), (long)count * dims);
        float[][] queries = Enumerable.Range(0, queryCount).Select(i => ReadFloats(Path.Combine(dir, "queries.f32"), dims, (long)i * dims)).ToArray();
        int[] truth = ReadInts(Path.Combine(dir, "truth.i32"), (long)queryCount * 100);
        var exact = new HashSet<int>[queryCount];
        // Ground truth was computed over all base vectors of the set; with a smaller --count, recompute exactly over the prefix.
        bool prefix = count < manifest.GetProperty("count").GetInt32();
        int[][] truthTop = prefix ? ExactPrefix(data, count, dims, queries, K) : Enumerable.Range(0, queryCount).Select(q => truth.AsSpan(q * 100, K).ToArray()).ToArray();
        for (int q = 0; q < queryCount; q++)
        {
            exact[q] = [.. truthTop[q]];
        }

        if (Option("mode", "search") == "merge")
        {
            return MergeRun.Run(data, count, dims, queries, exact, m, efc, scratch);
        }

        using IEngine engine = Engines.Create(engineName, m, efc, scratch);
        long before = GC.GetTotalMemory(true);
        var clock = Stopwatch.StartNew();
        engine.Build(data, count, dims);
        double buildSeconds = clock.Elapsed.TotalSeconds;
        long managed = GC.GetTotalMemory(true) - before;
        long peakRss = Process.GetCurrentProcess().PeakWorkingSet64;
        Console.WriteLine($"# engine {engine.Name} n={count} d={dims} M={m} efConstruction={efc} build {buildSeconds:F1}s managed-after-build {managed >> 20} MB peak-RSS {peakRss >> 20} MB index-bytes {engine.IndexBytes} (data {(long)count * dims * 4 >> 20} MB)");

        if (Option("mode", "search") == "ab")
        {
            // Interleaved repeats of the same search under several touch settings, to separate a setting's effect from host noise.
            int abEf = int.Parse(Option("ef", "270"), CultureInfo.InvariantCulture);
            int[] settings = Option("touches", "0,1").Split(',').Select(int.Parse).ToArray();
            var samples = settings.ToDictionary(t => t, _ => new List<double>());
            var abIds = new int[K];
            for (int round = 0; round < int.Parse(Option("rounds", "7"), CultureInfo.InvariantCulture); round++)
            {
                foreach (int t in settings)
                {
                    OneIndex.Vectors.FloatNodeScorer.TouchLines = t;
                    long t0 = Stopwatch.GetTimestamp();
                    for (int q = 0; q < queryCount; q++)
                    {
                        engine.Search(queries[q], K, abEf, abIds);
                    }

                    samples[t].Add(queryCount / Stopwatch.GetElapsedTime(t0).TotalSeconds);
                }
            }

            foreach (int t in settings)
            {
                samples[t].Sort();
                Console.WriteLine($"@ab touch={t} ef={abEf} median qps={samples[t][samples[t].Count / 2]:F0} min={samples[t][0]:F0} max={samples[t][^1]:F0}");
            }

            return 0;
        }

        if (Option("grid", "") is { Length: > 0 } grid)
        {
            _efGrid = grid.Split(',').Select(int.Parse).ToArray();
        }

        foreach (int touch in Option("touches", Option("touch", "1")).Split(',').Select(int.Parse))
        {
        OneIndex.Vectors.FloatNodeScorer.TouchLines = touch;
        Console.WriteLine($"# touch {touch}");
        var ids = new int[K];
        var results = new List<(int Ef, double Recall, double Qps)>();
        foreach (int ef in _efGrid)
        {
            if (ef < K)
            {
                continue;
            }

            // Warm up, then take the median of timed passes over all queries.
            for (int q = 0; q < Math.Min(100, queryCount); q++)
            {
                engine.Search(queries[q], K, ef, ids);
            }

            var times = new List<double>();
            double recall = 0;
            for (int pass = 0; pass < passes; pass++)
            {
                long hits = 0;
                long start = Stopwatch.GetTimestamp();
                for (int q = 0; q < queryCount; q++)
                {
                    int n = engine.Search(queries[q], K, ef, ids);
                    if (pass == 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            hits += exact[q].Contains(ids[i]) ? 1 : 0;
                        }
                    }
                }

                times.Add(Stopwatch.GetElapsedTime(start).TotalSeconds);
                if (pass == 0)
                {
                    recall = hits / (double)(queryCount * K);
                }
            }

            times.Sort();
            double qps = queryCount / times[times.Count / 2];
            results.Add((ef, recall, qps));
            Console.WriteLine($"{engine.Name},{dims},{count},ef={ef},recall@10={recall:F4},qps={qps:F0}");
            if (recall >= 0.9995)
            {
                break;
            }
        }

        foreach (double target in new[] { 0.90, 0.95, 0.99 })
        {
            (int ef, double recall, double qps) = results.FirstOrDefault(r => r.Recall >= target);
            Console.WriteLine(ef == 0 ? $"@{target:F2}: not reached" : $"@{target:F2}: ef={ef} recall={recall:F4} qps={qps:F0}");
        }

        }

        return 0;
    }

    private static float[] ReadFloats(string path, long count, long offsetFloats = 0)
    {
        using FileStream f = File.OpenRead(path);
        f.Seek(offsetFloats * 4, SeekOrigin.Begin);
        var bytes = new byte[count * 4];
        f.ReadExactly(bytes);
        var floats = new float[count];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static int[] ReadInts(string path, long count)
    {
        using FileStream f = File.OpenRead(path);
        var bytes = new byte[count * 4];
        f.ReadExactly(bytes);
        var ints = new int[count];
        Buffer.BlockCopy(bytes, 0, ints, 0, bytes.Length);
        return ints;
    }

    // Exact top-k by dot product over the first `count` vectors (vectors have length 1, so this is the cosine ranking).
    private static int[][] ExactPrefix(float[] data, int count, int dims, float[][] queries, int k)
    {
        var result = new int[queries.Length][];
        Parallel.For(0, queries.Length, q =>
        {
            var best = new PriorityQueue<int, float>();
            for (int i = 0; i < count; i++)
            {
                float dot = Dot(queries[q], data.AsSpan(i * dims, dims));
                if (best.Count < k)
                {
                    best.Enqueue(i, dot);
                }
                else if (best.TryPeek(out _, out float worst) && dot > worst)
                {
                    best.Dequeue();
                    best.Enqueue(i, dot);
                }
            }

            var order = new List<(int Id, float Dot)>();
            while (best.TryDequeue(out int id, out float p))
            {
                order.Add((id, p));
            }

            result[q] = [.. order.OrderByDescending(x => x.Dot).Select(x => x.Id)];
        });
        return result;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
