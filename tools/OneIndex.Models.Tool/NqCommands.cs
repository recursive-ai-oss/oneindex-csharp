using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OneIndex.Analysis;
using OneIndex.Harness;
using OneIndex.Index;
using OneIndex.Models;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Models.Tool;

/// <summary>
/// BEIR NQ with reference encodings. The document vectors (dense, learned sparse) and the cross-encoder pair scores come from the
/// published model runtimes on a GPU (tools/models/gpu_reference.py); the engine does everything else: indexing, BM25F, exact vector
/// search, sparse scoring, fusion and nDCG. The stand-in commands run the shipped int8 files through OneModel on a random sub-corpus
/// and compare them with the reference encodings of the same passages.
///
///   nq-index    --data D --set S [--dense DIR] [--sparse DIR] --index DIR [--limit N] [--budget-seconds N]   (resumable; exit 3 = run again)
///   nq-run      --data D --set S --index DIR [--dense DIR] [--sparse-model DIR] --work DIR [--arms a,b] [--threads N] [--budget-seconds N]
///   nq-eval     --data D --set S --work DIR [--run-dir DIR] [--candidates FILE] [--rerank-scores FILE]
///   standin-dense  --data DIR --gpu DIR --model F --vocab F --work DIR [--budget-seconds N]
///   standin-sparse --data DIR --gpu DIR --dir DIR --work DIR [--budget-seconds N]
///   standin-rerank --data D --set S --work DIR --candidates FILE --gpu-scores FILE --model F --vocab F [--queries N] [--budget-seconds N]
/// </summary>
internal static class NqCommands
{
    private const int Shard = 20000;
    private const int Dim = 384;
    private const int Window = 100;
    private const string BgeQueryInstruction = "Represent this sentence for searching relevant passages: ";

    public static int Run(string command, Dictionary<string, string> o)
    {
        return command switch
        {
            "nq-index" => Index(o),
            "nq-run" => RunArms(o),
            "nq-eval" => Eval(o),
            "standin-dense" => StandInDense(o),
            "standin-sparse" => StandInSparse(o),
            "standin-rerank" => StandInRerank(o),
            _ => 2,
        };
    }

    private static string Req(Dictionary<string, string> o, string name) => o.TryGetValue(name, out string? v) ? v : throw new ArgumentException($"missing --{name}");

    private static string Opt(Dictionary<string, string> o, string name, string fallback) => o.TryGetValue(name, out string? v) ? v : fallback;

    private static FieldDefinition Text(string name, float boost = 1f) => new(name, FieldType.Text) { Analyzer = "en", IndexOptions = IndexOptions.Freqs, Stored = false, Boost = boost };

    // Exact vector search is what is measured, so the field gets no graph: the flat threshold is above any segment size.
    private static Schema BuildSchema(bool vectors, bool sparse)
    {
        SchemaBuilder b = new SchemaBuilder().Id("id").Add(Text("title", 3f)).Add(Text("text"));
        if (vectors)
        {
            b.Add(new FieldDefinition("vec", FieldType.Vector) { Dimensions = Dim, Metric = VectorMetric.Cosine, Stored = false, FlatThreshold = 100_000_000 });
        }

        return sparse ? b.Sparse("s").Build() : b.Build();
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = System.Numerics.Vector<float>.Zero;
        int i = 0;
        for (; i <= a.Length - System.Numerics.Vector<float>.Count; i += System.Numerics.Vector<float>.Count)
        {
            sum += new System.Numerics.Vector<float>(a.Slice(i)) * new System.Numerics.Vector<float>(b.Slice(i));
        }

        float total = System.Numerics.Vector.Sum(sum);
        for (; i < a.Length; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    private static float[] ReadFloats(string path) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();

    private static string[] ReadVocab(string dir) => File.ReadAllLines(Path.Combine(dir, "vocab.txt"), Encoding.UTF8);

    /// <summary>Reads one sparse shard of the GPU script: counts, token ids, weights.</summary>
    private static SparseVector[] ReadSparseShard(string path, string[] vocab)
    {
        byte[] raw = File.ReadAllBytes(path);
        ReadOnlySpan<int> ints = MemoryMarshal.Cast<byte, int>(raw);
        ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(raw);
        int docs = ints[0];
        long sum = 0;
        for (int i = 0; i < docs; i++)
        {
            sum += ints[1 + i];
        }

        if (1 + docs + (2 * sum) != ints.Length)
        {
            throw new InvalidDataException($"{path}: sizes do not add up");
        }

        var result = new SparseVector[docs];
        long idAt = 1 + docs, weightAt = 1 + docs + sum;
        for (int i = 0; i < docs; i++)
        {
            int n = ints[1 + i];
            var list = new List<KeyValuePair<string, float>>(n);
            for (int k = 0; k < n; k++)
            {
                list.Add(new(vocab[ints[(int)(idAt + k)]], floats[(int)(weightAt + k)]));
            }

            idAt += n;
            weightAt += n;
            result[i] = new SparseVector(list);
        }

        return result;
    }

    private static string PassageText(Doc d) => d.Title.Length == 0 ? d.Text : d.Title + " " + d.Text;

    private static NeuralSparseEncoder OpenSparse(string dir, int threads) => NeuralSparseEncoder.OpenDirectory(dir, new NeuralSparseEncoderOptions
    {
        ModelId = "tool",
        ExtraLog1p = 1,
        MaxThreads = threads,
        MaxBatchTokens = 1024,
    });

    // ---------------------------------------------------------------- index

    private static int Index(Dictionary<string, string> o)
    {
        var data = BeirDataset.Open(Path.Combine(Req(o, "data"), Req(o, "set")));
        string? denseDir = o.GetValueOrDefault("dense");
        string? sparseDir = o.GetValueOrDefault("sparse");
        string indexDir = Req(o, "index");
        int limit = int.Parse(Opt(o, "limit", "0"), CultureInfo.InvariantCulture);
        double budget = double.Parse(Opt(o, "budget-seconds", "1500"), CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        Schema schema = BuildSchema(denseDir is not null, sparseDir is not null);
        Directory.CreateDirectory(indexDir);
        if (File.Exists(Path.Combine(indexDir, ".complete")))
        {
            Console.WriteLine("index complete");
            return 0;
        }

        var storage = new FileSystemStorage(indexDir);
        int skip = 0;
        if (Directory.EnumerateFiles(indexDir, "commit-*").Any())
        {
            using IndexWriter probe = IndexWriter.Open(storage, schema, new IndexWriterOptions());
            using IndexSnapshot probeSnapshot = probe.OpenSnapshot();
            skip = SnapshotSearch.CreateContext(probeSnapshot).MaxDoc;
            Console.WriteLine($"resuming after {skip} committed passages");
        }

        string[] vocab = sparseDir is null ? [] : ReadVocab(sparseDir);
        const int CommitEvery = 200_000;
        int at = 0, shardNo = -1;
        float[] dense = [];
        SparseVector[] sparse = [];
        using (IndexWriter writer = IndexWriter.Open(storage, schema, new IndexWriterOptions { MaxBufferedDocuments = 20_000 }))
        {
            int sinceCommit = 0;
            foreach (Doc doc in data.ReadCorpus())
            {
                if (limit > 0 && at >= limit)
                {
                    break;
                }

                if (at < skip)
                {
                    at++;
                    continue;
                }

                if (at / Shard != shardNo)
                {
                    shardNo = at / Shard;
                    if (denseDir is not null)
                    {
                        dense = ReadFloats(Path.Combine(denseDir, $"dense-{shardNo:D5}.f32"));
                    }

                    if (sparseDir is not null)
                    {
                        sparse = ReadSparseShard(Path.Combine(sparseDir, $"sparse-{shardNo:D5}.bin"), vocab);
                    }
                }

                int within = at % Shard;
                var document = new Document().Add("id", doc.Id).Add("title", doc.Title).Add("text", doc.Text);
                if (denseDir is not null)
                {
                    document.Add("vec", dense.AsSpan(within * Dim, Dim).ToArray());
                }

                if (sparseDir is not null)
                {
                    document.Add("s", sparse[within]);
                }

                writer.AddUnique(document);
                at++;
                if (++sinceCommit == CommitEvery)
                {
                    writer.Commit();
                    sinceCommit = 0;
                    Console.WriteLine($"{at} passages committed after {clock.Elapsed.TotalSeconds:F0} s");
                    if (clock.Elapsed.TotalSeconds > budget)
                    {
                        Console.WriteLine("budget used; run again to continue");
                        return 3;
                    }
                }
            }

            writer.Commit();
            writer.WaitForMerges();
        }

        File.WriteAllText(Path.Combine(indexDir, ".complete"), at.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine($"{at} passages indexed in {clock.Elapsed.TotalSeconds:F0} s, index {new DirectoryInfo(indexDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)} bytes");
        return 0;
    }

    // ---------------------------------------------------------------- arms

    private static BooleanQuery TextQuery(string text)
    {
        var terms = new List<string>();
        TokenSource tokens = AnalyzerRegistry.Create("en").GetTokens(text.AsMemory());
        while (tokens.MoveNext())
        {
            terms.Add(tokens.Token.Term.ToString());
        }

        return new BooleanQuery([.. terms.Select(t => new BooleanClause(new MultiFieldTermQuery(["title", "text"], t), Occur.Should))]);
    }

    private static int RunArms(Dictionary<string, string> o)
    {
        var data = BeirDataset.Open(Path.Combine(Req(o, "data"), Req(o, "set")));
        string indexDir = Req(o, "index");
        string work = Req(o, "work");
        Directory.CreateDirectory(work);
        double budget = double.Parse(Opt(o, "budget-seconds", "1500"), CultureInfo.InvariantCulture);
        int threads = int.Parse(Opt(o, "threads", "6"), CultureInfo.InvariantCulture);
        string[] arms = Opt(o, "arms", "bm25,sparse,dense,rrf,rrf-text-sparse,rrf3").Split(',');
        var clock = Stopwatch.StartNew();
        bool hasSparse = o.ContainsKey("sparse-model");
        bool hasDense = o.ContainsKey("dense");
        Schema schema = BuildSchema(hasDense, hasSparse);
        var storage = new FileSystemStorage(indexDir);
        using IndexWriter writer = IndexWriter.Open(storage, schema, new IndexWriterOptions());
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        SearchContext context = SnapshotSearch.CreateContext(snapshot);
        string idsFile = Path.Combine(work, "ids.txt");
        string[] idOf;
        if (File.Exists(idsFile) && File.ReadLines(idsFile).Count() == context.MaxDoc)
        {
            idOf = File.ReadAllLines(idsFile);
        }
        else
        {
            idOf = new string[context.MaxDoc];
            foreach (LeafContext leaf in context.Leaves)
            {
                IStoredFieldsReader stored = leaf.Reader.StoredFields ?? throw new InvalidOperationException("no stored source");
                for (int i = 0; i < leaf.Reader.MaxDoc; i++)
                {
                    idOf[leaf.DocBase + i] = stored.GetDocument(i).Get("id").AsText();
                }
            }

            File.WriteAllLines(idsFile, idOf);
            Console.WriteLine($"ids of {idOf.Length} passages read in {clock.Elapsed.TotalSeconds:F0} s");
        }

        var searcher = new IndexSearcher(context, new QueryEngine());
        float[] qdense = hasDense ? ReadFloats(Path.Combine(Req(o, "dense"), "queries.f32")) : [];
        if (hasDense && qdense.Length != data.Queries.Count * Dim)
        {
            throw new InvalidOperationException($"queries.f32 has {qdense.Length / Dim} vectors for {data.Queries.Count} queries");
        }

        var qsparse = new SparseVector[data.Queries.Count];
        if (hasSparse)
        {
            using NeuralSparseEncoder enc = OpenSparse(Req(o, "sparse-model"), 0);
            enc.Encode(data.Queries.Select(q => q.Text).ToList(), EmbeddingRole.Query, qsparse);
        }

        int exit = 0;
        foreach (string arm in arms)
        {
            string cache = Path.Combine(work, $"arm-{arm}.jsonl");
            var done = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(cache))
            {
                foreach (string line in File.ReadLines(cache))
                {
                    using JsonDocument j = JsonDocument.Parse(line);
                    done.Add(j.RootElement.GetProperty("q").GetString()!);
                }
            }

            int keep = arm is "bm25" or "dense" or "sparse" ? Window : 20;
            int pending = 0;
            var sw = Stopwatch.StartNew();
            using var append = new StreamWriter(cache, append: true);
            object gate = new();
            bool stopped = false;
            Parallel.For(0, data.Queries.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, q =>
            {
                (string qid, string text) = data.Queries[q];
                if (done.Contains(qid) || Volatile.Read(ref stopped))
                {
                    return;
                }

                if (clock.Elapsed.TotalSeconds > budget)
                {
                    Volatile.Write(ref stopped, true);
                    return;
                }

                Query textQuery = TextQuery(text);
                KnnQuery Knn() => new("vec", qdense.AsSpan(q * Dim, Dim).ToArray(), Window) { Exact = true };
                Query query = arm switch
                {
                    "bm25" => textQuery,
                    "dense" => Knn(),
                    "sparse" => new SparseQuery("s", qsparse[q]),
                    "rrf" => new HybridQuery(textQuery, Knn()),
                    "rrf-text-sparse" => new HybridQuery(textQuery, null) { Sparse = new SparseQuery("s", qsparse[q]) },
                    "rrf3" => new HybridQuery(textQuery, Knn()) { Sparse = new SparseQuery("s", qsparse[q]) },
                    _ => throw new ArgumentException($"unknown arm {arm}"),
                };
                TopDocs top = searcher.Search(query, keep);
                string json = JsonSerializer.Serialize(new { q = qid, d = top.Hits.Select(h => idOf[h.DocId]).ToArray(), s = top.Hits.Select(h => h.Score).ToArray() });
                lock (gate)
                {
                    append.Write(json);
                    append.Write('\n');
                    append.Flush();
                    pending++;
                }
            });
            Console.WriteLine($"arm {arm}: {pending} queries this run in {sw.Elapsed.TotalSeconds:F0} s ({done.Count} before)");
            if (Volatile.Read(ref stopped))
            {
                Console.WriteLine("budget used; run again to continue");
                exit = 3;
                break;
            }
        }

        return exit;
    }

    // ---------------------------------------------------------------- eval

    private static Dictionary<string, (string[] Docs, float[] Scores)> ReadArm(string path)
    {
        var result = new Dictionary<string, (string[], float[])>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            using JsonDocument j = JsonDocument.Parse(line);
            JsonElement r = j.RootElement;
            result[r.GetProperty("q").GetString()!] = ([.. r.GetProperty("d").EnumerateArray().Select(static e => e.GetString()!)], [.. r.GetProperty("s").EnumerateArray().Select(static e => e.GetSingle())]);
        }

        return result;
    }

    private static Dictionary<string, List<(string Doc, double Score)>> ToRun(IEnumerable<(string Qid, IEnumerable<(string Doc, double Score)> Hits)> lists)
    {
        // BEIR convention: a hit with the query's own id is dropped; only the top 10 count.
        var run = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);
        foreach ((string qid, var hits) in lists)
        {
            run[qid] = [.. hits.Where(h => h.Doc != qid).Take(10)];
        }

        return run;
    }

    private static double Ndcg(BeirDataset data, Dictionary<string, List<(string Doc, double Score)>> run) => TrecEval.NdcgAt(10, data.Qrels, run).Mean;

    private static void WriteRun(string dir, string setName, string name, Dictionary<string, List<(string Doc, double Score)>> run)
    {
        Directory.CreateDirectory(dir);
        using var w = new StreamWriter(Path.Combine(dir, $"{setName}.{name}.run"));
        foreach ((string qid, var hits) in run)
        {
            for (int r = 0; r < hits.Count; r++)
            {
                w.Write($"{qid} Q0 {hits[r].Doc} {r + 1} {hits[r].Score.ToString("R", CultureInfo.InvariantCulture)} {name}\n");
            }
        }
    }

    // Reciprocal rank fusion and weighted linear fusion exactly as docs/hybrid.md states them, over two cached windows.
    private static List<(string Doc, double Score)> Fuse((string[] Docs, float[] Scores) text, (string[] Docs, float[] Scores) vec, bool linear, double ratio, int k)
    {
        var f = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!linear)
        {
            for (int i = 0; i < text.Docs.Length; i++) { f[text.Docs[i]] = f.GetValueOrDefault(text.Docs[i]) + (2 * (1 - ratio) / (k + i + 1)); }
            for (int i = 0; i < vec.Docs.Length; i++) { f[vec.Docs[i]] = f.GetValueOrDefault(vec.Docs[i]) + (2 * ratio / (k + i + 1)); }
        }
        else
        {
            double Norm(float s, float[] all) { float min = all.Min(), max = all.Max(); return max == min ? 1 : (s - min) / (max - min); }
            for (int i = 0; i < text.Docs.Length; i++) { f[text.Docs[i]] = f.GetValueOrDefault(text.Docs[i]) + ((1 - ratio) * Norm(text.Scores[i], text.Scores)); }
            for (int i = 0; i < vec.Docs.Length; i++) { f[vec.Docs[i]] = f.GetValueOrDefault(vec.Docs[i]) + (ratio * Norm(vec.Scores[i], vec.Scores)); }
        }

        return [.. f.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value))];
    }

    private static int Eval(Dictionary<string, string> o)
    {
        var data = BeirDataset.Open(Path.Combine(Req(o, "data"), Req(o, "set")));
        string work = Req(o, "work");
        string? runDir = o.GetValueOrDefault("run-dir");
        string set = data.Name;
        var arms = new Dictionary<string, Dictionary<string, (string[] Docs, float[] Scores)>>();
        foreach (string name in new[] { "bm25", "dense", "sparse", "rrf", "rrf-text-sparse", "rrf3" })
        {
            string file = Path.Combine(work, $"arm-{name}.jsonl");
            if (File.Exists(file))
            {
                arms[name] = ReadArm(file);
            }
        }

        foreach ((string name, var arm) in arms)
        {
            Dictionary<string, List<(string Doc, double Score)>> run = ToRun(data.Queries.Where(q => arm.ContainsKey(q.Id)).Select(q => (q.Id, arm[q.Id].Docs.Zip(arm[q.Id].Scores, (d, s) => (d, (double)s)))));
            Console.WriteLine($"set {set} arm {name} queries {run.Count} nDCG@10 {Ndcg(data, run):F4}");
            if (runDir is not null)
            {
                WriteRun(runDir, set, name, run);
            }
        }

        if (arms.TryGetValue("bm25", out var text) && arms.TryGetValue("dense", out var vec))
        {
            var configs = new (string Name, bool Linear, double Ratio, int K)[]
            {
                ("rrf-offline", false, 0.75, 10),
                ("rrf-k60-ratio0.5", false, 0.5, 60),
                ("rrf-k10-ratio0.5", false, 0.5, 10),
                ("linear-0.3", true, 0.3, 0),
                ("linear-0.5", true, 0.5, 0),
                ("linear-0.7", true, 0.7, 0),
            };
            Dictionary<string, List<(string Doc, double Score)>>? offlineRrf = null;
            foreach ((string name, bool linear, double ratio, int k) in configs)
            {
                var run = ToRun(data.Queries.Select(q => (q.Id, (IEnumerable<(string, double)>)Fuse(text[q.Id], vec[q.Id], linear, ratio, k))));
                Console.WriteLine($"set {set} config {name} (offline from the cached windows) queries {run.Count} nDCG@10 {Ndcg(data, run):F4}");
                if (name == "rrf-offline")
                {
                    offlineRrf = run;
                }

                if (runDir is not null)
                {
                    WriteRun(runDir, set, name, run);
                }
            }

            if (arms.TryGetValue("rrf", out var engine) && offlineRrf is not null)
            {
                int same = 0, sameSet = 0, compared = 0;
                foreach (var q in data.Queries.Where(q => engine.ContainsKey(q.Id)))
                {
                    var e = ToRun([(q.Id, engine[q.Id].Docs.Zip(engine[q.Id].Scores, (d, s) => (d, (double)s)))])[q.Id].Select(h => h.Doc).ToArray();
                    var f = offlineRrf[q.Id].Select(h => h.Doc).ToArray();
                    compared++;
                    same += e.SequenceEqual(f) ? 1 : 0;
                    sameSet += e.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(f.OrderBy(x => x, StringComparer.Ordinal)) ? 1 : 0;
                }

                Console.WriteLine($"engine default hybrid vs the offline formula: {compared} queries, identical top-10 order {same}, same top-10 set {sameSet}");
            }
        }

        if (o.TryGetValue("candidates", out string? candidates) && arms.TryGetValue("bm25", out var bm25))
        {
            using var w = new StreamWriter(candidates);
            foreach ((string qid, string qtext) in data.Queries)
            {
                w.Write(JsonSerializer.Serialize(new { q = qid, t = qtext, d = bm25[qid].Docs }));
                w.Write('\n');
            }

            Console.WriteLine($"wrote {data.Queries.Count} candidate lists to {candidates}");
        }

        if (o.TryGetValue("rerank-scores", out string? scoresFile) && arms.TryGetValue("bm25", out var first))
        {
            var scores = ReadArm(scoresFile);
            var rerun = ToRun(data.Queries.Where(q => scores.ContainsKey(q.Id)).Select(q =>
            {
                (string[] docs, float[] s) = scores[q.Id];
                int[] order = [.. Enumerable.Range(0, docs.Length).OrderByDescending(i => s[i]).ThenBy(i => i)];
                // Strictly decreasing scores keep the order through the metric's own tie rule.
                return (q.Id, order.Select((i, rank) => (docs[i], (double)(docs.Length - rank))));
            }));
            var before = ToRun(data.Queries.Where(q => scores.ContainsKey(q.Id)).Select(q => (q.Id, first[q.Id].Docs.Select((d, rank) => (d, (double)(first[q.Id].Docs.Length - rank))))));
            Console.WriteLine($"set {set} rerank queries {rerun.Count} window {first.Values.First().Docs.Length}: first stage nDCG@10 {Ndcg(data, before):F4} reranked {Ndcg(data, rerun):F4}");
            if (runDir is not null)
            {
                WriteRun(runDir, set, "bm25+ce", rerun);
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- stand-in proofs

    private static List<(int Doc, float Score)> Top10(ReadOnlySpan<float> query, float[] docs, int count)
    {
        var heap = new PriorityQueue<int, float>();
        var scores = new float[10];
        for (int i = 0; i < count; i++)
        {
            float s = Dot(query, docs.AsSpan(i * Dim, Dim));
            if (heap.Count < 10)
            {
                heap.Enqueue(i, s);
            }
            else if (heap.TryPeek(out _, out float min) && s > min)
            {
                heap.DequeueEnqueue(i, s);
            }
        }

        var result = new List<(int, float)>();
        while (heap.TryDequeue(out int d, out float sc))
        {
            result.Add((d, sc));
        }

        result.Reverse();
        return result;
    }

    private static Dictionary<string, List<(string Doc, double Score)>> DenseRun(BeirDataset data, List<string> ids, float[] queries, float[] docs)
    {
        var tops = new List<(int, float)>[data.Queries.Count];
        Parallel.For(0, data.Queries.Count, q => tops[q] = Top10(queries.AsSpan(q * Dim, Dim), docs, ids.Count));
        var run = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);
        for (int q = 0; q < tops.Length; q++)
        {
            run[data.Queries[q].Id] = [.. tops[q].Select(t => (ids[t.Item1], (double)t.Item2))];
        }

        return run;
    }

    private static double Overlap(Dictionary<string, List<(string Doc, double Score)>> a, Dictionary<string, List<(string Doc, double Score)>> b)
        => a.Keys.Average(q => a[q].Select(h => h.Doc).Intersect(b[q].Select(h => h.Doc)).Count() / (double)Math.Max(1, a[q].Count));

    private static string Percentiles(List<double> v)
    {
        v.Sort();
        return $"min {v[0]:F6} p1 {v[v.Count / 100]:F6} median {v[v.Count / 2]:F6} mean {v.Average():F6}";
    }

    private static int StandInDense(Dictionary<string, string> o)
    {
        var data = BeirDataset.Open(Req(o, "data"));
        double budget = double.Parse(Opt(o, "budget-seconds", "1500"), CultureInfo.InvariantCulture);
        string work = Req(o, "work");
        Directory.CreateDirectory(work);
        var clock = Stopwatch.StartNew();
        var docs = data.ReadCorpus().ToList();
        var ids = docs.Select(d => d.Id).ToList();
        using BertEmbedder embedder = BertEmbedder.Open(Req(o, "model"), Req(o, "vocab"), new BertEmbedderOptions
        {
            ModelId = "tool",
            QueryPrefix = BgeQueryInstruction,
            MaxThreads = int.Parse(Opt(o, "threads", "0"), CultureInfo.InvariantCulture),
        });
        const int Chunk = 500;
        double seconds = 0;
        for (int c = 0; c * Chunk < docs.Count; c++)
        {
            string file = Path.Combine(work, $"ours-{c:D5}.f32");
            if (File.Exists(file))
            {
                continue;
            }

            if (clock.Elapsed.TotalSeconds > budget)
            {
                Console.WriteLine($"budget used after {c} chunks; run again to continue");
                return 3;
            }

            var sw = Stopwatch.StartNew();
            float[] v = embedder.EmbedBatch([.. docs.Skip(c * Chunk).Take(Chunk).Select(PassageText)], EmbeddingRole.Document);
            seconds += sw.Elapsed.TotalSeconds;
            File.WriteAllBytes(file + ".part", MemoryMarshal.AsBytes(v.AsSpan()).ToArray());
            File.Move(file + ".part", file);
            Console.WriteLine($"chunk {c}: {Chunk / sw.Elapsed.TotalSeconds:F1} passages/s");
        }

        string qfile = Path.Combine(work, "ours-queries.f32");
        if (!File.Exists(qfile))
        {
            float[] qv = embedder.EmbedBatch([.. data.Queries.Select(q => q.Text)], EmbeddingRole.Query);
            File.WriteAllBytes(qfile, MemoryMarshal.AsBytes(qv.AsSpan()).ToArray());
        }

        var ours = new float[docs.Count * Dim];
        for (int c = 0; c * Chunk < docs.Count; c++)
        {
            ReadFloats(Path.Combine(work, $"ours-{c:D5}.f32")).CopyTo(ours.AsSpan(c * Chunk * Dim));
        }

        var gpu = new float[docs.Count * Dim];
        for (int s = 0; s * Shard < docs.Count; s++)
        {
            ReadFloats(Path.Combine(Req(o, "gpu"), $"dense-{s:D5}.f32")).CopyTo(gpu.AsSpan(s * Shard * Dim));
        }

        float[] oursQ = ReadFloats(qfile);
        float[] gpuQ = ReadFloats(Path.Combine(Req(o, "gpu"), "queries.f32"));
        List<double> Cosines(float[] a, float[] b, int n, out double maxAbs)
        {
            var c = new List<double>(n);
            maxAbs = 0;
            for (int i = 0; i < n; i++)
            {
                c.Add(Dot(a.AsSpan(i * Dim, Dim), b.AsSpan(i * Dim, Dim)));
                for (int k = 0; k < Dim; k++)
                {
                    maxAbs = Math.Max(maxAbs, Math.Abs(a[(i * Dim) + k] - b[(i * Dim) + k]));
                }
            }

            return c;
        }

        Console.WriteLine($"stand-in dense: {docs.Count} passages, {data.Queries.Count} queries");
        var dc = Cosines(ours, gpu, docs.Count, out double dmax);
        Console.WriteLine($"  passages cosine(int8 OneModel, GPU fp32): {Percentiles(dc)} maxAbsDiff {dmax:F5}");
        var qc = Cosines(oursQ, gpuQ, data.Queries.Count, out double qmax);
        Console.WriteLine($"  queries  cosine(int8 OneModel, GPU fp32): {Percentiles(qc)} maxAbsDiff {qmax:F5}");
        var runGpu = DenseRun(data, ids, gpuQ, gpu);
        var runOurs = DenseRun(data, ids, oursQ, ours);
        var runMixed = DenseRun(data, ids, oursQ, gpu);
        Console.WriteLine($"  nDCG@10 on the sample sub-corpus: GPU fp32 {Ndcg(data, runGpu):F4}, int8 OneModel {Ndcg(data, runOurs):F4}, int8 queries on GPU passages {Ndcg(data, runMixed):F4}");
        Console.WriteLine($"  top-10 overlap, int8 vs GPU rankings: {Overlap(runGpu, runOurs):F4} (this run embedded passages in {seconds:F0} s)");
        return 0;
    }

    private static int StandInSparse(Dictionary<string, string> o)
    {
        var data = BeirDataset.Open(Req(o, "data"));
        double budget = double.Parse(Opt(o, "budget-seconds", "1500"), CultureInfo.InvariantCulture);
        string work = Req(o, "work");
        Directory.CreateDirectory(work);
        var clock = Stopwatch.StartNew();
        var docs = data.ReadCorpus().ToList();
        using NeuralSparseEncoder encoder = OpenSparse(Req(o, "dir"), int.Parse(Opt(o, "threads", "0"), CultureInfo.InvariantCulture));
        const int Chunk = 200;
        var ours = new List<SparseVector>();
        double seconds = 0;
        for (int c = 0; c * Chunk < docs.Count; c++)
        {
            string file = Path.Combine(work, $"ours-{c:D5}.sparse");
            if (!File.Exists(file))
            {
                if (clock.Elapsed.TotalSeconds > budget)
                {
                    Console.WriteLine($"budget used after {c} chunks; run again to continue");
                    return 3;
                }

                var sw = Stopwatch.StartNew();
                var v = new SparseVector[Math.Min(Chunk, docs.Count - (c * Chunk))];
                encoder.Encode([.. docs.Skip(c * Chunk).Take(Chunk).Select(PassageText)], EmbeddingRole.Document, v);
                seconds += sw.Elapsed.TotalSeconds;
                using (var w = new BinaryWriter(File.Create(file + ".part")))
                {
                    foreach (SparseVector s in v)
                    {
                        w.Write(s.Count);
                        for (int k = 0; k < s.Count; k++) { w.Write(s.Tokens[k]); w.Write(s.Weights[k]); }
                    }
                }

                File.Move(file + ".part", file);
                Console.WriteLine($"chunk {c}: {v.Length / sw.Elapsed.TotalSeconds:F1} passages/s");
            }

            using var r = new BinaryReader(File.OpenRead(file));
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                int n = r.ReadInt32();
                var list = new List<KeyValuePair<string, float>>(n);
                for (int k = 0; k < n; k++) { list.Add(new(r.ReadString(), r.ReadSingle())); }
                ours.Add(new SparseVector(list));
            }
        }

        string[] vocab = ReadVocab(Req(o, "gpu"));
        var gpu = new List<SparseVector>();
        for (int s = 0; s * Shard < docs.Count; s++)
        {
            gpu.AddRange(ReadSparseShard(Path.Combine(Req(o, "gpu"), $"sparse-{s:D5}.bin"), vocab));
        }

        var cos = new List<double>();
        int sameSupport = 0, specials = 0;
        double maxAbs = 0, sumAbs = 0;
        long entries = 0;
        for (int i = 0; i < docs.Count; i++)
        {
            var a = new Dictionary<string, float>(StringComparer.Ordinal);
            var b = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int k = 0; k < ours[i].Count; k++) { a[ours[i].Tokens[k]] = ours[i].Weights[k]; }
            for (int k = 0; k < gpu[i].Count; k++) { b[gpu[i].Tokens[k]] = gpu[i].Weights[k]; }
            double dot = 0, na = 0, nb = 0;
            foreach ((string t, float w) in a) { na += (double)w * w; if (b.TryGetValue(t, out float v)) { dot += (double)w * v; } }
            foreach ((string t, float w) in b) { nb += (double)w * w; }
            foreach (string t in a.Keys.Union(b.Keys))
            {
                double d = Math.Abs(a.GetValueOrDefault(t) - b.GetValueOrDefault(t));
                maxAbs = Math.Max(maxAbs, d);
                sumAbs += d;
                entries++;
            }

            sameSupport += a.Keys.ToHashSet().SetEquals(b.Keys) ? 1 : 0;
            specials += a.Keys.Concat(b.Keys).Count(t => t.StartsWith('[') && t.EndsWith(']'));
            cos.Add(na == 0 && nb == 0 ? 1 : dot / Math.Sqrt(na * nb));
        }

        Console.WriteLine($"stand-in sparse: {docs.Count} passages; document vector cosine (int8 OneModel, GPU fp32): {Percentiles(cos)}; identical token support on {sameSupport}; maxAbsWeightDiff {maxAbs:F4}; meanAbsWeightDiff {sumAbs / entries:F5}; special tokens {specials}");

        var qvec = new SparseVector[data.Queries.Count];
        encoder.Encode(data.Queries.Select(q => q.Text).ToList(), EmbeddingRole.Query, qvec);
        Dictionary<string, List<(string Doc, double Score)>> Rank(List<SparseVector> vectors)
        {
            var postings = new Dictionary<string, List<(int Doc, float W)>>(StringComparer.Ordinal);
            for (int i = 0; i < vectors.Count; i++)
            {
                for (int k = 0; k < vectors[i].Count; k++)
                {
                    if (!postings.TryGetValue(vectors[i].Tokens[k], out var l)) { postings[vectors[i].Tokens[k]] = l = []; }
                    l.Add((i, vectors[i].Weights[k]));
                }
            }

            var result = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);
            var gate = new object();
            Parallel.For(0, qvec.Length, q =>
            {
                var acc = new Dictionary<int, double>();
                for (int k = 0; k < qvec[q].Count; k++)
                {
                    if (postings.TryGetValue(qvec[q].Tokens[k], out var l)) { foreach ((int d, float w) in l) { acc[d] = acc.GetValueOrDefault(d) + ((double)w * qvec[q].Weights[k]); } }
                }

                var top = acc.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(10).Select(kv => (docs[kv.Key].Id, kv.Value)).ToList();
                lock (gate) { result[data.Queries[q].Id] = top; }
            });
            return result;
        }

        var runGpu = Rank(gpu);
        var runOurs = Rank(ours);
        Console.WriteLine($"  nDCG@10 on the sample sub-corpus (exact float dot products): GPU fp32 {Ndcg(data, runGpu):F4}, int8 OneModel {Ndcg(data, runOurs):F4}; top-10 overlap {Overlap(runGpu, runOurs):F4} (this run encoded passages in {seconds:F0} s)");
        return 0;
    }

    private static int StandInRerank(Dictionary<string, string> o)
    {
        var data = BeirDataset.Open(Path.Combine(Req(o, "data"), Req(o, "set")));
        double budget = double.Parse(Opt(o, "budget-seconds", "1500"), CultureInfo.InvariantCulture);
        int queryCount = int.Parse(Opt(o, "queries", "60"), CultureInfo.InvariantCulture);
        string work = Req(o, "work");
        Directory.CreateDirectory(work);
        var clock = Stopwatch.StartNew();
        var cands = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Req(o, "candidates")))
        {
            using JsonDocument j = JsonDocument.Parse(line);
            cands[j.RootElement.GetProperty("q").GetString()!] = [.. j.RootElement.GetProperty("d").EnumerateArray().Select(static e => e.GetString()!)];
        }

        // Evenly spaced queries, so the sample is not the first few topics only.
        var picked = data.Queries.Where(q => cands.ContainsKey(q.Id)).ToList();
        int step = Math.Max(1, picked.Count / queryCount);
        picked = [.. picked.Where((_, i) => i % step == 0).Take(queryCount)];
        var need = new HashSet<string>(picked.SelectMany(q => cands[q.Id]), StringComparer.Ordinal);
        var passages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Doc d in data.ReadCorpus())
        {
            if (need.Contains(d.Id))
            {
                passages[d.Id] = PassageText(d);
            }
        }

        using CrossEncoderReranker model = CrossEncoderReranker.Open(Req(o, "model"), Req(o, "vocab"), new CrossEncoderOptions
        {
            ModelId = "tool",
            MaxThreads = int.Parse(Opt(o, "threads", "0"), CultureInfo.InvariantCulture),
        });
        string cache = Path.Combine(work, "ours-rerank.jsonl");
        var ours = File.Exists(cache) ? ReadArm(cache) : [];
        double seconds = 0;
        int pairs = 0;
        using (var append = new StreamWriter(cache, append: true))
        {
            foreach ((string qid, string text) in picked)
            {
                if (ours.ContainsKey(qid))
                {
                    continue;
                }

                if (clock.Elapsed.TotalSeconds > budget)
                {
                    Console.WriteLine("budget used; run again to continue");
                    return 3;
                }

                string[] docs = cands[qid];
                var sw = Stopwatch.StartNew();
                float[] s = RerankerExtensions.Score(model, text, [.. docs.Select(d => passages[d])]);
                seconds += sw.Elapsed.TotalSeconds;
                pairs += docs.Length;
                ours[qid] = (docs, s);
                append.Write(JsonSerializer.Serialize(new { q = qid, d = docs, s }));
                append.Write('\n');
                append.Flush();
            }
        }

        if (!File.Exists(Req(o, "gpu-scores")))
        {
            Console.WriteLine($"scored {pairs} pairs ({picked.Count} queries); the GPU scores are not there yet");
            return 4;
        }

        var gpu = ReadArm(Req(o, "gpu-scores"));
        if (picked.Any(q => !gpu.ContainsKey(q.Id)))
        {
            Console.WriteLine("the GPU scores do not cover the sampled queries yet");
            return 4;
        }

        var a = new List<float>();
        var b = new List<float>();
        double maxAbs = 0, sumAbs = 0;
        foreach ((string qid, _) in picked)
        {
            for (int i = 0; i < gpu[qid].Docs.Length; i++)
            {
                a.Add(ours[qid].Scores[i]);
                b.Add(gpu[qid].Scores[i]);
                double d = Math.Abs(ours[qid].Scores[i] - gpu[qid].Scores[i]);
                maxAbs = Math.Max(maxAbs, d);
                sumAbs += d;
            }
        }

        Dictionary<string, List<(string Doc, double Score)>> Rerank(Dictionary<string, (string[] Docs, float[] Scores)> scores)
            => ToRun(picked.Select(q =>
            {
                (string[] docs, float[] s) = scores[q.Id];
                int[] order = [.. Enumerable.Range(0, docs.Length).OrderByDescending(i => s[i]).ThenBy(i => i)];
                return (q.Id, order.Select((i, rank) => (docs[i], (double)(docs.Length - rank))));
            }));
        var runGpu = Rerank(gpu);
        var runOurs = Rerank(ours);
        Console.WriteLine($"stand-in rerank: {picked.Count} queries, {a.Count} pairs; score difference int8 OneModel vs GPU fp32: max {maxAbs:F4}, mean {sumAbs / a.Count:F5}, Spearman {RerankCommands.Spearman([.. a], b.ToArray()):F5}");
        Console.WriteLine($"  nDCG@10 of the {picked.Count} queries reranked: GPU fp32 {Ndcg(data, runGpu):F4}, int8 OneModel {Ndcg(data, runOurs):F4}; top-10 overlap {Overlap(runGpu, runOurs):F4} (this run scored {pairs} pairs in {seconds:F0} s = {pairs / Math.Max(seconds, 1e-9):F1}/s)");
        return 0;
    }
}
