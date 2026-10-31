using System.Globalization;
using OneIndex.Harness;

// oneindex-harness: the relevance runner and the benchmark corpus driver.
//
//   beir     --engine lucene --data <beir dir> --sets scifact,arguana --out <dir> --work <dir> [--threads N]
//   ndcg     --qrels <test.tsv> <run files...>
//   driver   --engine lucene --corpus <abstracts.jsonl> --docs N --work <dir> --out <result.json> [--seed 1] [--queries 200] [--reps 3] [--threads 1] [--page-cache text]
//   queries  --corpus <abstracts.jsonl> --docs N --out <queries.json> [--seed 1] [--queries 200]
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: oneindex-harness beir|ndcg|driver|queries ... (see Program.cs)");
    return 2;
}

var opts = new Dictionary<string, string>(StringComparer.Ordinal);
var positional = new List<string>();
for (var i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        opts[args[i][2..]] = args[++i];
    }
    else
    {
        positional.Add(args[i]);
    }
}

string Req(string name) => opts.TryGetValue(name, out var v) ? v : throw new ArgumentException($"missing --{name}");
int Int(string name, int fallback) => opts.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

switch (args[0])
{
    case "beir":
    {
        using var engine = Engines.Create(Req("engine"));
        if (!engine.IsAvailable(out var reason))
        {
            Console.Error.WriteLine($"{engine.Name} is not available: {reason}");
            return 3;
        }

        foreach (var set in Req("sets").Split(','))
        {
            var data = BeirDataset.Open(Path.Combine(Req("data"), set));
            var outDir = Path.Combine(Req("out"), set, Req("engine"));
            BeirRunner.Run(engine, data, Path.Combine(Req("work"), $"{set}-{Req("engine")}"), outDir, Int("threads", 4));
        }

        return 0;
    }

    case "ndcg":
    {
        var qrels = BeirDataset.ReadQrels(Req("qrels"));
        foreach (var run in positional)
        {
            var (mean, n) = TrecEval.NdcgAt(10, qrels, TrecEval.ReadRun(run));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(run)}\t{mean:F6}\t{n}"));
        }

        return 0;
    }

    case "wand":
    {
        // Block-max WAND against exhaustive scoring on a BEIR set: same top 10, and how much was scored.
        var set = Req("set");
        var data = BeirDataset.Open(Path.Combine(Req("data"), set));
        using var engine = new OneIndexEngine();
        engine.Create(new EngineSettings(Path.Combine(Req("work"), set + "-wand"), AnalysisKind.English, IndexProfile.Beir));
        Parallel.ForEach(data.ReadCorpus(), new ParallelOptions { MaxDegreeOfParallelism = Int("threads", 4) }, engine.Add);
        engine.Commit();
        engine.OpenSearcher();
        foreach (var n in new[] { 1, 2, 5 })
        {
            long queries = 0, matching = 0, scored = 0, different = 0;
            foreach (var q in data.Queries)
            {
                var terms = OneIndexEngine.Analyze(q.Text);
                if (terms.Count < n)
                {
                    continue;
                }

                var (m, s, same) = engine.MeasureWand(terms.Take(n).ToList(), 10);
                queries++;
                matching += m;
                scored += s;
                different += same ? 0 : 1;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{set} terms={n} queries={queries} identical_top10={queries - different}/{queries} scored/matching={scored}/{matching} fraction={(double)scored / Math.Max(1, matching):F4}"));
        }

        // Allocation per query: every query's analyzed terms (at most 5), run once to warm up and once measured, on this thread.
        var all = data.Queries.Select(q => OneIndexEngine.Analyze(q.Text).Take(5).ToList()).Where(t => t.Count > 0).ToList();
        foreach (var t in all)
        {
            engine.MeasureSearchAllocation(t, 10);
        }

        var bytes = all.Select(t => engine.MeasureSearchAllocation(t, 10)).OrderBy(b => b).ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{set} allocation per pruned top-10 search over {bytes.Count} queries: mean={bytes.Average():F0} B median={bytes[bytes.Count / 2]} B p95={bytes[(int)(bytes.Count * 0.95)]} B max={bytes[^1]} B"));
        return 0;
    }

    case "analyze":
    {
        // Both English analyzers over every query and the first N documents of a BEIR set: one line per text, tab separated
        // (kind, id, Lucene terms, OneIndex terms), for comparing the two analysis chains.
        var data = BeirDataset.Open(Path.Combine(Req("data"), Req("set")));
        using var w = new StreamWriter(Req("out"));
        void Line(string kind, string id, string text) => w.WriteLine($"{kind}\t{id}\t{string.Join(' ', LuceneEngine.Analyze(text))}\t{string.Join(' ', OneIndexEngine.Analyze(text))}");
        foreach (var q in data.Queries)
        {
            Line("q", q.Id, q.Text);
        }

        foreach (var d in data.ReadCorpus().Take(Int("docs", 3000)))
        {
            Line("d", d.Id, d.Title + " " + d.Text);
        }

        return 0;
    }

    case "explain-lucene":
    {
        var data = BeirDataset.Open(Path.Combine(Req("data"), Req("set")));
        using var lucene = new LuceneEngine();
        lucene.Create(new EngineSettings(Path.Combine(Req("work"), Req("set") + "-explain"), AnalysisKind.English, IndexProfile.Beir));
        Parallel.ForEach(data.ReadCorpus(), new ParallelOptions { MaxDegreeOfParallelism = Int("threads", 4) }, lucene.Add);
        lucene.Commit();
        lucene.OpenSearcher();
        lucene.SetBm25(0.9f, 0.4f);
        foreach (var i in Req("queries").Split(','))
        {
            var q = data.Queries[int.Parse(i, CultureInfo.InvariantCulture)];
            Console.WriteLine($"=== query {i} {q.Id}: {q.Text}");
            Console.WriteLine(lucene.ExplainTop(new MatchQuery(q.Text, FieldLayout.MultiFieldSum)));
        }

        return 0;
    }

    case "driver":
        return BenchDriver.Run(new DriverOptions(
            Req("engine"), Req("corpus"), Int("docs", 50_000), Req("work"), Req("out"), Int("seed", 1), Int("queries", 200),
            Int("reps", 3), Int("threads", 1), opts.GetValueOrDefault("page-cache", "warm: the index was written by this process")));

    case "queries":
    {
        var docs = WikiCorpus.Read(Req("corpus"), Int("docs", 50_000));
        QueryGenerator.WriteJson(QueryGenerator.Generate(docs, Int("queries", 200), Int("seed", 1)), Req("out"));
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}
