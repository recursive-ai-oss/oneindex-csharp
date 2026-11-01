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
            .Add(new FieldDefinition("v8", FieldType.Vector) { Dimensions = 6, FlatThreshold = 8, HnswM = 4, EfConstruction = 16, Quantization = VectorQuantization.Scalar8 })
            .Add(new FieldDefinition("v4", FieldType.Vector) { Dimensions = 6, FlatThreshold = 8, HnswM = 4, EfConstruction = 16, Quantization = VectorQuantization.Scalar4 })
            .Add(new FieldDefinition("v1", FieldType.Vector) { Dimensions = 6, FlatThreshold = 8, HnswM = 4, EfConstruction = 16, Quantization = VectorQuantization.OneBit, Oversample = 20 })
            .Build();
        var rng = new Random(5);
        using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), schema, new IndexWriterOptions { MaxBufferedDocuments = 40, AutoMerge = false });
        for (int i = 0; i < 120; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("kind", i % 3 == 0 ? "a" : "b").Add("v", Vector(rng)).Add("v8", Vector(rng)).Add("v4", Vector(rng)).Add("v1", Vector(rng)));
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
        foreach (string field in new[] { "v8", "v4", "v1" })
        {
            // A compressed copy chooses candidates and the full-precision vectors order them, so the best hits agree with exact search.
            TopDocs fastQuantized = searcher.Search(new KnnQuery(field, q, 5) { Ef = 100 }, 5);
            TopDocs trueQuantized = searcher.Search(new KnnQuery(field, q, 5) { Exact = true }, 5);
            Require(fastQuantized.Hits.Count == 5 && fastQuantized.Hits.Select(h => h.DocId).Intersect(trueQuantized.Hits.Select(h => h.DocId)).Count() >= 4, "quantized recall " + field);
        }
        Require(searcher.Context.Leaves[0].Reader.GetVectors("v")!.Metric == VectorMetric.Cosine, "metric");
    }

    /// <summary>A filter that admits more than 50 times the search breadth, so the segment walks the graph over the accepted nodes instead of scanning them.</summary>
    public static void RunFilteredWalk()
    {
        Schema schema = new SchemaBuilder().Id("id").Integral("n")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = 6, FlatThreshold = 8, HnswM = 8, EfConstruction = 32 })
            .Add(new FieldDefinition("v4", FieldType.Vector) { Dimensions = 6, FlatThreshold = 8, HnswM = 8, EfConstruction = 32, Quantization = VectorQuantization.Scalar4 })
            .Build();
        var rng = new Random(8);
        using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), schema, new IndexWriterOptions { MaxBufferedDocuments = 2000, AutoMerge = false });
        for (int i = 0; i < 1200; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("n", (long)i).Add("v", Vector(rng)).Add("v4", Vector(rng)));
        }

        writer.Commit();
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snapshot);
        Query filter = new RangeQuery("n", null, FieldValue.FromInt64(600)) { IncludeUpper = false };
        float[] q = Vector(rng);
        foreach (string field in new[] { "v", "v4" })
        {
            TopDocs walk = searcher.Search(new KnnQuery(field, q, 5) { Ef = 10, Filter = filter }, 5);
            TopDocs exact = searcher.Search(new KnnQuery(field, q, 5) { Exact = true, Filter = filter }, 5);
            Require(walk.Hits.Count == 5 && walk.Hits.All(h => h.DocId < 600), "filtered walk " + field);
            Require(walk.Hits.Select(h => h.DocId).Intersect(exact.Hits.Select(h => h.DocId)).Count() >= 3, "filtered walk recall " + field);
        }
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
