using System.Globalization;
using OneIndex.Harness;

// oneindex-harness: the relevance runner and the benchmark corpus driver.
//
//   beir     --engine lucene --data <beir dir> --sets scifact,arguana --out <dir> --work <dir> [--threads N]
//   ndcg     --qrels <test.tsv> <run files...>
//   driver   --engine lucene --corpus <abstracts.jsonl> --docs N --work <dir> --out <result.json> [--seed 1] [--queries 200] [--reps 3] [--threads 1] [--page-cache text] [--rows Terms1,Prefix]
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
            Int("reps", 3), Int("threads", 1), opts.GetValueOrDefault("page-cache", "warm: the index was written by this process"), opts.GetValueOrDefault("rows")));

    case "count-diff":
    {
        // Finds the single terms of the benchmark's query sets on which the engines count different matches, and the documents involved.
        var docs = WikiCorpus.Read(Req("corpus"), Int("docs", 50_000));
        var sets = QueryGenerator.Generate(docs, Int("queries", 200), Int("seed", 1));
        var terms = sets.Values.SelectMany(v => v).OfType<StatsQuery>().SelectMany(q => q.Terms).Distinct().ToArray();
        using var a = Engines.Create("oneindex");
        using var b = Engines.Create("lucene");
        foreach (var (e, n) in new[] { (a, "o"), (b, "l") })
        {
            e.Create(new EngineSettings(Path.Combine(Req("work"), n), AnalysisKind.Standard, IndexProfile.Bench));
            foreach (var d in docs) { e.Add(d); }
            e.Commit();
            e.OpenSearcher();
        }

        int bad = 0;
        foreach (var t in terms)
        {
            var ra = a.Search(new TermsQuery([t]), docs.Count);
            var rb = b.Search(new TermsQuery([t]), docs.Count);
            var ia = ra.Hits.Select(h => h.Id).ToHashSet();
            var ib = rb.Hits.Select(h => h.Id).ToHashSet();
            if (ia.SetEquals(ib)) { continue; }
            bad++;
            Console.WriteLine($"term '{t}': oneindex {ia.Count} lucene {ib.Count}");
            foreach (var id in ia.Except(ib).Concat(ib.Except(ia)).Take(5))
            {
                var doc = docs.First(x => x.Id == id);
                int at = doc.Text.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"  {(ia.Contains(id) ? "only oneindex" : "only lucene")} {id} len={doc.Text.Length}: ...{doc.Text.Substring(Math.Max(0, at - 30), Math.Min(80, doc.Text.Length - Math.Max(0, at - 30)))}...");
            }
        }

        foreach (var id in new[] { "4749992", "4959550" })
        {
            var same = docs.Where(x => x.Id == id).ToList();
            Console.WriteLine($"id {id}: {same.Count} corpus docs; first at index {docs.FindIndex(x => x.Id == id)}");
            foreach (var d in same.Take(1))
            {
                var words = System.Text.RegularExpressions.Regex.Split(d.Text.ToLowerInvariant(), "[^a-z0-9]+").Where(x => x.Length > 0).Distinct().Take(400).ToList();
                int missing = 0;
                foreach (var w in words)
                {
                    if (!a.Search(new TermsQuery([w]), docs.Count).Hits.Any(h => h.Id == id) && b.Search(new TermsQuery([w]), docs.Count).Hits.Any(h => h.Id == id)) { missing++; }
                }

                Console.WriteLine($"  terms lucene finds but oneindex does not: {missing} of {words.Count}");
            }
        }

        Console.WriteLine($"{terms.Length} terms, {bad} differ");
        return 0;
    }

    case "commit-profile":
    {
        // Runs the driver's commit sequence with marker probes (a stat of a missing file) between steps, so a system-call trace
        // can be cut into the bulk commit and each small commit and the calls counted per commit.
        var docs = WikiCorpus.Read(Req("corpus"), Int("docs", 30_000));
        using var e = Engines.Create(Req("engine"));
        e.Create(new EngineSettings(Path.Combine(Req("work"), "profile"), AnalysisKind.Standard, IndexProfile.Bench));
        int bulk = docs.Count - 1000;
        for (int i = 0; i < bulk; i++) { e.Add(docs[i]); }
        e.Commit();
        for (int c = 0; c < 5; c++)
        {
            for (int i = 0; i < 200; i++) { e.Add(docs[bulk + (c * 200) + i]); }
            _ = File.Exists("/MARKER_BEFORE_COMMIT_" + c);
            e.Commit();
            _ = File.Exists("/MARKER_AFTER_COMMIT_" + c);
        }

        return 0;
    }

    case "commit-files":
    {
        // Times the driver's commit sequence and lists the files each small commit adds, to compare how many files each engine writes.
        var docs = WikiCorpus.Read(Req("corpus"), Int("docs", 30_000));
        using var e = Engines.Create(Req("engine"));
        string dir = Path.Combine(Req("work"), "files");
        e.Create(new EngineSettings(dir, AnalysisKind.Standard, IndexProfile.Bench));
        int bulk = docs.Count - 1000;
        for (int i = 0; i < bulk; i++) { e.Add(docs[i]); }
        e.Commit();
        var seen = new HashSet<string>(Directory.GetFileSystemEntries(dir).Select(Path.GetFileName)!);
        for (int c = 0; c < 5; c++)
        {
            for (int i = 0; i < 200; i++) { e.Add(docs[bulk + (c * 200) + i]); }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            e.Commit();
            double ms = sw.Elapsed.TotalMilliseconds;
            var now = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Cast<string>().ToList();
            var added = now.Where(n => !seen.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Console.WriteLine($"commit {c}: {ms:F1} ms, +{added.Count} files, {now.Count - seen.Count + 0} net, total {now.Count}: {string.Join(" ", added.Select(n => n + ":" + new FileInfo(Path.Combine(dir, n)).Length))}");
            seen = [.. now];
        }

        return 0;
    }

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
