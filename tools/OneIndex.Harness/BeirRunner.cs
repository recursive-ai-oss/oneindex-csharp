using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OneIndex.Harness;

/// <summary>One BM25 configuration of a relevance run.</summary>
/// <param name="Layout">How title and text are scored.</param>
/// <param name="K1">BM25 k1.</param>
/// <param name="B">BM25 b.</param>
public sealed record Bm25Setting(FieldLayout Layout, float K1, float B)
{
    /// <summary>File-name-safe name of the setting.</summary>
    public string Name => string.Create(CultureInfo.InvariantCulture, $"{Layout}.k1_{K1}_b_{B}");
}

/// <summary>The measured outcome of one run file.</summary>
/// <param name="Run">Run name.</param>
/// <param name="Ndcg10">nDCG@10 of the raw top-k.</param>
/// <param name="Ndcg10Beir">nDCG@10 after dropping hits whose document id equals the query id (BEIR's default evaluation).</param>
/// <param name="Queries">Queries evaluated.</param>
/// <param name="SearchSeconds">Wall time to answer all queries.</param>
public sealed record RunOutcome(string Run, double Ndcg10, double Ndcg10Beir, int Queries, double SearchSeconds);

/// <summary>Drives any <see cref="ITextEngine"/> over a BEIR dataset: index title and text, retrieve the top documents for every test query under each setting, write TREC run files and score them.</summary>
public static class BeirRunner
{
    /// <summary>The two BM25 parameter sets reported: the Anserini/Pyserini-style k1=0.9, b=0.4 and Lucene's defaults k1=1.2, b=0.75.</summary>
    public static readonly (float K1, float B)[] Parameters = [(0.9f, 0.4f), (1.2f, 0.75f)];

    /// <summary>All reported settings: three field layouts times the two parameter sets.</summary>
    public static IEnumerable<Bm25Setting> Settings() =>
        from layout in Layouts()
        from p in Parameters
        select new Bm25Setting(layout, p.K1, p.B);

    // ONEINDEX_LAYOUTS (comma separated layout names) narrows a run to some layouts; large corpora use it to fit a time budget.
    private static IEnumerable<FieldLayout> Layouts()
    {
        FieldLayout[] all = [FieldLayout.Flat, FieldLayout.MultiFieldMax, FieldLayout.MultiFieldSum, FieldLayout.Bm25F];
        string? only = Environment.GetEnvironmentVariable("ONEINDEX_LAYOUTS");
        return only is null ? all : all.Where(l => only.Split(',').Contains(l.ToString(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Indexes the dataset, runs every setting and writes <c>runs/*.run</c> (raw) and <c>runs/*.beir.run</c> (identical ids dropped) plus <c>summary.json</c> under <paramref name="outDir"/>.</summary>
    /// <param name="engine">The engine to drive.</param>
    /// <param name="data">The dataset.</param>
    /// <param name="indexDir">Where the engine builds its index.</param>
    /// <param name="outDir">Where run files and the summary go.</param>
    /// <param name="threads">Threads for indexing and searching.</param>
    /// <param name="depth">Hits retrieved per query.</param>
    public static List<RunOutcome> Run(ITextEngine engine, BeirDataset data, string indexDir, string outDir, int threads, int depth = 1000)
    {
        var runsDir = Path.Combine(outDir, "runs");
        Directory.CreateDirectory(runsDir);
        // ONEINDEX_PHASE=index builds and commits the index and stops; =search reopens it and runs the queries. A corpus too big for one
        // time-limited job is run in two. Unset runs both in one process.
        var phase = Environment.GetEnvironmentVariable("ONEINDEX_PHASE") ?? "both";
        var marker = Path.Combine(indexDir + ".indexed");
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
        long docCount;
        double indexSeconds;
        if (phase == "search")
        {
            var parts = File.ReadAllText(marker).Split(' ');
            docCount = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var open = Stopwatch.StartNew();
            engine.Create(new EngineSettings(indexDir, AnalysisKind.English, IndexProfile.Beir));
            engine.OpenSearcher();
            indexSeconds = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            Console.Error.WriteLine($"{data.Name}: reopened {docCount} docs in {open.Elapsed.TotalSeconds:F1}s");
        }
        else
        {
            if (Directory.Exists(indexDir))
            {
                Directory.Delete(indexDir, true);
            }

            var sw = Stopwatch.StartNew();
            engine.Create(new EngineSettings(indexDir, AnalysisKind.English, IndexProfile.Beir));
            var added = 0L;
            Parallel.ForEach(data.ReadCorpus(), options, doc =>
            {
                engine.Add(doc);
                Interlocked.Increment(ref added);
            });
            docCount = added;
            var addSeconds = sw.Elapsed.TotalSeconds;
            engine.Commit();
            indexSeconds = sw.Elapsed.TotalSeconds;
            Console.Error.WriteLine($"{data.Name}: indexed {docCount} docs in {indexSeconds:F1}s ({addSeconds:F1}s adding), {engine.IndexSizeBytes() / 1048576} MiB");
            if (phase == "index")
            {
                File.WriteAllText(marker, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{docCount} {indexSeconds}"));
                return [];
            }

            engine.OpenSearcher();
        }

        var outcomes = new List<RunOutcome>();
        foreach (var setting in Settings())
        {
            engine.SetBm25(setting.K1, setting.B);
            var results = new IReadOnlyList<Hit>[data.Queries.Count];
            var searchWatch = Stopwatch.StartNew();
            try
            {
                Parallel.For(0, data.Queries.Count, options, i =>
                    results[i] = engine.Search(new MatchQuery(data.Queries[i].Text, setting.Layout), depth).Hits);
            }
            catch (AggregateException e) when (e.InnerExceptions.All(x => x is NotSupportedException))
            {
                Console.Error.WriteLine($"{data.Name} {engine.Name}: skipping {setting.Name}: {e.InnerExceptions[0].Message}");
                continue;
            }

            var searchSeconds = searchWatch.Elapsed.TotalSeconds;

            var name = $"{engine.Name.ToLowerInvariant()}.{setting.Name}";
            var raw = Path.Combine(runsDir, name + ".run");
            var beir = Path.Combine(runsDir, name + ".beir.run");
            using (var w = new StreamWriter(raw, false, new System.Text.UTF8Encoding(false)))
            using (var wb = new StreamWriter(beir, false, new System.Text.UTF8Encoding(false)))
            {
                for (var i = 0; i < results.Length; i++)
                {
                    var qid = data.Queries[i].Id;
                    TrecEval.WriteRun(w, qid, results[i], name);
                    TrecEval.WriteRun(wb, qid, results[i].Where(h => h.Id != qid).ToList(), name);
                }
            }

            var (n, q) = TrecEval.NdcgAt(10, data.Qrels, TrecEval.ReadRun(raw));
            var (nb, _) = TrecEval.NdcgAt(10, data.Qrels, TrecEval.ReadRun(beir));
            outcomes.Add(new RunOutcome(name, n, nb, q, searchSeconds));
            Console.Error.WriteLine($"{data.Name} {name}: nDCG@10 {n:F4} (BEIR convention {nb:F4}) over {q} queries, {searchSeconds:F1}s");
        }

        var summary = new
        {
            dataset = data.Name,
            engine = engine.Name,
            engineVersion = engine.Version,
            analysis = "English (lowercase, possessive removal, English stop words, Porter-style stemming)",
            documents = docCount,
            queries = data.Queries.Count,
            depth,
            indexSeconds,
            indexBytes = engine.IndexSizeBytes(),
            runs = outcomes,
        };
        File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summary, JsonOptions.Indented));
        return outcomes;
    }
}
