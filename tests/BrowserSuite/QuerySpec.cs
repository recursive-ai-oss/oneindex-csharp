using System.Globalization;
using System.Text;
using System.Text.Json;
using OneIndex.Highlighting;
using OneIndex.StaticIndexes;

namespace OneIndex.BrowserSuite;

/// <summary>One query of the suite, shared by the server-side fixture and the browser app so both run exactly the same code.</summary>
public static class QuerySpec
{
    /// <summary>Runs the queries of a <c>queries.json</c> against a static index and returns one line per query: <c>index|kind|id:score id:score ...</c>.</summary>
    public static Task<List<string>> RunAsync(StaticIndex<BrowserDoc> index, byte[] queriesJson)
        => RunAsync(new StaticHost(index), queriesJson);

    /// <summary>Where the queries run: a static index, or an index held in memory.</summary>
    public interface IHost
    {
        ValueTask<List<string>> QueryAsync(Func<SearchIndex<BrowserDoc>, List<string>> query);

        /// <summary>Vector search, or null when the host has no IVF file (the line is then skipped).</summary>
        ValueTask<IReadOnlyList<(BrowserDoc Document, float Score)>?> VectorAsync(float[] vector, int k, Func<string, bool>? accept);
    }

    private sealed class StaticHost(StaticIndex<BrowserDoc> index) : IHost
    {
        public ValueTask<List<string>> QueryAsync(Func<SearchIndex<BrowserDoc>, List<string>> query) => index.QueryAsync(query);

        public async ValueTask<IReadOnlyList<(BrowserDoc Document, float Score)>?> VectorAsync(float[] vector, int k, Func<string, bool>? accept)
            => await index.SearchVectorAsync(nameof(BrowserDoc.Embedding), vector, k, 8, accept).ConfigureAwait(false);
    }

    /// <summary>Runs the queries against any host.</summary>
    public static async Task<List<string>> RunAsync(IHost host, byte[] queriesJson)
    {
        var lines = new List<string>();
        using JsonDocument doc = JsonDocument.Parse(queriesJson);
        int n = 0;
        foreach (JsonElement q in doc.RootElement.EnumerateArray())
        {
            string kind = q.GetProperty("kind").GetString()!;
            string? text = q.TryGetProperty("text", out JsonElement t) ? t.GetString() : null;
            string? filter = q.TryGetProperty("filter", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            int take = q.TryGetProperty("take", out JsonElement tk) ? tk.GetInt32() : 10;
            float[]? vector = q.TryGetProperty("vector", out JsonElement v) ? [.. v.EnumerateArray().Select(static x => x.GetSingle())] : null;
            var sb = new StringBuilder();
            sb.Append(n).Append('|').Append(kind).Append('|');
            if (kind == "vector")
            {
                string? accept = q.TryGetProperty("idSuffix", out JsonElement s) ? s.GetString() : null;
                IReadOnlyList<(BrowserDoc Document, float Score)>? hits = await host.VectorAsync(vector!, take, accept is null ? null : id => id.EndsWith(accept, StringComparison.Ordinal));
                if (hits is null)
                {
                    n++;
                    continue;
                }

                foreach ((BrowserDoc d, float score) in hits)
                {
                    sb.Append(d.Id).Append(':').Append(score.ToString("F4", CultureInfo.InvariantCulture)).Append(' ');
                }
            }
            else
            {
                List<string> hits = await host.QueryAsync(i =>
                {
                    SearchQuery<BrowserDoc> sq = i.Search(text);
                    if (filter is not null)
                    {
                        sq = sq.Where(filter);
                    }

                    if (kind == "hybrid")
                    {
                        sq = sq.Hybrid(nameof(BrowserDoc.Embedding), vector!);
                    }

                    if (kind == "highlight")
                    {
                        sq = sq.Highlight(new HighlightOptions());
                    }

                    var result = new List<string>();
                    foreach (SearchHit<BrowserDoc> h in sq.Take(take).Execute())
                    {
                        string extra = kind == "highlight" ? "/" + string.Join(",", h.Highlights.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => kv.Key + "=" + string.Join("~", kv.Value.Select(static fr => fr.Text + "@" + fr.Offset))))  : string.Empty;
                        result.Add(h.Document.Id + ":" + h.Score.ToString("F4", CultureInfo.InvariantCulture) + extra);
                    }

                    return result;
                });
                sb.Append(string.Join(' ', hits)).Append(' ');
            }

            lines.Add(sb.ToString().TrimEnd());
            n++;
        }

        return lines;
    }
}
