// oneindex-models: model conversion and embedder measurements.
//
//   convert  <model.onnx> <out.onemodel>
//   parity   --model <file> --vocab <vocab.txt> --texts <texts.jsonl> --reference <vectors.f32> [--role document|query] [--prefix <text>]
//   beir     --model <file> --vocab <vocab.txt> --data <beir root> --set <name> --work <dir> [--instruction on|off] [--budget-seconds N]
//            [--threads N] [--run <file>]
//   pair-ids      --vocab <vocab.txt> --pairs <pairs.jsonl>: pair token ids against the reference tokenizer's
//   rerank-parity --model <file> --vocab <vocab.txt> --pairs <scored.jsonl> --reference <scores.f32> [--threads N]
//   rerank-beir   --model <file> --vocab <vocab.txt> --data <beir root> --set <name> --work <dir> [--embedder <onemodel> [--vectors <dir>]]: BM25
//                 (and hybrid) top-100 reranked by the cross-encoder, nDCG@10 before and after; resumable
//   hybrid   as beir (the corpus vectors of a finished beir run are reused), plus --out <dir>: BM25, dense (exact), and hybrid runs
//            through the engine's own query objects; one run file per configuration
//
// beir embeds the corpus in resumable chunks (exit code 3 when the time budget ran out; run it again to continue), then ranks by exact
// dot product and prints nDCG@10 under the BEIR convention (a document with the query's own id is skipped).
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using OneIndex.Analysis;
using OneIndex.Harness;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Models;
using OneIndex.Models.Tool;
using OneIndex.Vectors;
using RecursiveAI.OneModel.Format;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: oneindex-models convert|parity|beir ...");
    return 2;
}

var options = new Dictionary<string, string>();
for (int i = 1; i + 1 < args.Length; i += 2)
{
    options[args[i].TrimStart('-')] = args[i + 1];
}

string Req(string name) => options.TryGetValue(name, out string? v) ? v : throw new ArgumentException($"missing --{name}");
string Opt(string name, string fallback) => options.TryGetValue(name, out string? v) ? v : fallback;
const string BgeQueryInstruction = "Represent this sentence for searching relevant passages: ";

switch (args[0])
{
    case "convert":
        ModelFormat.Convert(args[1], args[2]);
        Console.WriteLine($"{args[2]}: {new FileInfo(args[2]).Length} bytes");
        return 0;

    case "parity":
        return Parity();

    case "beir":
        return Beir();

    case "sparse-parity":
        return SparseCommands.Parity(n => options.GetValueOrDefault(n));

    case "sparse-beir":
        return SparseCommands.Beir(n => options.GetValueOrDefault(n));

    case "sparse-hybrid":
        return SparseCommands.Hybrid(n => options.GetValueOrDefault(n));

    case "hybrid":
        return HybridGate();

    case "nq-index":
    case "nq-run":
    case "nq-eval":
    case "standin-dense":
    case "standin-sparse":
    case "standin-rerank":
        return NqCommands.Run(args[0], options);

    case "pair-ids":
    case "rerank-parity":
    case "rerank-beir":
        return RerankCommands.Run(args[0], options);

    default:
        Console.Error.WriteLine("unknown command");
        return 2;
}

BertEmbedder OpenEmbedder(string prefix) => BertEmbedder.Open(Req("model"), Req("vocab"), new BertEmbedderOptions
{
    ModelId = "tool",
    QueryPrefix = prefix,
    MaxThreads = int.Parse(Opt("threads", "0"), CultureInfo.InvariantCulture),
    ExactArithmetic = Opt("exact", "off") == "on",
    MaxBatchTokens = int.Parse(Opt("batch-tokens", "2048"), CultureInfo.InvariantCulture),
});

int Parity()
{
    var role = Opt("role", "document") == "query" ? EmbeddingRole.Query : EmbeddingRole.Document;
    var texts = File.ReadLines(Req("texts")).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("t").GetString()!).ToList();
    byte[] raw = File.ReadAllBytes(Req("reference"));
    ReadOnlySpan<float> reference = MemoryMarshal.Cast<byte, float>(raw);
    using BertEmbedder embedder = OpenEmbedder(Opt("prefix", ""));
    int d = embedder.Dimension;
    if (reference.Length != texts.Count * d)
    {
        throw new InvalidOperationException($"reference has {reference.Length} floats, expected {texts.Count * d}");
    }

    var sw = Stopwatch.StartNew();
    float[] ours = embedder.EmbedBatch(texts, role);
    sw.Stop();
    var cosines = new double[texts.Count];
    double maxAbs = 0;
    for (int i = 0; i < texts.Count; i++)
    {
        double dot = 0;
        for (int k = 0; k < d; k++)
        {
            dot += (double)ours[(i * d) + k] * reference[(i * d) + k];
            maxAbs = Math.Max(maxAbs, Math.Abs(ours[(i * d) + k] - reference[(i * d) + k]));
        }

        cosines[i] = dot;
    }

    Array.Sort(cosines);
    Console.WriteLine($"texts {texts.Count} dim {d} kernel {embedder.KernelPath} cosine min {cosines[0]:F6} p1 {cosines[texts.Count / 100]:F6} median {cosines[texts.Count / 2]:F6} mean {cosines.Average():F6} maxAbsDiff {maxAbs:F5} seconds {sw.Elapsed.TotalSeconds:F1}");
    return 0;
}

int Beir()
{
    var data = BeirDataset.Open(Path.Combine(Req("data"), Req("set")));
    string work = Path.Combine(Req("work"), Req("set"));
    Directory.CreateDirectory(work);
    bool instruction = Opt("instruction", "on") == "on";
    double budget = double.Parse(Opt("budget-seconds", "1500"), CultureInfo.InvariantCulture);
    var clock = Stopwatch.StartNew();
    using BertEmbedder embedder = OpenEmbedder(instruction ? BgeQueryInstruction : "");
    int d = embedder.Dimension;
    const int ChunkDocs = 500;

    var ids = new List<string>();
    var chunkTexts = new List<List<string>>();
    foreach (Doc doc in data.ReadCorpus())
    {
        ids.Add(doc.Id);
        if (chunkTexts.Count == 0 || chunkTexts[^1].Count == ChunkDocs)
        {
            chunkTexts.Add([]);
        }

        chunkTexts[^1].Add(doc.Title.Length == 0 ? doc.Text : doc.Title + " " + doc.Text);
    }

    int embedded = 0;
    double embedSeconds = 0;
    for (int c = 0; c < chunkTexts.Count; c++)
    {
        string file = Path.Combine(work, $"corpus-{c:D5}.f32");
        if (File.Exists(file))
        {
            continue;
        }

        if (clock.Elapsed.TotalSeconds > budget)
        {
            Console.WriteLine($"budget used after {c} of {chunkTexts.Count} chunks; run again to continue");
            return 3;
        }

        var sw = Stopwatch.StartNew();
        float[] vectors = embedder.EmbedBatch(chunkTexts[c], EmbeddingRole.Document);
        embedSeconds += sw.Elapsed.TotalSeconds;
        embedded += chunkTexts[c].Count;
        File.WriteAllBytes(file + ".part", MemoryMarshal.AsBytes(vectors.AsSpan()).ToArray());
        File.Move(file + ".part", file);
        Console.WriteLine($"chunk {c + 1}/{chunkTexts.Count}: {chunkTexts[c].Count} docs in {sw.Elapsed.TotalSeconds:F1} s ({chunkTexts[c].Count / sw.Elapsed.TotalSeconds:F1} passages/s)");
    }

    float[] corpus = new float[ids.Count * d];
    for (int c = 0; c < chunkTexts.Count; c++)
    {
        MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(work, $"corpus-{c:D5}.f32"))).CopyTo(corpus.AsSpan(c * ChunkDocs * d));
    }

    float[] queries = embedder.EmbedBatch(data.Queries.Select(q => q.Text).ToList(), EmbeddingRole.Query);
    var run = new Dictionary<string, List<(string Doc, double Score)>>(StringComparer.Ordinal);
    for (int q = 0; q < data.Queries.Count; q++)
    {
        var query = queries.AsSpan(q * d, d);
        var top = new List<(string, double)>(11);
        var scores = new float[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            scores[i] = Dot(query, corpus.AsSpan(i * d, d));
        }

        int[] order = Enumerable.Range(0, ids.Count).ToArray();
        Array.Sort(order, (a, b) => scores[b] != scores[a] ? scores[b].CompareTo(scores[a]) : string.CompareOrdinal(ids[b], ids[a]));
        foreach (int i in order)
        {
            if (ids[i] == data.Queries[q].Id)
            {
                continue;
            }

            top.Add((ids[i], scores[i]));
            if (top.Count == 10)
            {
                break;
            }
        }

        run[data.Queries[q].Id] = top;
    }

    (double ndcg, int n) = TrecEval.NdcgAt(10, data.Qrels, run);
    if (options.TryGetValue("run", out string? runFile))
    {
        using var w = new StreamWriter(runFile);
        foreach ((string qid, var hits) in run)
        {
            for (int r = 0; r < hits.Count; r++)
            {
                w.Write($"{qid} Q0 {hits[r].Doc} {r + 1} {hits[r].Score.ToString("R", CultureInfo.InvariantCulture)} bge\n");
            }
        }
    }

    Console.WriteLine($"set {data.Name} docs {ids.Count} queries {n} instruction {(instruction ? "on" : "off")} nDCG@10 {ndcg:F4} (embedding this run: {embedded} passages in {embedSeconds:F1} s) model {Path.GetFileName(Req("model"))} kernel {embedder.KernelPath}");
    return 0;
}

static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
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

int HybridGate()
{
    var data = BeirDataset.Open(Path.Combine(Req("data"), Req("set")));
    string work = Path.Combine(Req("work"), Req("set"));
    string outDir = Req("out");
    Directory.CreateDirectory(outDir);
    using BertEmbedder embedder = OpenEmbedder(BgeQueryInstruction);
    int d = embedder.Dimension;
    const int ChunkDocs = 500;
    string indexDir = Path.Combine(work, "hybrid-index");
    if (Directory.Exists(indexDir))
    {
        Directory.Delete(indexDir, recursive: true);
    }

    Directory.CreateDirectory(indexDir);
    FieldDefinition Text(string name, float boost = 1f) => new(name, FieldType.Text) { Analyzer = "en", IndexOptions = IndexOptions.Freqs, Stored = false, Boost = boost };
    Schema schema = new SchemaBuilder().Id("id").Add(Text("title", 3f)).Add(Text("text"))
        .Add(new FieldDefinition("vec", FieldType.Vector) { Dimensions = d, Metric = VectorMetric.Cosine, Stored = false }).Build();
    var storage = new FileSystemStorage(indexDir);
    var ids = new List<string>();
    using (IndexWriter writer = IndexWriter.Open(storage, schema, new IndexWriterOptions { MaxBufferedDocuments = 20_000 }))
    {
        int chunk = -1;
        float[] vectors = [];
        int inChunk = 0;
        foreach (Doc doc in data.ReadCorpus())
        {
            if (inChunk % ChunkDocs == 0)
            {
                chunk++;
                string file = Path.Combine(work, $"corpus-{chunk:D5}.f32");
                if (!File.Exists(file))
                {
                    Console.WriteLine($"missing {file}: run the beir step to completion first");
                    return 3;
                }

                vectors = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(file)).ToArray();
                inChunk = 0;
            }

            float[] v = vectors.AsSpan(inChunk * d, d).ToArray();
            inChunk++;
            ids.Add(doc.Id);
            writer.AddUnique(new Document().Add("id", doc.Id).Add("title", doc.Title).Add("text", doc.Text).Add("vec", v));
        }

        writer.Commit();
        writer.WaitForMerges();
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
    float[] queryVectors = embedder.EmbedBatch(data.Queries.Select(q => q.Text).ToList(), EmbeddingRole.Query);
    const int Window = 100;
    if (Opt("grid", "off") == "on")
    {
        // Explores fusion settings: both lists are taken once, deep, and fused here with the documented formulas.
        const int Deep = 1000;
        var arms = new List<(List<(string Id, float S)> Text, List<(string Id, float S)> Vec)>();
        for (int q = 0; q < data.Queries.Count; q++)
        {
            var terms = new List<string>();
            TokenSource tokens = AnalyzerRegistry.Create("en").GetTokens(data.Queries[q].Text.AsMemory());
            while (tokens.MoveNext())
            {
                terms.Add(tokens.Token.Term.ToString());
            }

            var bm25 = new BooleanQuery([.. terms.Select(t => new BooleanClause(new MultiFieldTermQuery(["title", "text"], t), Occur.Should))]);
            var knn = new KnnQuery("vec", queryVectors.AsSpan(q * d, d).ToArray(), Deep) { Exact = true };
            arms.Add(([.. searcher.Search(bm25, Deep).Hits.Select(h => (idOf[h.DocId], h.Score))], [.. searcher.Search(knn, Deep).Hits.Select(h => (idOf[h.DocId], h.Score))]));
        }

        double Eval(Func<List<(string Id, float S)>, List<(string Id, float S)>, Dictionary<string, double>> fuse)
        {
            var run = new Dictionary<string, List<(string Doc, double Score)>>(StringComparer.Ordinal);
            for (int q = 0; q < arms.Count; q++)
            {
                Dictionary<string, double> f = fuse(arms[q].Text, arms[q].Vec);
                run[data.Queries[q].Id] = [.. f.Where(kv => kv.Key != data.Queries[q].Id).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(10).Select(kv => (kv.Key, kv.Value))];
            }

            return TrecEval.NdcgAt(10, data.Qrels, run).Mean;
        }

        foreach (int w in new[] { 100 })
        {
            foreach (int k in new[] { 3, 5, 10, 20 })
            {
                foreach (double r in new[] { 0.5, 0.55, 0.6, 0.65, 0.7, 0.75, 0.8 })
                {
                    double v = Eval((t, vec) =>
                    {
                        var f = new Dictionary<string, double>();
                        for (int i = 0; i < Math.Min(w, t.Count); i++) { f[t[i].Id] = f.GetValueOrDefault(t[i].Id) + (2 * (1 - r) / (k + i + 1)); }
                        for (int i = 0; i < Math.Min(w, vec.Count); i++) { f[vec[i].Id] = f.GetValueOrDefault(vec[i].Id) + (2 * r / (k + i + 1)); }
                        return f;
                    });
                    Console.WriteLine($"set {data.Name} grid rrf window {w} k {k} ratio {r} nDCG@10 {v:F4}");
                }
            }
        }

        return 0;
    }

    var configs = new List<(string Name, Func<Query, KnnQuery, Query> Make)>
    {
        ("bm25", (t, _) => t),
        ("dense", (_, k) => k),
        ("rrf", (t, k) => new HybridQuery(t, k)),
        ("rrf-k60-ratio0.5", (t, k) => new HybridQuery(t, k) { RankConstant = 60, SemanticRatio = 0.5 }),
        ("rrf-k10-ratio0.5", (t, k) => new HybridQuery(t, k) { SemanticRatio = 0.5 }),
        ("rrf-k10-ratio0.3", (t, k) => new HybridQuery(t, k) { SemanticRatio = 0.3 }),
        ("linear-0.3", (t, k) => new HybridQuery(t, k) { Fusion = FusionMethod.Linear, SemanticRatio = 0.3 }),
        ("linear-0.5", (t, k) => new HybridQuery(t, k) { Fusion = FusionMethod.Linear, SemanticRatio = 0.5 }),
        ("linear-0.7", (t, k) => new HybridQuery(t, k) { Fusion = FusionMethod.Linear, SemanticRatio = 0.7 }),
    };
    foreach ((string name, Func<Query, KnnQuery, Query> make) in configs)
    {
        var run = new Dictionary<string, List<(string Doc, double Score)>>(StringComparer.Ordinal);
        for (int q = 0; q < data.Queries.Count; q++)
        {
            var terms = new List<string>();
            TokenSource tokens = AnalyzerRegistry.Create("en").GetTokens(data.Queries[q].Text.AsMemory());
            while (tokens.MoveNext())
            {
                terms.Add(tokens.Token.Term.ToString());
            }

            var bm25 = new BooleanQuery([.. terms.Select(t => new BooleanClause(new MultiFieldTermQuery(["title", "text"], t), Occur.Should))]);
            var knn = new KnnQuery("vec", queryVectors.AsSpan(q * d, d).ToArray(), Window) { Exact = true };
            TopDocs top = searcher.Search(make(bm25, knn), 11);
            var list = new List<(string, double)>(10);
            foreach (ScoreDoc sd in top.Hits)
            {
                if (idOf[sd.DocId] == data.Queries[q].Id)
                {
                    continue;
                }

                list.Add((idOf[sd.DocId], sd.Score));
                if (list.Count == 10)
                {
                    break;
                }
            }

            run[data.Queries[q].Id] = list;
        }

        (double ndcg, int n) = TrecEval.NdcgAt(10, data.Qrels, run);
        using (var w = new StreamWriter(Path.Combine(outDir, $"{data.Name}.{name}.run")))
        {
            foreach ((string qid, var hits) in run)
            {
                for (int r = 0; r < hits.Count; r++)
                {
                    w.Write($"{qid} Q0 {hits[r].Doc} {r + 1} {hits[r].Score.ToString("R", CultureInfo.InvariantCulture)} {name}\n");
                }
            }
        }

        Console.WriteLine($"set {data.Name} config {name} queries {n} nDCG@10 {ndcg:F4}");
    }

    return 0;
}
