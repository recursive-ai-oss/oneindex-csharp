using OneIndex.Index;
using OneIndex.Models;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>A document whose learned sparse vector is made from its text.</summary>
public sealed class SparseArticle
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";

    [Sparse(From = nameof(Body))]
    public SparseVector? Expansion { get; set; }
}

/// <summary>A deterministic encoder: every word weighs 1 as a query and 1 + (length / 10) as a document.</summary>
internal sealed class WordSparseEncoder : ISparseEncoder
{
    public string ModelId => "words";

    public void Encode(IReadOnlyList<string> texts, EmbeddingRole role, Span<SparseVector> destination, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < texts.Count; i++)
        {
            var entries = new List<KeyValuePair<string, float>>();
            foreach (string w in texts[i].ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                entries.Add(new(w, role == EmbeddingRole.Query ? 1f : 1f + (w.Length / 10f)));
            }

            destination[i] = new SparseVector(entries);
        }
    }

    public ValueTask EncodeAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<SparseVector> destination, CancellationToken cancellationToken = default)
    {
        Encode(texts, role, destination.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Sparse auto-encoding, the cache, a sparse query, a hybrid arm and query JSON under NativeAOT; the packed model too when this build has it.</summary>
internal static class SparseScenario
{
    private static readonly string[] Bodies = ["red apple pie", "green apple tart", "blue ocean wave", "red ocean sunset"];

    public static void Run()
    {
        using SearchIndex<SparseArticle> index = SearchIndex.InMemory<SparseArticle>(null, new WordSparseEncoder());
        index.Upsert(Bodies.Select(static (b, i) => new SparseArticle { Id = "s" + i, Body = b }));
        index.Commit();
        SearchHit<SparseArticle>[] hits = [.. index.Search("red apple").Sparse().Take(2)];
        if (hits.Length == 0 || hits[0].Id != "s0")
        {
            throw new InvalidOperationException("the sparse search did not find the red apple article first: " + string.Join(',', hits.Select(static h => h.Id)));
        }

        SearchHit<SparseArticle>[] direct = [.. index.SearchBy(new SparseQuery("Expansion", new SparseVector([new("ocean", 1f)]))).Take(5)];
        if (direct.Length != 2 || direct.Any(static h => !h.Document.Body.Contains("ocean", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("the sparse query did not match exactly the ocean articles");
        }

        SearchHit<SparseArticle>[] viaJson = [.. index.Parse("{\"v\":1,\"query\":{\"type\":\"sparse\",\"field\":\"Expansion\",\"weights\":{\"tart\":2}}}").Take(5)];
        if (viaJson.Length != 1 || viaJson[0].Id != "s1")
        {
            throw new InvalidOperationException("the sparse query JSON node did not find the tart article");
        }

        Console.WriteLine("sparse: ok");
        RunPacked();
    }

    private static void RunPacked()
    {
        OpenSearchSparseEncoder encoder;
        try
        {
            encoder = OpenSearchSparseEncoder.Load();
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("sparse model: skipped (this build has no packed weights)");
            return;
        }

        using (encoder)
        {
            using SearchIndex<SparseArticle> index = SearchIndex.InMemory<SparseArticle>(null, encoder);
            index.Upsert(Bodies.Select(static (b, i) => new SparseArticle { Id = "s" + i, Body = b }));
            index.Commit();
            SearchHit<SparseArticle>[] hits = [.. index.Search("red apple").Sparse().Take(2)];
            if (hits.Length == 0 || hits[0].Id != "s0")
            {
                throw new InvalidOperationException("the packed sparse model did not find the red apple article first");
            }

            Console.WriteLine("sparse model: ok");
        }
    }
}
