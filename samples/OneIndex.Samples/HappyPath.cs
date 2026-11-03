using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneIndex.Vectors;

namespace OneIndex.Samples.HappyPath;

// The attributed document type.
public class Invoice
{
    [SearchId] public string Id { get; set; } = "";
    [FullText(Boost = 2)] public string Title { get; set; } = "";
    [FullText(Language = "en")] public string Body { get; set; } = "";
    [Filterable, Sortable] public DateTime Modified { get; set; }
    [Keyword, Facet] public string[] Tags { get; set; } = [];
    [Dynamic] public JsonElement Meta { get; set; }
    [Vector(From = nameof(Body))] public float[] Embedding { get; set; } = [];   // auto-embedded when an embedder is set
}

/// <summary>
/// A small deterministic embedder so the sample runs anywhere: each word votes for a few of 32 slots. Applications use a model instead, for example
/// <c>BgeSmallEmbedder.UseAsDefault()</c> from the Models package.
/// </summary>
public sealed class HashingEmbedder : IEmbedder
{
    public string ModelId => "sample-hashing-32";

    public int Dimension => 32;

    public bool IsNormalized => false;

    public void Embed(IReadOnlyList<string> texts, EmbeddingRole role, Span<float> destination, CancellationToken cancellationToken = default)
    {
        destination.Clear();
        for (int i = 0; i < texts.Count; i++)
        {
            Span<float> v = destination.Slice(i * Dimension, Dimension);
            foreach (string w in texts[i].ToLowerInvariant().Split([' ', '.', ',', '"', '-'], StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(w));
                v[h[0] % Dimension] += 1f;
                v[h[1] % Dimension] += 0.5f;
            }

            if (v.IndexOfAnyExcept(0f) < 0)
            {
                v[0] = 1f;
            }
        }
    }

    public ValueTask EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<float> destination, CancellationToken cancellationToken = default)
    {
        Embed(texts, role, destination.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }
}

/// <summary>What the sample observed, for the test that runs it.</summary>
public sealed record Outcome(string[] Ids, string[] Facets, bool HadHighlights, bool Deleted, string ToolName, string[] ParsedIds, long InMemoryCount);

public static class Sample
{
    /// <summary>Runs the sample of the design document against a directory.</summary>
    public static Outcome Run(string directory)
    {
        SearchEmbedders.Default ??= new HashingEmbedder();   // an application sets this once at startup
        DateTime cutoff = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        using var index = SearchIndex.Open<Invoice>(directory);   // thin static entry point, no options object
        index.Upsert(new Invoice { Id = "inv-1", Title = "Overdue notice", Body = "Payment from Acme Corp is overdue since March.", Modified = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice", "overdue"] });
        index.Upsert(new Invoice { Id = "inv-2", Title = "Overdue draft", Body = "Draft of the acme corp reminder, overdue.", Modified = new DateTime(2024, 3, 2, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice", "draft"] });
        index.Upsert(new Invoice { Id = "inv-17", Title = "Old notice", Body = "Acme Corp, overdue in 2022.", Modified = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice"] });
        index.Commit();                                          // commit is atomic and durable
        bool deleted = index.Delete("inv-17");
        index.Commit();

        var r = index.Search("overdue \"acme corp\" -draft")      // forgiving user text, never throws
                     .Where(x => x.Tags.Contains("invoice") && x.Modified > cutoff)
                     .Facets(x => x.Tags)
                     .Take(10);                                 // hybrid automatically, because T has a [Vector(From = ...)] member
        List<string> ids = [];
        bool highlights = false;
        foreach (var hit in r)
        {
            _ = hit.Document;
            _ = hit.Score;
            highlights |= hit.Highlights.Count > 0;
            ids.Add(hit.Id);
        }

        var results = r.Execute();
        string[] facets = [.. results.Facets["Tags"].Values.Select(v => v.Value + "=" + v.Count)];

        string tool = SearchTool.For<Invoice>();                // JSON schema for an LLM tool definition
        string toolCallJson = """{"v":1,"q":"overdue","filter":"Tags = invoice","limit":5}""";
        var q = index.Parse(toolCallJson);                      // validated against the schema; unknown fields named
        string[] parsed = [.. q.ToList().Select(h => h.Id)];

        using var mem = SearchIndex.InMemory<Invoice>();         // same API, no directory
        mem.Upsert(new Invoice { Id = "m", Title = "Memory", Body = "in memory" });
        mem.Commit();

        using JsonDocument toolJson = JsonDocument.Parse(tool);
        return new Outcome([.. ids], facets, highlights, deleted, toolJson.RootElement.GetProperty("name").GetString()!, parsed, mem.Count);
    }
}
