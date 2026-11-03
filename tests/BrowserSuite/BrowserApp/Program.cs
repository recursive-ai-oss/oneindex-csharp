using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OneIndex;
using OneIndex.Browser;
using OneIndex.BrowserSuite;
using OneIndex.Storage;
using OneIndex.StaticIndexes;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

// Runs inside the browser's WebAssembly runtime. Arguments: the address of the static index and a mode:
//   http    opens the static index over HTTP and runs the suite's queries (text, filter, hybrid, vector, highlight);
//   memory  downloads every chunk into a MemoryStorage and opens that;
//   opfs    saves the downloaded files to the origin-private file system, loads them back into memory and opens that.
// It prints one RESULT line per query, STAT lines (milliseconds, bytes and requests per query) and TOTAL.
string baseUrl = args.Length > 0 ? args[0] : throw new ArgumentException("The address of the static index is required.");
string mode = args.Length > 1 ? args[1] : "http";
using var http = new HttpClient();
var source = new HttpStaticSource(http, new Uri(baseUrl));
byte[] queries = await source.FetchAsync("queries.json");
long before = source.BytesFetched;
var clock = Stopwatch.StartNew();
StaticIndex<BrowserDoc> index = await StaticIndex.OpenAsync<BrowserDoc>(source, maxCacheBytes: 1L << 30);
QuerySpec.IHost host;
SearchIndex<BrowserDoc>? local = null;
if (mode == "http")
{
    host = new HttpHost(index);
}
else
{
    var memory = new MemoryStorage();
    foreach (StaticFile f in index.Storage.Manifest.Files)
    {
        if (f.Name.StartsWith("ivf-", StringComparison.Ordinal))
        {
            continue;
        }

        using IRangeReader r = await index.Storage.OpenReadAsync(f.Name);
        var bytes = new byte[r.Length];
        await r.ReadExactlyAsync(0, bytes);
        using IStorageWriter w = memory.Create(f.Name);
        w.Write(bytes);
        w.Sync();
    }

    if (mode == "opfs")
    {
        await JsOpfsHost.InitAsync();
        var opfs = new JsOpfsHost();
        await OpfsPersistence.SaveAsync(memory, opfs);
        var reloaded = new MemoryStorage();
        int loaded = await OpfsPersistence.LoadAsync(opfs, reloaded);
        Console.WriteLine($"OPFS files={loaded}");
        memory = reloaded;
    }

    local = SearchIndex.OpenReadOnly<BrowserDoc>(memory);
    host = new MemoryHost(local);
}

var lines = new List<string>();
var perQuery = new List<string>();
using (JsonDocument doc = JsonDocument.Parse(queries))
{
    // Each query alone, so its latency and bytes can be told apart (the chunk cache is warm after the first ones, as in a session).
    int n = 0;
    foreach (JsonElement q in doc.RootElement.EnumerateArray())
    {
        long b0 = source.BytesFetched;
        int r0 = source.Requests;
        long t0 = clock.ElapsedMilliseconds;
        byte[] one = Encoding.UTF8.GetBytes("[" + q.GetRawText() + "]");
        List<string> got = await QuerySpec.RunAsync(host, one);
        if (got.Count > 0)
        {
            lines.Add(n + got[0][got[0].IndexOf('|', StringComparison.Ordinal)..]);
        }

        perQuery.Add($"{n}|{clock.ElapsedMilliseconds - t0}|{source.BytesFetched - b0}|{source.Requests - r0}");
        n++;
    }
}

foreach (string l in lines) { Console.WriteLine("RESULT " + l); }
foreach (string l in perQuery) { Console.WriteLine("STAT " + l); }
Console.WriteLine($"TOTAL mode={mode} bytes={source.BytesFetched - before} requests={source.Requests} retries={index.Retries} ms={clock.ElapsedMilliseconds}");
local?.Dispose();
Console.WriteLine("DONE");
return 0;

internal sealed class HttpHost(StaticIndex<BrowserDoc> index) : QuerySpec.IHost
{
    public ValueTask<List<string>> QueryAsync(Func<SearchIndex<BrowserDoc>, List<string>> query) => index.QueryAsync(query);

    public async ValueTask<IReadOnlyList<(BrowserDoc Document, float Score)>?> VectorAsync(float[] vector, int k, Func<string, bool>? accept)
        => await index.SearchVectorAsync(nameof(BrowserDoc.Embedding), vector, k, 8, accept).ConfigureAwait(false);
}

internal sealed class MemoryHost(SearchIndex<BrowserDoc> index) : QuerySpec.IHost
{
    public ValueTask<List<string>> QueryAsync(Func<SearchIndex<BrowserDoc>, List<string>> query) => ValueTask.FromResult(query(index));

    public ValueTask<IReadOnlyList<(BrowserDoc Document, float Score)>?> VectorAsync(float[] vector, int k, Func<string, bool>? accept)
        => ValueTask.FromResult<IReadOnlyList<(BrowserDoc Document, float Score)>?>(null);
}
