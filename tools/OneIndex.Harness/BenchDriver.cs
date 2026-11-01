using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OneIndex.Harness;

/// <summary>Runs one row's queries against an engine. Shared by the corpus driver and the BenchmarkDotNet suite so both measure the same work.</summary>
public static class Rows
{
    /// <summary>Hits requested per query.</summary>
    public const int TopK = 10;

    /// <summary>Runs every query once. Returns the sum of match counts, which engines with matched analysis agree on for unscored rows.</summary>
    public static long RunPass(ITextEngine engine, HarnessQuery[] queries)
    {
        long total = 0;
        foreach (var q in queries)
        {
            var r = engine.Search(q, TopK);
            total += r.Total + r.Hits.Length + (r.Facets?.Length ?? 0);
        }

        return total;
    }
}

/// <summary>Options of the corpus driver.</summary>
/// <param name="Engine">Engine name.</param>
/// <param name="Corpus">Path of the abstracts JSON-lines file.</param>
/// <param name="Docs">Documents to index.</param>
/// <param name="WorkDir">Directory for the index.</param>
/// <param name="OutFile">Result JSON path.</param>
/// <param name="Seed">Query generator seed.</param>
/// <param name="QueriesPerRow">Queries per row.</param>
/// <param name="Reps">Timed passes per query row (the median is reported).</param>
/// <param name="Threads">Indexing threads.</param>
/// <param name="PageCache">Text recorded for the page-cache state.</param>
public sealed record DriverOptions(string Engine, string Corpus, int Docs, string WorkDir, string OutFile, int Seed, int QueriesPerRow, int Reps, int Threads, string PageCache);

/// <summary>
/// The corpus driver: indexes the Wikipedia abstracts (Standard analysis: standard tokenization, lowercase, no stemming), times a
/// bulk commit and five small commits, then times every query row, recording peak resident memory and index size per row
/// with the machine record. Method: docs/benchmarks.md.
/// </summary>
public static class BenchDriver
{
    private const int CommitBatches = 5;
    private const int CommitBatchSize = 200;

    /// <summary>Runs the driver and writes the result JSON; returns a process exit code.</summary>
    public static int Run(DriverOptions o)
    {
        using var engine = Engines.Create(o.Engine);
        if (!engine.IsAvailable(out var reason))
        {
            Console.Error.WriteLine($"{engine.Name} is not available: {reason}");
            return 3;
        }

        var machine = MachineRecord.Capture(o.WorkDir);
        var loadBefore = MachineRecord.LoadAverage();
        var docs = WikiCorpus.Read(o.Corpus, o.Docs);
        Console.Error.WriteLine($"loaded {docs.Count} abstracts");
        var queries = QueryGenerator.Generate(docs, o.QueriesPerRow, o.Seed);

        var indexDir = Path.Combine(o.WorkDir, $"{engine.Name.ToLowerInvariant()}-index");
        if (Directory.Exists(indexDir))
        {
            Directory.Delete(indexDir, true);
        }

        var rows = new List<Dictionary<string, object?>>();
        var loads = new List<string>();

        // Indexing throughput: everything but the last batches, then one commit.
        var bulk = docs.Count - (CommitBatches * CommitBatchSize);
        if (bulk <= 0)
        {
            throw new ArgumentException($"At least {CommitBatches * CommitBatchSize + 1} documents are needed.");
        }

        engine.Create(new EngineSettings(indexDir, AnalysisKind.Standard, IndexProfile.Bench));
        GC.Collect();
        PeakMemory.Reset();
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        if (o.Threads > 1)
        {
            Parallel.For(0, bulk, new ParallelOptions { MaxDegreeOfParallelism = o.Threads }, i => engine.Add(docs[i]));
        }
        else
        {
            for (var i = 0; i < bulk; i++)
            {
                engine.Add(docs[i]);
            }
        }

        var addSeconds = sw.Elapsed.TotalSeconds;
        var commitWatch = Stopwatch.StartNew();
        engine.Commit();
        var bulkCommitMs = commitWatch.Elapsed.TotalMilliseconds;
        var indexSeconds = sw.Elapsed.TotalSeconds;
        rows.Add(new()
        {
            ["row"] = "index",
            ["docs"] = bulk,
            ["docsPerSecond"] = bulk / indexSeconds,
            ["addSeconds"] = addSeconds,
            ["commitMs"] = bulkCommitMs,
            ["allocBytesPerDoc"] = (GC.GetTotalAllocatedBytes(true) - allocBefore) / (double)bulk,
            ["peakRssMiB"] = PeakMemory.Bytes() / 1048576.0,
            ["indexBytes"] = engine.IndexSizeBytes(),
        });
        loads.Add(MachineRecord.LoadAverage());
        Console.Error.WriteLine($"index: {bulk / indexSeconds:F0} docs/s");

        // Commit latency: a small batch of documents, then a timed commit.
        var latencies = new List<double>();
        PeakMemory.Reset();
        for (var b = 0; b < CommitBatches; b++)
        {
            for (var i = 0; i < CommitBatchSize; i++)
            {
                engine.Add(docs[bulk + (b * CommitBatchSize) + i]);
            }

            var t = Stopwatch.StartNew();
            engine.Commit();
            latencies.Add(t.Elapsed.TotalMilliseconds);
        }

        latencies.Sort();
        rows.Add(new()
        {
            ["row"] = "commit",
            ["batchDocs"] = CommitBatchSize,
            ["medianMs"] = latencies[latencies.Count / 2],
            ["maxMs"] = latencies[^1],
            ["peakRssMiB"] = PeakMemory.Bytes() / 1048576.0,
            ["indexBytes"] = engine.IndexSizeBytes(),
        });
        loads.Add(MachineRecord.LoadAverage());

        engine.OpenSearcher();
        // ONEINDEX_ROWS=Prefix,Fuzzy1 limits the timed query rows (comma-separated row names).
        var only = Environment.GetEnvironmentVariable("ONEINDEX_ROWS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (kind, set) in queries)
        {
            if (only is not null && !only.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!engine.Supports(set[0].GetType()))
            {
                rows.Add(new() { ["row"] = kind.ToString(), ["skipped"] = "unsupported by engine" });
                continue;
            }

            Rows.RunPass(engine, set); // warm-up, not timed
            var timings = new List<double>();
            long checksum = 0, allocated = 0;
            PeakMemory.Reset();
            for (var rep = 0; rep < o.Reps; rep++)
            {
                GC.Collect();
                var a0 = GC.GetTotalAllocatedBytes(true);
                var t = Stopwatch.StartNew();
                checksum = Rows.RunPass(engine, set);
                timings.Add(t.Elapsed.TotalMilliseconds);
                allocated = GC.GetTotalAllocatedBytes(true) - a0;
            }

            timings.Sort();
            var medianMs = timings[timings.Count / 2];
            rows.Add(new()
            {
                ["row"] = kind.ToString(),
                ["queries"] = set.Length,
                ["medianMsPerQuery"] = medianMs / set.Length,
                ["queriesPerSecond"] = set.Length / (medianMs / 1000.0),
                ["allocBytesPerQuery"] = allocated / (double)set.Length,
                ["matchChecksum"] = checksum,
                ["peakRssMiB"] = PeakMemory.Bytes() / 1048576.0,
                ["indexBytes"] = engine.IndexSizeBytes(),
            });
            loads.Add(MachineRecord.LoadAverage());
            Console.Error.WriteLine($"{kind}: {medianMs / set.Length:F3} ms/query");
        }

        var result = new Dictionary<string, object?>
        {
            ["engine"] = engine.Name,
            ["engineVersion"] = engine.Version,
            ["analysis"] = "Standard: UAX #29-style word tokens, lowercase, no stop words, no stemming",
            ["commitSettings"] = "one bulk commit, then 5 commits after 200 added documents each; write buffer 256 MiB; default merge policy",
            ["documents"] = docs.Count,
            ["seed"] = o.Seed,
            ["queriesPerRow"] = o.QueriesPerRow,
            ["repetitions"] = o.Reps,
            ["indexingThreads"] = o.Threads,
            ["topK"] = Rows.TopK,
            ["pageCache"] = o.PageCache,
            ["machine"] = machine,
            ["loadBefore"] = loadBefore,
            ["loadDuring"] = loads,
            ["loadAfter"] = MachineRecord.LoadAverage(),
            ["rows"] = rows,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.OutFile))!);
        File.WriteAllText(o.OutFile, JsonSerializer.Serialize(result, JsonOptions.Indented));
        Console.Error.WriteLine($"wrote {o.OutFile}");
        return 0;
    }
}
