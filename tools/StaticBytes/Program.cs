using System.Text.Json;
using OneIndex;
using OneIndex.BrowserSuite;
using OneIndex.StaticIndexes;

// Counts the bytes a static index fetches per query, per file kind, for the browser suite's queries (tools/BrowserFixture writes the
// static directory and queries.json). Cold: the first query of each kind starts with an empty chunk cache, as a new page would.
// With --index <directory of the source index>, every text, filter, hybrid and highlight result is also compared with the same query on the
// ordinary (non-static) index, and the number of equal results is printed.
// Usage: StaticBytes <static directory> [--index <source index directory>]
string dir = args[0];
string? indexDir = args.Length >= 3 && args[1] == "--index" ? args[2] : null;
byte[] queries = File.ReadAllBytes(Path.Combine(dir, "queries.json"));
var source = new CountingSource(new DirectoryStaticSource(dir));
StaticIndex<BrowserDoc> index = await StaticIndex.OpenAsync<BrowserDoc>(source);
long afterOpen = source.BytesFetched;
Console.WriteLine($"open: {afterOpen} bytes in {source.Requests} requests");

using JsonDocument doc = JsonDocument.Parse(queries);
var kinds = new Dictionary<string, (int N, long Bytes, int Requests)>();
var byFile = new Dictionary<string, long>();
var byKindFile = new Dictionary<string, long>();
int i = 0;
foreach (JsonElement q in doc.RootElement.EnumerateArray())
{
    byte[] one = JsonSerializer.SerializeToUtf8Bytes(new[] { q });
    long b0 = source.BytesFetched;
    int r0 = source.Requests;
    source.PerFile.Clear();
    await QuerySpec.RunAsync(index, one);
    long bytes = source.BytesFetched - b0;
    int requests = source.Requests - r0;
    string kind = q.GetProperty("kind").GetString()!;
    (int n, long total, int reqs) = kinds.GetValueOrDefault(i == 0 ? "first" : kind);
    kinds[i == 0 ? "first" : kind] = (n + 1, total + bytes, reqs + requests);
    if (i == 0)
    {
        foreach ((string f, long v) in source.PerFile.OrderByDescending(static kv => kv.Value))
        {
            Console.WriteLine($"  first query: {v,10} {f}");
        }
    }

    foreach ((string f, long v) in source.PerFile)
    {
        string ext = Path.GetExtension(f.Split(".c0")[0]);
        byFile[ext] = byFile.GetValueOrDefault(ext) + (i == 0 ? 0 : v);
        if (i != 0)
        {
            byKindFile[kind + ext] = byKindFile.GetValueOrDefault(kind + ext) + v;
        }
    }

    i++;
}

foreach ((string kind, (int n, long bytes, int requests)) in kinds)
{
    Console.WriteLine($"{kind,-10} queries {n,3}  avg {bytes / n / 1024.0,9:F1} KB  {requests / (double)n,5:F1} requests");
}

if (indexDir is not null)
{
    using SearchIndex<BrowserDoc> local = SearchIndex.Open<BrowserDoc>(indexDir, null);
    List<string> expected = await QuerySpec.RunAsync(new LocalHost(local), queries);
    var viaStatic = new SearchIndexHost(index);
    List<string> actual = await QuerySpec.RunAsync(viaStatic, queries);
    int equal = 0;
    int compared = 0;
    foreach (string line in expected)
    {
        string prefix = line[..(line.IndexOf('|', StringComparison.Ordinal))] + "|";
        string? other = actual.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        compared++;
        equal += other == line ? 1 : 0;
        if (other != line)
        {
            Console.WriteLine("DIFFERENT: " + line[..Math.Min(120, line.Length)] + "\n           " + (other ?? "missing")[..Math.Min(120, (other ?? "missing").Length)]);
        }
    }

    Console.WriteLine($"equal to the ordinary index: {equal} of {compared} queries (vector queries use the IVF file and are not in the comparison)");
}

foreach (string kind in kinds.Keys.Where(static k => k != "first"))
{
    int n = kinds[kind].N;
    Console.WriteLine($"  {kind,-10} per query, KB by file kind: " + string.Join(", ", byKindFile.Where(kv => kv.Key.StartsWith(kind + ".", StringComparison.Ordinal)).OrderByDescending(static kv => kv.Value).Select(kv => kv.Key[kind.Length..] + "=" + (kv.Value / n / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
}

Console.WriteLine("later queries, bytes by file kind: " + string.Join(", ", byFile.OrderByDescending(static kv => kv.Value).Select(static kv => kv.Key + "=" + kv.Value / 1024 + "KB")));

sealed class CountingSource(IStaticSource inner) : IStaticSource
{
    public Dictionary<string, long> PerFile { get; } = [];

    public long BytesFetched => inner.BytesFetched;

    public int Requests => inner.Requests;

    public async ValueTask<byte[]> FetchAsync(string objectName, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await inner.FetchAsync(objectName, cancellationToken).ConfigureAwait(false);
        PerFile[objectName] = PerFile.GetValueOrDefault(objectName) + bytes.Length;
        return bytes;
    }
}

sealed class LocalHost(SearchIndex<BrowserDoc> index) : QuerySpec.IHost
{
    public ValueTask<List<string>> QueryAsync(Func<SearchIndex<BrowserDoc>, List<string>> query) => ValueTask.FromResult(query(index));

    public ValueTask<IReadOnlyList<(BrowserDoc Document, float Score)>?> VectorAsync(float[] vector, int k, Func<string, bool>? accept)
        => ValueTask.FromResult<IReadOnlyList<(BrowserDoc Document, float Score)>?>(null);
}

sealed class SearchIndexHost(StaticIndex<BrowserDoc> index) : QuerySpec.IHost
{
    public ValueTask<List<string>> QueryAsync(Func<SearchIndex<BrowserDoc>, List<string>> query) => index.QueryAsync(query);

    public ValueTask<IReadOnlyList<(BrowserDoc Document, float Score)>?> VectorAsync(float[] vector, int k, Func<string, bool>? accept)
        => ValueTask.FromResult<IReadOnlyList<(BrowserDoc Document, float Score)>?>(null);
}
