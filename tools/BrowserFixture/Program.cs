using System.Text;
using System.Text.Json;
using OneIndex;
using OneIndex.BrowserSuite;
using OneIndex.StaticIndexes;

// Builds what the browser suite needs: an index of a corpus, its static form, the queries, and what a server computes for them.
// Usage: BrowserFixture --out <dir> [--corpus <abstracts.jsonl>] [--docs N]
string? outDir = null, corpusPath = null;
int count = 3000;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--corpus": corpusPath = args[++i]; break;
        case "--docs": count = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}

if (outDir is null)
{
    Console.Error.WriteLine("usage: BrowserFixture --out <dir> [--corpus <jsonl>] [--docs N]");
    return 2;
}

Directory.CreateDirectory(outDir);
string indexDir = Path.Combine(outDir, "index");
string staticDir = Path.Combine(outDir, "static");
if (Directory.Exists(indexDir)) { Directory.Delete(indexDir, true); }
if (Directory.Exists(staticDir)) { Directory.Delete(staticDir, true); }

List<BrowserDoc> docs = corpusPath is null ? Synthetic(count) : FromJsonl(corpusPath, count);
using (SearchIndex<BrowserDoc> index = SearchIndex.Open<BrowserDoc>(indexDir, null))
{
    for (int i = 0; i < docs.Count; i += 2000)
    {
        index.Upsert(docs.Skip(i).Take(2000));
        index.Commit();
    }
}

StaticManifest manifest = StaticIndexBuilder.Build<BrowserDoc>(indexDir, staticDir, new StaticBuildOptions { ChunkBytes = 64 * 1024 });
byte[] queries = Queries(docs);
File.WriteAllBytes(Path.Combine(staticDir, "queries.json"), queries);

StaticIndex<BrowserDoc> server = await StaticIndex.OpenAsync<BrowserDoc>(new DirectoryStaticSource(staticDir));
List<string> lines = await QuerySpec.RunAsync(server, queries);
File.WriteAllLines(Path.Combine(outDir, "expected.txt"), lines);
Console.WriteLine($"docs={docs.Count} files={manifest.Files.Count} bytes={manifest.Files.Sum(static f => f.Size)} queries={lines.Count} retries={server.Retries}");
return 0;

static float[] Embed(string text)
{
    var v = new float[BrowserDoc.Dimensions];
    foreach (string w in text.ToLowerInvariant().Split([' ', ',', '.', ';', ':', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries))
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in w) { h = (h ^ c) * 1099511628211UL; }
        for (int d = 0; d < v.Length; d++)
        {
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
            v[d] += (h & 1) == 0 ? 1f : -1f;
        }
    }

    float norm = MathF.Sqrt(v.Sum(static x => x * x));
    if (norm > 0) { for (int d = 0; d < v.Length; d++) { v[d] /= norm; } }
    return v;
}

static BrowserDoc Make(string id, string title, string body)
{
    uint g = 2166136261;
    foreach (char c in id) { g = (g ^ c) * 16777619; }
    return new BrowserDoc { Id = id, Title = title, Body = body, Group = "g" + (g % 5), Length = body.Length, Embedding = Embed(title + " " + body) };
}

static List<BrowserDoc> Synthetic(int n)
{
    string[] words = ["river", "garden", "engine", "market", "winter", "signal", "harbor", "lantern", "meadow", "copper", "violin", "glacier", "orchard", "compass", "thunder", "saffron", "bridge", "castle", "forest", "island"];
    var rng = new Random(17);
    var docs = new List<BrowserDoc>();
    for (int i = 0; i < n; i++)
    {
        string title = words[rng.Next(words.Length)] + " " + words[rng.Next(words.Length)];
        string body = string.Join(' ', Enumerable.Range(0, 40).Select(_ => words[rng.Next(words.Length)]));
        docs.Add(Make("doc" + i, title, body));
    }

    return docs;
}

static List<BrowserDoc> FromJsonl(string path, int n)
{
    var docs = new List<BrowserDoc>();
    foreach (string line in File.ReadLines(path))
    {
        if (docs.Count >= n) { break; }
        using JsonDocument j = JsonDocument.Parse(line);
        string id = j.RootElement.GetProperty("id").ToString();
        string title = j.RootElement.GetProperty("title").GetString() ?? string.Empty;
        string text = j.RootElement.GetProperty("text").GetString() ?? string.Empty;
        if (text.Length == 0) { continue; }
        docs.Add(Make("w" + id, title, text));
    }

    return docs;
}

static byte[] Queries(List<BrowserDoc> docs)
{
    var rng = new Random(99);
    string[] vocab = [.. docs.SelectMany(static d => (d.Title + " " + d.Body).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(static w => w.Length is > 3 and < 12 && w.All(char.IsLetter)).Select(static w => w.ToLowerInvariant()).GroupBy(static w => w).Where(static g => g.Count() > 2).Select(static g => g.Key).Order(StringComparer.Ordinal)];
    string Word() => vocab[rng.Next(vocab.Length)];
    float[] Near(BrowserDoc d) { float[] v = (float[])d.Embedding!.Clone(); for (int i = 0; i < v.Length; i++) { v[i] += (float)((rng.NextDouble() - 0.5) * 0.05); } return v; }
    using var stream = new MemoryStream();
    using (var w = new Utf8JsonWriter(stream))
    {
        w.WriteStartArray();
        void Q(string kind, string? text, string? filter, float[]? vector, string? suffix = null)
        {
            w.WriteStartObject();
            w.WriteString("kind", kind);
            if (text is not null) { w.WriteString("text", text); }
            if (filter is not null) { w.WriteString("filter", filter); }
            if (vector is not null) { w.WriteStartArray("vector"); foreach (float x in vector) { w.WriteNumberValue(x); } w.WriteEndArray(); }
            if (suffix is not null) { w.WriteString("idSuffix", suffix); }
            w.WriteNumber("take", 10);
            w.WriteEndObject();
        }

        for (int i = 0; i < 12; i++) { Q("text", i % 3 == 0 ? Word() : Word() + " " + Word(), null, null); }
        for (int i = 0; i < 8; i++) { Q("filter", Word() + " " + Word(), i % 2 == 0 ? "Group = g" + rng.Next(5) : "Length > " + (50 + rng.Next(300)), null); }
        for (int i = 0; i < 8; i++) { BrowserDoc d = docs[rng.Next(docs.Count)]; Q("hybrid", Word() + " " + Word(), null, Near(d)); }
        for (int i = 0; i < 8; i++) { BrowserDoc d = docs[rng.Next(docs.Count)]; Q("vector", null, null, Near(d), i % 4 == 3 ? "1" : null); }
        for (int i = 0; i < 4; i++) { Q("highlight", Word(), null, null); }
        w.WriteEndArray();
    }

    return stream.ToArray();
}
