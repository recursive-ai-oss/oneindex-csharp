namespace OneIndex.AotSmoke;

/// <summary>The sample document of the getting-started guide, mapped by the source generator.</summary>
public class Invoice
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Boost = 2)]
    public string Title { get; set; } = string.Empty;

    [FullText(Language = "en")]
    public string Body { get; set; } = string.Empty;

    [Filterable]
    [Sortable]
    public DateTime Modified { get; set; }

    [Keyword]
    [Facet]
    public string[] Tags { get; set; } = [];
}

/// <summary>Runs the happy path the way an application does: open, upsert, commit, search with user text and a filter, highlight.</summary>
internal static class HappyPathScenario
{
    public static void Run()
    {
        DateTime cutoff = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using SearchIndex<Invoice> index = SearchIndex.InMemory<Invoice>();
        index.Upsert(new Invoice { Id = "inv-1", Title = "Overdue notice", Body = "Payment from Acme Corp is overdue.", Modified = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice"] });
        index.Upsert(new Invoice { Id = "inv-2", Title = "Overdue draft", Body = "Acme Corp draft.", Modified = new DateTime(2024, 3, 2, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice", "draft"] });
        index.Upsert(new Invoice { Id = "inv-3", Title = "Old", Body = "Acme Corp overdue in 2022.", Modified = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice"] });
        index.Commit();

        List<SearchHit<Invoice>> hits = index.Search("overdue \"acme corp\" -draft")
            .Where(x => x.Tags.Contains("invoice") && x.Modified > cutoff)
            .Take(10)
            .ToList();
        Require(hits.Count == 1 && hits[0].Id == "inv-1" && hits[0].Document.Title == "Overdue notice" && hits[0].Score > 0, "search");
        Require(hits[0].Highlights["Body"][0].Marked("[", "]") == "Payment from [Acme Corp] is [overdue].", "highlight");
        Require(index.Search("\"unterminated").Count() == 0 && index.Search(null).Count() == 3, "forgiving text");

        SearchResults<Invoice> faceted = index.Search().Facets(x => x.Tags, sortBy: Aggregations.FacetSort.Count).Distinct(x => x.Tags).Aggregate(a => a
            .Terms("tags", x => x.Tags, sub: s => s.Cardinality("days", x => x.Modified))
            .DateHistogram("months", x => x.Modified, Aggregations.DateInterval.Year)
            .Percentiles("when", x => x.Modified, 50)).Take(5).Execute();
        Require(faceted.Facets["Tags"].Values.Select(v => v.Value + "=" + v.Count).SequenceEqual(["invoice=3", "draft=1"]), "facets");
        Require(faceted.Aggregations.Get<Aggregations.BucketsResult>("months").Buckets.Count == 3 && faceted.TotalHits == 1 && faceted.Count == 1, "aggregations and distinct");
        Require(faceted.Aggregations.Get<Aggregations.BucketsResult>("tags").Buckets[0].Aggregations.Get<Aggregations.CardinalityResult>("days").Value == 3, "sub-aggregation");

        bool threw = false;
        try
        {
            _ = index.Search().Where(x => x.Title == "a");
        }
        catch (NotSupportedException)
        {
            threw = true;
        }

        Require(threw, "untranslatable expression");

        string directory = Path.Combine(Path.GetTempPath(), "oneindex-aot-happy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using SearchIndex<Invoice> onDisk = SearchIndex.Open<Invoice>(directory);
            onDisk.Upsert(new Invoice { Id = "d1", Title = "On disk", Body = "kept", Modified = cutoff });
            onDisk.Commit();
            Require(onDisk.Search("disk").Count() == 1, "directory index");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("happy path check failed: " + what);
        }
    }
}
