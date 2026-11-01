// oneindex-models: model conversion and embedder measurements.
//
//   convert  <model.onnx> <out.onemodel>
//   parity   --model <file> --vocab <vocab.txt> --texts <texts.jsonl> --reference <vectors.f32> [--role document|query] [--prefix <text>]
//   beir     --model <file> --vocab <vocab.txt> --data <beir root> --set <name> --work <dir> [--instruction on|off] [--budget-seconds N]
//            [--threads N] [--run <file>]
//
// beir embeds the corpus in resumable chunks (exit code 3 when the time budget ran out; run it again to continue), then ranks by exact
// dot product and prints nDCG@10 under the BEIR convention (a document with the query's own id is skipped).
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using OneIndex.Harness;
using OneIndex.Models;
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
