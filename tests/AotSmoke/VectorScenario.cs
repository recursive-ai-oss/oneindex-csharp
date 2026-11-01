using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>A mapped type with a vector, for the happy path.</summary>
public class Clip
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Keyword]
    [Filterable]
    public string Kind { get; set; } = string.Empty;

    [Vector(Dimensions = 6, FlatThreshold = 8)]
    public float[] Embedding { get; set; } = [];
}

/// <summary>Vector search: flush and merge with graphs, exact and graph search, filters, deletes, query JSON, and the happy path.</summary>
internal static class VectorScenario
{
    private static float[] Vector(Random rng)
    {
        var v = new float[6];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = (float)(rng.NextDouble() - 0.5);
        }

        return v;
    }

    public static void RunLowLevel()
    {
        Schema schema = new SchemaBuilder().Id("id").Keyword("kind")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = 6, FlatThreshold = 8, HnswM = 4, EfConstruction = 16 })
            .Build();
        var rng = new Random(5);
        using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), schema, new IndexWriterOptions { MaxBufferedDocuments = 40, AutoMerge = false });
        for (int i = 0; i < 120; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("kind", i % 3 == 0 ? "a" : "b").Add("v", Vector(rng)));
        }

        writer.Commit();
        writer.DeleteById("d7");
        writer.ForceMerge(1);
        writer.Commit();
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snapshot);
        float[] q = Vector(rng);
        TopDocs exact = searcher.Search(new KnnQuery("v", q, 5) { Exact = true }, 5);
        TopDocs graph = searcher.Search(new KnnQuery("v", q, 5) { Ef = 100 }, 5);
        TopDocs filtered = searcher.Search(new KnnQuery("v", q, 5) { Filter = new TermQuery("kind", "a") }, 5);
        Require(exact.Hits.Count == 5 && graph.Hits.Count == 5 && filtered.Hits.Count == 5, "vector hits");
        Require(exact.Hits.Select(h => h.DocId).Intersect(graph.Hits.Select(h => h.DocId)).Count() >= 4, "graph recall");
        Require(searcher.Context.Leaves[0].Reader.GetVectors("v") is { HasGraph: true, Count: 119 }, "graph after merge");
        Require(searcher.Context.Leaves[0].Reader.GetVectors("v")!.Metric == VectorMetric.Cosine, "metric");
    }

    public static void RunHappyPath()
    {
        var rng = new Random(6);
        using SearchIndex<Clip> index = SearchIndex.InMemory<Clip>();
        for (int i = 0; i < 60; i++)
        {
            index.Upsert(new Clip { Id = "c" + i, Kind = i % 2 == 0 ? "x" : "y", Embedding = Vector(rng) });
        }

        index.Commit();
        float[] q = Vector(rng);
        SearchResults<Clip> hits = index.SearchVector(c => c.Embedding, q, neighbors: 10).Where(c => c.Kind == "x").Take(5).Execute();
        Require(hits.Count == 5 && hits.All(h => h.Document.Kind == "x" && h.Document.Embedding.Length == 6), "happy path");
        SearchResults<Clip> json = index.Parse("{\"v\":1,\"query\":{\"type\":\"knn\",\"field\":\"Embedding\",\"vector\":[" + string.Join(",", q.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "],\"k\":3}}").Execute();
        Require(json.Count == 3, "knn query json");
        Require(SearchTool.For<Clip>().Contains("knn0", StringComparison.Ordinal), "tool schema");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("vector check failed: " + what);
        }
    }
}
