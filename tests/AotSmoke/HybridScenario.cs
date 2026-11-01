using OneIndex.Models;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>A document whose vector is made from its text.</summary>
public sealed class Article
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";

    [Keyword, Filterable]
    public string Topic { get; set; } = "";

    [Vector(From = nameof(Body), Dimensions = 384)]
    public float[]? Embedding { get; set; }
}

/// <summary>Automatic embedding, the embedding cache and hybrid search with the packed default embedder, under NativeAOT.</summary>
internal static class HybridScenario
{
    private static readonly (string Id, string Topic, string Body)[] Corpus =
    [
        ("a1", "food", "How to bake sourdough bread at home with a starter, flour and water."),
        ("a2", "food", "A recipe for tomato soup with basil and garlic."),
        ("a3", "space", "The Moon orbits the Earth and causes ocean tides."),
        ("a4", "space", "Mars has two small moons and a thin atmosphere of carbon dioxide."),
        ("a5", "sport", "The marathon is a 42 kilometre footrace run in cities around the world."),
        ("a6", "sport", "Basketball players dribble and shoot hoops on a hardwood court."),
        ("a7", "tech", "A hash table maps keys to values with constant expected lookup time."),
        ("a8", "tech", "Compilers translate source code into machine instructions."),
    ];

    public static void Run()
    {
        BgeSmallEmbedder embedder;
        try
        {
            embedder = BgeSmallEmbedder.Load();
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("hybrid: skipped (this build has no packed weights)");
            return;
        }

        using (embedder)
        {
            using SearchIndex<Article> index = SearchIndex.InMemory<Article>(embedder);
            index.Upsert(Corpus.Select(static c => new Article { Id = c.Id, Topic = c.Topic, Body = c.Body }));
            index.Commit();

            // Plain text on an index with an embedder is hybrid: the query shares no word with the best document.
            SearchHit<Article>[] hits = [.. index.Search("making loaves of bread").Take(3)];
            if (hits.Length == 0 || hits[0].Id != "a1" || index.Search("making loaves of bread").ToQuery() is not HybridQuery)
            {
                throw new InvalidOperationException("hybrid search did not find the bread article first: " + string.Join(',', hits.Select(static h => h.Id)));
            }

            SearchHit<Article>[] filtered = [.. index.Search("satellites of planets").Where(x => x.Topic == "space").Take(2)];
            if (filtered.Length != 2 || filtered.Any(static h => h.Document.Topic != "space"))
            {
                throw new InvalidOperationException("the filter did not apply to both lists");
            }

            // Reindexing unchanged text costs no embedding: the stored vectors are the cache.
            long before = GC.GetAllocatedBytesForCurrentThread();
            var counting = new CountingEmbedder(embedder);
            using SearchIndex<Article> again = SearchIndex.InMemory<Article>(counting);
            again.Upsert(Corpus.Select(static c => new Article { Id = c.Id, Topic = c.Topic, Body = c.Body }));
            again.Commit();
            int first = counting.Texts;
            again.Upsert(Corpus.Select(static c => new Article { Id = c.Id, Topic = c.Topic, Body = c.Body }));
            if (first != Corpus.Length || counting.Texts != first)
            {
                throw new InvalidOperationException($"embedded {first} then {counting.Texts} texts for {Corpus.Length} documents");
            }

            // Query JSON with a hybrid request goes through the same path.
            SearchHit<Article>[] viaJson = [.. again.Parse("""{"v":1,"q":"making loaves of bread","hybrid":{"semanticRatio":0.75},"limit":1}""")];
            if (viaJson.Length != 1 || viaJson[0].Id != "a1")
            {
                throw new InvalidOperationException("hybrid query JSON did not find the bread article");
            }

            Console.WriteLine($"hybrid: auto-embedding, cache and hybrid search ok ({embedder.Inner.KernelPath}, {(GC.GetAllocatedBytesForCurrentThread() - before) / 1024} KiB)");
        }
    }

    private sealed class CountingEmbedder(IEmbedder inner) : IEmbedder
    {
        public int Texts { get; private set; }

        public string ModelId => inner.ModelId;

        public int Dimension => inner.Dimension;

        public bool IsNormalized => inner.IsNormalized;

        public void Embed(IReadOnlyList<string> texts, EmbeddingRole role, Span<float> destination, CancellationToken cancellationToken = default)
        {
            Texts += texts.Count;
            inner.Embed(texts, role, destination, cancellationToken);
        }

        public ValueTask EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<float> destination, CancellationToken cancellationToken = default)
        {
            Texts += texts.Count;
            return inner.EmbedAsync(texts, role, destination, cancellationToken);
        }
    }
}
