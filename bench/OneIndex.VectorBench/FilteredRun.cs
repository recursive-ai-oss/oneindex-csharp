using System.Diagnostics;
using System.Globalization;
using OneIndex.Codecs;
using OneIndex.Vectors;

namespace OneIndex.VectorBench;

/// <summary>
/// Filtered nearest-neighbour rows: recall@10 against the exact search over the documents that pass the filter, and queries per second,
/// for the filtered graph walk (with and without the exact-scan switch) and for naive post-filtering (search a larger k, then drop the
/// documents that fail). One uncorrelated filter (a hash of the document number) and one correlated filter (the lowest slice of
/// the projection on a fixed random direction, so the filter selects a region of the space and a query's neighbours are mostly
/// inside it or mostly outside it).
/// </summary>
internal static class FilteredRun
{
    public static int Run(float[] data, int count, int dims, float[][] queries, string selectivityList, string efList, float flatFactor, int cap, int m, int efc, string scratch)
    {
        const int K = 10;
        double[] selectivities = selectivityList.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        int[] efs = efList.Split(',').Select(int.Parse).ToArray();
        using var engine = new OneIndexSegment(m, efc, scratch);
        var clock = Stopwatch.StartNew();
        engine.Build(data, count, dims);
        Console.WriteLine($"# built n={count} d={dims} quant={Engines.Quantization} in {clock.Elapsed.TotalSeconds:F1}s, index bytes {engine.IndexBytes}, peak RSS {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");

        // The correlated key: projection on a fixed random direction.
        var rng = new Random(7);
        var dir = new float[dims];
        for (int j = 0; j < dims; j++)
        {
            dir[j] = (float)(rng.NextDouble() - 0.5);
        }

        var proj = new float[count];
        for (int i = 0; i < count; i++)
        {
            float p = 0;
            for (int j = 0; j < dims; j++)
            {
                p += data[(i * dims) + j] * dir[j];
            }

            proj[i] = p;
        }

        float[] sorted = [.. proj];
        Array.Sort(sorted);
        var ids = new int[10_000];
        foreach (string kind in new[] { "uncorrelated", "correlated" })
        {
            foreach (double s in selectivities)
            {
                var bits = new ulong[(count + 63) >> 6];
                int accepted = 0;
                float threshold = sorted[Math.Max(0, (int)(s * count) - 1)];
                for (int i = 0; i < count; i++)
                {
                    bool pass = kind == "uncorrelated" ? (((uint)((i * 2654435761L) >> 7) % 1_000_000) < s * 1_000_000) : proj[i] <= threshold;
                    if (pass)
                    {
                        bits[i >> 6] |= 1UL << (i & 63);
                        accepted++;
                    }
                }

                // Exact filtered truth: brute force over the passing vectors.
                int[][] truth = new int[queries.Length][];
                Parallel.For(0, queries.Length, q =>
                {
                    var best = new PriorityQueue<int, float>();
                    for (int i = 0; i < count; i++)
                    {
                        if ((bits[i >> 6] & (1UL << (i & 63))) == 0)
                        {
                            continue;
                        }

                        float dot = 0;
                        ReadOnlySpan<float> v = data.AsSpan(i * dims, dims);
                        for (int j = 0; j < dims; j++)
                        {
                            dot += queries[q][j] * v[j];
                        }

                        if (best.Count < K)
                        {
                            best.Enqueue(i, dot);
                        }
                        else if (best.TryPeek(out _, out float worst) && dot > worst)
                        {
                            best.Dequeue();
                            best.Enqueue(i, dot);
                        }
                    }

                    var list = new List<int>();
                    while (best.TryDequeue(out int id, out _))
                    {
                        list.Add(id);
                    }

                    truth[q] = [.. list];
                });
                var truthSets = truth.Select(t => new HashSet<int>(t)).ToArray();
                int possible = truthSets.Sum(t => t.Count);

                foreach (int ef in efs)
                {
                    foreach (string algo in (Environment.GetEnvironmentVariable("FILTERED_ALGOS") ?? "acorn+switch,acorn-only,flat,postfilter").Split(','))
                    {
                        VectorFieldReader.FlatSwitchFactor = algo == "acorn+switch" ? flatFactor : algo == "flat" ? 1e9 : 0;
                        HnswSearch.ExpansionCap = algo.StartsWith("acorn-t", StringComparison.Ordinal) ? int.Parse(algo[7..]) : cap;
                        int Query(float[] q)
                        {
                            if (algo != "postfilter")
                            {
                                return engine.SearchFiltered(q, K, ef, bits, ids);
                            }

                            // Naive post-filtering: ask for 4 K / selectivity candidates (at most 10,000), keep those that pass.
                            int ask = (int)Math.Min(10_000, Math.Ceiling(4 * K / s));
                            int n = engine.SearchFiltered(q, ask, Math.Max(ef, ask), null, ids);
                            int kept = 0;
                            for (int i = 0; i < n && kept < K; i++)
                            {
                                if ((bits[ids[i] >> 6] & (1UL << (ids[i] & 63))) != 0)
                                {
                                    ids[kept++] = ids[i];
                                }
                            }

                            return kept;
                        }

                        for (int w = 0; w < Math.Min(50, queries.Length); w++)
                        {
                            Query(queries[w]);
                        }

                        long hits = 0;
                        var times = new List<double>();
                        for (int pass = 0; pass < 3; pass++)
                        {
                            long start = Stopwatch.GetTimestamp();
                            for (int q = 0; q < queries.Length; q++)
                            {
                                int n = Query(queries[q]);
                                if (pass == 0)
                                {
                                    for (int i = 0; i < n; i++)
                                    {
                                        hits += truthSets[q].Contains(ids[i]) ? 1 : 0;
                                    }
                                }
                            }

                            times.Add(Stopwatch.GetElapsedTime(start).TotalSeconds);
                        }

                        times.Sort();
                        Console.WriteLine($"filtered,{kind},sel={s},accepted={accepted},ef={ef},{algo},recall@10={hits / (double)Math.Max(1, possible):F4},qps={queries.Length / times[1]:F0}");
                    }
                }
            }
        }

        VectorFieldReader.FlatSwitchFactor = 20;
        return 0;
    }
}
