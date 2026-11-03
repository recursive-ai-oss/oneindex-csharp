using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Samples.Migration;

// The OneIndex side of the worked example in docs/migrating-from-lucene-net.md. The page shows the same search written against Lucene.NET's
// public API; this file keeps the OneIndex halves compiling and tested.

/// <summary>The document type of the happy-path half.</summary>
public class Product
{
    [SearchId] public string Id { get; set; } = "";
    [FullText(Boost = 2)] public string Title { get; set; } = "";
    [FullText(Language = "en")] public string Body { get; set; } = "";
    [Filterable, Sortable] public double Price { get; set; }
    [Keyword, Facet] public string Category { get; set; } = "";
}

public static class MigrationExample
{
    private static readonly Product[] Catalog =
    [
        new() { Id = "p1", Title = "Wireless headphones", Body = "Over-ear wireless headphones with noise cancelling.", Price = 89, Category = "audio" },
        new() { Id = "p2", Title = "Studio headphones", Body = "Wired headphones for mixing.", Price = 149, Category = "audio" },
        new() { Id = "p3", Title = "Wireless mouse", Body = "A quiet wireless mouse.", Price = 25, Category = "office" },
        new() { Id = "p4", Title = "Wireless earbuds", Body = "Earbuds, wireless headphones in a small case.", Price = 59, Category = "audio" },
    ];

    /// <summary>The happy path: typed documents, a LINQ-shaped filter and sort, facets and highlights. Returns the ids in order and the first highlight.</summary>
    public static (string[] Ids, string[] Facets, string Highlight) HappyPath()
    {
        using var index = SearchIndex.InMemory<Product>();
        index.Upsert(Catalog);
        index.Commit();

        var search = index.Search("wireless headphones")
                          .Where(p => p.Category == "audio" && p.Price < 100)
                          .OrderBy(p => p.Price)
                          .Facets(p => p.Category)
                          .Take(10);
        var results = search.Execute();
        string[] ids = [.. results.Select(h => h.Id)];
        string[] facets = [.. results.Facets["Category"].Values.Select(v => v.Value + "=" + v.Count)];
        string highlight = results[0].Highlights["Title"][0].Marked();
        return (ids, facets, highlight);
    }

    /// <summary>The low level: a runtime schema, an <see cref="IndexWriter"/>, query objects and a searcher. Returns the ids best first.</summary>
    public static string[] LowLevel()
    {
        Schema schema = new SchemaBuilder().Id("id")
            .Add(new FieldDefinition("title", FieldType.Text) { Analyzer = "en" })
            .Keyword("category")
            .Fractional("price")
            .Build();

        using var writer = IndexWriter.Open(new MemoryStorage(), schema);
        foreach (Product p in Catalog)
        {
            writer.Upsert(new Document().Add("id", p.Id).Add("title", p.Title).Add("category", p.Category).Add("price", p.Price));
        }

        writer.Commit();

        using IndexSnapshot snapshot = writer.OpenSnapshot();
        SearchContext context = SnapshotSearch.CreateContext(snapshot);
        var searcher = new IndexSearcher(context, new QueryEngine());

        // Terms are analyzed by the caller; the "en" analyzer stems "headphones" to "headphon".
        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("title", "wireless"), Occur.Must),
            new BooleanClause(new TermQuery("title", "headphon"), Occur.Must),
            new BooleanClause(new TermQuery("category", "audio"), Occur.Filter),
            new BooleanClause(new RangeQuery("price", null, FieldValue.FromFloat64(100)), Occur.Filter),
        ]);
        TopDocs top = searcher.Search(query, 10);

        // Hits carry document numbers; the identifier comes from the stored source.
        var ids = new List<string>();
        foreach (ScoreDoc hit in top.Hits)
        {
            foreach (LeafContext leaf in context.Leaves)
            {
                if (hit.DocId >= leaf.DocBase && hit.DocId < leaf.DocBase + leaf.Reader.MaxDoc)
                {
                    ids.Add(leaf.Reader.StoredFields!.GetDocument(hit.DocId - leaf.DocBase).Get("id").AsText());
                }
            }
        }

        return [.. ids];
    }
}
