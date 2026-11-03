using System.Diagnostics;
using System.Globalization;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.VectorBench;

/// <summary>
/// Inverted-file rows: for each storage layout (full precision, 8-bit codes with float rerank) and each probe count, recall@10 against the
/// exact nearest neighbours, queries per second on one thread (median of the passes), and the size of the index file.
/// </summary>
internal static class IvfRun
{
    public static int Run(float[] data, int count, int dims, float[][] queries, HashSet<int>[] exact, string probeList, int lists, int oversample, int passes, string scratch)
    {
        const int K = 10;
        int[] probes = probeList.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        foreach (IvfStorage storage in new[] { IvfStorage.FullPrecision, IvfStorage.EightBit })
        {
            string file = Path.Combine(scratch, $"bench-{storage}.ivf");
            var local = new FileSystemStorage(scratch);
            long rssBefore = Process.GetCurrentProcess().WorkingSet64;
            var clock = Stopwatch.StartNew();
            IvfBuilder.Write(local, Path.GetFileName(file), data.AsSpan(0, count * dims), dims, new IvfOptions { Storage = storage, Lists = lists, Seed = 1 });
            double build = clock.Elapsed.TotalSeconds;
            long size = new FileInfo(file).Length;
            using IvfIndex index = IvfIndex.Open(local.OpenRead(Path.GetFileName(file)));
            Console.WriteLine($"# ivf {storage} n={count} d={dims} lists={index.Lists} oversample={oversample} build {build:F1}s file {size >> 10} KiB ({(double)size / ((long)count * dims * 4):F2}x the float vectors) peak-RSS {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB (before build {rssBefore >> 20} MB)");
            foreach (int nprobe in probes)
            {
                for (int q = 0; q < Math.Min(100, queries.Length); q++)
                {
                    index.Search(queries[q], K, nprobe, null, oversample);
                }

                var seconds = new List<double>();
                double recall = 0;
                for (int pass = 0; pass < passes; pass++)
                {
                    long hits = 0;
                    long start = Stopwatch.GetTimestamp();
                    for (int q = 0; q < queries.Length; q++)
                    {
                        IvfHit[] r = index.Search(queries[q], K, nprobe, null, oversample);
                        if (pass == 0)
                        {
                            hits += r.Count(h => exact[q].Contains(h.Id));
                        }
                    }

                    seconds.Add(Stopwatch.GetElapsedTime(start).TotalSeconds);
                    if (pass == 0)
                    {
                        recall = (double)hits / (queries.Length * K);
                    }
                }

                seconds.Sort();
                double qps = queries.Length / seconds[seconds.Count / 2];
                Console.WriteLine($"ivf\t{storage}\tnprobe={nprobe}\trecall@10={recall:F4}\tqps={qps:F0}\tscanned-lists={100.0 * nprobe / index.Lists:F1}%");
            }
        }

        return 0;
    }
}
