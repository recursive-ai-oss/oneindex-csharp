using System.Security.Cryptography;
using System.Text;
using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>A document whose vector length comes from the embedder.</summary>
public sealed class Memo
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Text { get; set; } = "";

    [Vector(From = nameof(Text))]
    public float[] Embedding { get; set; } = [];
}

/// <summary>A small deterministic embedder: each word votes for a few of 16 slots.</summary>
internal sealed class SlotEmbedder : IEmbedder
{
    public string ModelId => "slots-16";

    public int Dimension => 16;

    public bool IsNormalized => false;

    public void Embed(IReadOnlyList<string> texts, EmbeddingRole role, Span<float> destination, CancellationToken cancellationToken = default)
    {
        destination.Clear();
        for (int i = 0; i < texts.Count; i++)
        {
            Span<float> v = destination.Slice(i * Dimension, Dimension);
            foreach (string w in texts[i].ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
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

/// <summary>A vector without Dimensions, the asynchronous bulk upsert and list, and an excluded word in an automatic hybrid search, under NativeAOT.</summary>
internal static class UnsizedVectorScenario
{
    public static void Run()
    {
        using SearchIndex<Memo> index = SearchIndex.InMemory<Memo>(new SlotEmbedder());
        index.UpsertAsync([new Memo { Id = "keep", Text = "overdue notice acme" }, new Memo { Id = "drop", Text = "overdue draft acme" }]).AsTask().GetAwaiter().GetResult();
        index.Commit();
        string[] ids = [.. index.Search("overdue acme -draft").Take(10).ToListAsync().AsTask().GetAwaiter().GetResult().Select(static h => h.Id)];
        if (ids.Length != 1 || ids[0] != "keep" || index.Schema.Fields.Single(static f => f.Name == "Embedding").Dimensions != 16)
        {
            throw new InvalidOperationException("unsized vector scenario failed: " + string.Join(",", ids));
        }

        Console.WriteLine("unsized vector: ok");
    }
}
