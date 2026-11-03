using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using OneIndex.Analysis;
using OneIndex.Harness;
using OneIndex.Index;
using OneIndex.Models;
using OneIndex.Models.Tokenization;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Models.Tool;

/// <summary>Measurements of the cross-encoder: pair token ids and scores against the reference runtimes, and BEIR reranking.</summary>
internal static class RerankCommands
{
    public static int Run(string command, Dictionary<string, string> options)
    {
        string Req(string name) => options.TryGetValue(name, out string? v) ? v : throw new ArgumentException($"missing --{name}");
        return command switch
        {
            "pair-ids" => PairIds(Req("vocab"), Req("pairs")),
            "rerank-parity" => Parity(options, Req),
            _ => BeirRerank(options, Req),
        };
    }

    private static int PairIds(string vocab, string pairsFile)
    {
        using FileStream vs = File.OpenRead(vocab);
        var tokenizer = new WordPieceTokenizer(WordPieceVocabulary.Load(vs), null, UnicodeDecomposer.Instance);
        int total = 0, matched = 0, shown = 0, truncated = 0;
        var ids = new int[512];
        var types = new int[512];
        foreach (string line in File.ReadLines(pairsFile))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement r = doc.RootElement;
            int[] expectedIds = [.. r.GetProperty("i").EnumerateArray().Select(static e => e.GetInt32())];
            int[] expectedTypes = [.. r.GetProperty("t").EnumerateArray().Select(static e => e.GetInt32())];
            int n = tokenizer.EncodePair(r.GetProperty("q").GetString()!, r.GetProperty("p").GetString()!, ids, types);
            total++;
            if (expectedIds.Length == 512)
            {
                truncated++;
            }

            if (ids.AsSpan(0, n).SequenceEqual(expectedIds) && types.AsSpan(0, n).SequenceEqual(expectedTypes))
            {
                matched++;
            }
            else if (shown++ < 10)
            {
                Console.WriteLine($"difference at pair {total}: ours {n} ids ({types.AsSpan(0, n).Count(0) - 2} + {types.AsSpan(0, n).Count(1) - 1}), reference {expectedIds.Length} ({expectedTypes.Count(x => x == 0) - 2} + {expectedTypes.Count(x => x == 1) - 1})");
            }
        }

        Console.WriteLine($"pairs {total} matched {matched} differences {total - matched} (pairs at the 512 limit: {truncated})");
        return matched == total ? 0 : 1;
    }

    private static int Parity(Dictionary<string, string> options, Func<string, string> req)
    {
        var pairs = File.ReadLines(req("pairs")).Select(static l => JsonDocument.Parse(l).RootElement).Select(static e => (Q: e.GetProperty("q").GetString()!, P: e.GetProperty("p").GetString()!)).ToList();
        ReadOnlySpan<float> reference = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(req("reference")));
        using CrossEncoderReranker model = CrossEncoderReranker.Open(req("model"), req("vocab"), new CrossEncoderOptions
        {
            ModelId = "tool",
            MaxThreads = int.Parse(options.GetValueOrDefault("threads", "0"), CultureInfo.InvariantCulture),
            ExactArithmetic = options.GetValueOrDefault("exact", "off") == "on",
        });
        var ours = new float[pairs.Count];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < pairs.Count; i++)
        {
            ours[i] = RerankerExtensions.Score(model, pairs[i].Q, [pairs[i].P])[0];
        }

        sw.Stop();
        double maxAbs = 0, sumAbs = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            double d = Math.Abs(ours[i] - reference[i]);
            maxAbs = Math.Max(maxAbs, d);
            sumAbs += d;
        }

        // Batched scoring (all pairs in one call, so lengths mix and padding differs) against the one-pair-at-a-time scores.
        var batched = new float[pairs.Count];
        // The pairs have different queries, so batch per pair groups of identical query text; the rest run singly.
        double maxBatchDelta = 0;
        foreach (IGrouping<string, int> g in Enumerable.Range(0, pairs.Count).GroupBy(i => pairs[i].Q))
        {
            int[] idx = [.. g];
            float[] s = RerankerExtensions.Score(model, g.Key, [.. idx.Select(i => pairs[i].P)]);
            for (int k = 0; k < idx.Length; k++)
            {
                batched[idx[k]] = s[k];
                maxBatchDelta = Math.Max(maxBatchDelta, Math.Abs(s[k] - ours[idx[k]]));
            }
        }

        Console.WriteLine($"pairs {pairs.Count} kernel {model.KernelPath} maxAbsDiff {maxAbs:F5} meanAbsDiff {sumAbs / ours.Length:F5} spearman {Spearman(ours, reference):F5} reference range {reference.ToArray().Min():F2}..{reference.ToArray().Max():F2} maxBatchVsSingleDelta {maxBatchDelta:F5} seconds {sw.Elapsed.TotalSeconds:F1}");
        return 0;
    }

    private static double Spearman(float[] a, ReadOnlySpan<float> b)
    {
        double[] ra = Ranks(a);
        double[] rb = Ranks(b.ToArray());
        double ma = ra.Average(), mb = rb.Average(), num = 0, da = 0, db = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            num += (ra[i] - ma) * (rb[i] - mb);
            da += (ra[i] - ma) * (ra[i] - ma);
            db += (rb[i] - mb) * (rb[i] - mb);
        }

        return num / Math.Sqrt(da * db);
    }

    private static double[] Ranks(float[] v)
    {
        int[] order = [.. Enumerable.Range(0, v.Length).OrderBy(i => v[i])];
        var ranks = new double[v.Length];
        for (int i = 0; i < order.Length;)
        {
            int j = i;
            while (j + 1 < order.Length && v[order[j + 1]] == v[order[i]])
            {
                j++;
            }

            for (int k = i; k <= j; k++)
            {
                ranks[order[k]] = (i + j) / 2.0;
            }

            i = j + 1;
        }

        return ranks;
    }

    // BM25 (and, with --embedder and --vectors, default hybrid) top-100 reranked by the cross-encoder. Scores are cached per query in
    // <work>/<set>/rerank-scores-<arm>.jsonl, so a run that hits the time budget (exit 3) continues where it stopped.
    private static int BeirRerank(Dictionary<string, string> options, Func<string, string> req)
    {
        var data = BeirDataset.Open(Path.Combine(req("data"), req("set")));
        string work = Path.Combine(req("work"), req("set"));
        Directory.CreateDirectory(work);
        int window = int.Parse(options.GetValueOrDefault("window", "100"), CultureInfo.InvariantCulture);
        double budget = double.Parse(options.GetValueOrDefault("budget-seconds", "1500"), CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        int limitQueries = int.Parse(options.GetValueOrDefault("queries", "0"), CultureInfo.InvariantCulture);
        string? vectorsDir = options.GetValueOrDefault("vectors");
        string? embedderPath = options.GetValueOrDefault("embedder");
        bool hybrid = vectorsDir is not null && embedderPath is not null;
        int d = 384;

        var passages = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (Doc doc in data.ReadCorpus())
        {
            ids.Add(doc.Id);
            passages[doc.Id] = doc.Title.Length == 0 ? doc.Text : doc.Title + " " + doc.Text;
        }

        string indexDir = Path.Combine(work, hybrid ? "rerank-index-hybrid" : "rerank-index");
        Schema schema = hybrid
            ? new SchemaBuilder().Id("id").Add(Text("title", 3f)).Add(Text("text")).Add(new FieldDefinition("vec", FieldType.Vector) { Dimensions = d, Metric = VectorMetric.Cosine, Stored = false }).Build()
            : new SchemaBuilder().Id("id").Add(Text("title", 3f)).Add(Text("text")).Build();
        var storage = new FileSystemStorage(indexDir);
        if (!File.Exists(Path.Combine(indexDir, ".complete")))
        {
            if (Directory.Exists(indexDir))
            {
                Directory.Delete(indexDir, recursive: true);
            }

            Directory.CreateDirectory(indexDir);
            using IndexWriter writer = IndexWriter.Open(storage, schema, new IndexWriterOptions { MaxBufferedDocuments = 20_000 });
            int chunk = -1, inChunk = 0;
            float[] vectors = [];
            foreach (Doc doc in data.ReadCorpus())
            {
                var document = new Document().Add("id", doc.Id).Add("title", doc.Title).Add("text", doc.Text);
                if (hybrid)
                {
                    if (inChunk % 500 == 0)
                    {
                        chunk++;
                        vectors = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(vectorsDir!, req("set"), $"corpus-{chunk:D5}.f32"))).ToArray();
                        inChunk = 0;
                    }

                    document.Add("vec", vectors.AsSpan(inChunk++ * d, d).ToArray());
                }

                writer.AddUnique(document);
            }

            writer.Commit();
            writer.WaitForMerges();
            File.WriteAllText(Path.Combine(indexDir, ".complete"), "ok");
        }

        using var reopened = IndexWriter.Open(storage, schema, new IndexWriterOptions());
        using IndexSnapshot snapshot = reopened.OpenSnapshot();
        SearchContext context = SnapshotSearch.CreateContext(snapshot);
        var idOf = new string[context.MaxDoc];
        foreach (LeafContext leaf in context.Leaves)
        {
            IStoredFieldsReader stored = leaf.Reader.StoredFields ?? throw new InvalidOperationException("no stored source");
            for (int i = 0; i < leaf.Reader.MaxDoc; i++)
            {
                idOf[leaf.DocBase + i] = stored.GetDocument(i).Get("id").AsText();
            }
        }

        var searcher = new IndexSearcher(context, new QueryEngine());
        var queries = limitQueries > 0 ? data.Queries.Take(limitQueries).ToList() : data.Queries;
        float[]? queryVectors = null;
        if (hybrid)
        {
            using BertEmbedder embedder = BertEmbedder.Open(embedderPath!, Path.Combine(req("vocab")), new BertEmbedderOptions { ModelId = "bge", QueryPrefix = BgeSmallEmbedder.QueryInstruction });
            queryVectors = embedder.EmbedBatch([.. queries.Select(static q => q.Text)], EmbeddingRole.Query);
        }

        using CrossEncoderReranker reranker = CrossEncoderReranker.Open(req("model"), req("vocab"), new CrossEncoderOptions
        {
            ModelId = "tool",
            MaxThreads = int.Parse(options.GetValueOrDefault("threads", "0"), CultureInfo.InvariantCulture),
        });

        string[] arms = hybrid ? ["bm25", "hybrid"] : ["bm25"];
        int exit = 0;
        foreach (string arm in arms)
        {
            string cache = Path.Combine(work, $"rerank-scores-{arm}.jsonl");
            var done = new Dictionary<string, (string[] Docs, float[] Scores)>(StringComparer.Ordinal);
            if (File.Exists(cache))
            {
                foreach (string line in File.ReadLines(cache))
                {
                    using JsonDocument j = JsonDocument.Parse(line);
                    done[j.RootElement.GetProperty("q").GetString()!] = ([.. j.RootElement.GetProperty("d").EnumerateArray().Select(static e => e.GetString()!)], [.. j.RootElement.GetProperty("s").EnumerateArray().Select(static e => e.GetSingle())]);
                }
            }

            var first = new Dictionary<string, List<(string Doc, double Score)>>(StringComparer.Ordinal);
            var reranked = new Dictionary<string, List<(string Doc, double Score)>>(StringComparer.Ordinal);
            int scoredPairs = 0;
            double scoreSeconds = 0;
            using (var append = new StreamWriter(cache, append: true))
            {
                for (int q = 0; q < queries.Count; q++)
                {
                    (string qid, string text) = queries[q];
                    var terms = new List<string>();
                    TokenSource tokens = AnalyzerRegistry.Create("en").GetTokens(text.AsMemory());
                    while (tokens.MoveNext())
                    {
                        terms.Add(tokens.Token.Term.ToString());
                    }

                    Query bm25 = new BooleanQuery([.. terms.Select(t => new BooleanClause(new MultiFieldTermQuery(["title", "text"], t), Occur.Should))]);
                    Query query = bm25;
                    if (arm == "hybrid")
                    {
                        var knn = new KnnQuery("vec", queryVectors.AsSpan(q * d, d).ToArray(), window) { Exact = true };
                        query = new HybridQuery(bm25, knn);
                    }

                    TopDocs top = searcher.Search(query, window);
                    string[] docs = [.. top.Hits.Select(h => idOf[h.DocId])];
                    first[qid] = [.. top.Hits.Select(h => (idOf[h.DocId], (double)h.Score))];
                    if (!done.TryGetValue(qid, out var cached) || !cached.Docs.AsSpan().SequenceEqual(docs))
                    {
                        if (clock.Elapsed.TotalSeconds > budget)
                        {
                            Console.WriteLine($"budget used at query {q} of {queries.Count} ({arm}); run again to continue");
                            exit = 3;
                            goto next;
                        }

                        var sw = Stopwatch.StartNew();
                        float[] scores = RerankerExtensions.Score(reranker, text, [.. docs.Select(x => passages[x])]);
                        scoreSeconds += sw.Elapsed.TotalSeconds;
                        scoredPairs += docs.Length;
                        cached = (docs, scores);
                        append.Write(JsonSerializer.Serialize(new { q = qid, d = docs, s = scores }));
                        append.Write('\n');
                        append.Flush();
                    }

                    // Best score first, ties in first-stage order: the rerank stage's rule.
                    int[] order = [.. Enumerable.Range(0, docs.Length).OrderByDescending(i => cached.Scores[i]).ThenBy(i => i)];
                    reranked[qid] = [.. order.Select(i => (docs[i], (double)cached.Scores[i]))];
                }
            }

            next:
            if (exit == 3)
            {
                continue;
            }

            double Eval(Dictionary<string, List<(string Doc, double Score)>> run, bool dropSelf = true)
            {
                if (!dropSelf)
                {
                    // The BEIR paper's own convention: a document with the query's id is an ordinary hit (it counts as not relevant).
                    return TrecEval.NdcgAt(10, data.Qrels, run.ToDictionary(static kv => kv.Key, static kv => kv.Value.Take(10).ToList(), StringComparer.Ordinal)).Mean;
                }

                var cut = new Dictionary<string, List<(string Doc, double Score)>>(StringComparer.Ordinal);
                foreach ((string qid, var hits) in run)
                {
                    cut[qid] = [.. hits.Where(h => h.Doc != qid).Take(10)];
                }

                return TrecEval.NdcgAt(10, data.Qrels, cut).Mean;
            }

            foreach ((string name, var run) in new[] { (arm, first), (arm + "+ce", reranked) })
            {
                using var w = new StreamWriter(Path.Combine(work, $"{data.Name}.{name}.run"));
                foreach ((string qid, var hits) in run)
                {
                    int rank = 0;
                    foreach (var h in hits.Where(h => h.Doc != qid).Take(10))
                    {
                        w.Write($"{qid} Q0 {h.Doc} {++rank} {h.Score.ToString("R", CultureInfo.InvariantCulture)} {name}\n");
                    }
                }
            }

            Console.WriteLine($"set {data.Name} arm {arm} queries {queries.Count} window {window} nDCG@10 first stage {Eval(first):F4} reranked {Eval(reranked):F4}; without dropping a hit that has the query's own id: first stage {Eval(first, false):F4} reranked {Eval(reranked, false):F4} (scored this run: {scoredPairs} pairs in {scoreSeconds:F1} s = {scoredPairs / Math.Max(scoreSeconds, 1e-9):F1} pairs/s) kernel {reranker.KernelPath}");
        }

        return exit;
    }

    private static FieldDefinition Text(string name, float boost = 1f) => new(name, FieldType.Text) { Analyzer = "en", IndexOptions = IndexOptions.Freqs, Stored = false, Boost = boost };
}
