using OneIndex.Aggregations;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Tests;

public class Product
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;

    [Keyword]
    [Facet]
    public string Category { get; set; } = string.Empty;

    [Keyword]
    [Facet]
    public string[] Tags { get; set; } = [];

    [Filterable]
    [Facet]
    public int Price { get; set; }

    [Filterable]
    public double Rating { get; set; }

    [Filterable]
    public DateTime Added { get; set; }

    [Filterable]
    public bool InStock { get; set; }
}

[Collection(AllocationMeasurement.Name)]
public sealed class FacetTests(ITestOutputHelper output)
{
    private static SearchIndex<Product> Catalog()
    {
        SearchIndex<Product> index = SearchIndex.InMemory<Product>();
        DateTime d(int year, int month) => new(year, month, 5, 0, 0, 0, DateTimeKind.Utc);
        index.Upsert(new Product { Id = "p1", Title = "red running shoe", Category = "shoes", Tags = ["sport", "red"], Price = 80, Rating = 4.5, Added = d(2024, 1), InStock = true });
        index.Upsert(new Product { Id = "p2", Title = "blue running shoe", Category = "shoes", Tags = ["sport", "blue"], Price = 95, Rating = 4.0, Added = d(2024, 2), InStock = true });
        index.Upsert(new Product { Id = "p3", Title = "red wool sock", Category = "socks", Tags = ["red"], Price = 12, Rating = 3.5, Added = d(2024, 2), InStock = false });
        index.Upsert(new Product { Id = "p4", Title = "running sock", Category = "socks", Tags = ["sport"], Price = 12, Rating = 5, Added = d(2023, 12), InStock = true });
        index.Upsert(new Product { Id = "p5", Title = "leather boot", Category = "boots", Tags = [], Price = 150, Rating = 4.25, Added = d(2024, 3), InStock = true });
        index.Commit();
        return index;
    }

    [Fact]
    public void FacetsCountAllMatchesNotJustThePageAndMakeTheTotalExact()
    {
        using SearchIndex<Product> index = Catalog();
        SearchResults<Product> r = index.Search("running").Facets(x => x.Category).Facets(x => x.Tags, sortBy: FacetSort.Count).Facets(x => x.Price).Take(1).Execute();
        Assert.Single(r);
        Assert.Equal(3, r.TotalHits); // p1, p2, p4 -- exact without CountExactly(), because facets visit every match
        Assert.True(r.TotalHitsExact);
        Assert.Equal(["shoes=2", "socks=1"], r.Facets["Category"].Values.Select(v => $"{v.Value}={v.Count}"));
        Assert.Equal(["sport=3", "blue=1", "red=1"], r.Facets["Tags"].Values.Select(v => $"{v.Value}={v.Count}"));
        FacetResult price = r.Facets["Price"];
        Assert.Equal(["12=1", "80=1", "95=1"], price.Values.Select(v => $"{v.Value}={v.Count}"));
        Assert.Equal(new FacetStats(12, 95), price.Stats);
        Assert.Null(r.Facets["Category"].Stats);
    }

    [Fact]
    public void FacetsFollowFiltersAndHonourMaxValues()
    {
        using SearchIndex<Product> index = Catalog();
        SearchResults<Product> r = index.Search().Where(x => x.InStock).Facets(x => x.Category, maxValues: 2, sortBy: FacetSort.Count).Take(0).Execute();
        Assert.Equal(4, r.TotalHits);
        Assert.Empty(r);
        FacetResult f = r.Facets["Category"];
        Assert.Equal(["shoes=2", "boots=1"], f.Values.Select(v => $"{v.Value}={v.Count}"));
        Assert.Equal(3, f.DistinctValues);
        Assert.Throws<ArgumentException>(() => index.Search().Facets(x => x.Title)); // full text keeps no column
        Assert.Throws<ArgumentException>(() => index.Search().Facets(x => x.Category.Length));
        Assert.Throws<ArgumentException>(() => index.Search().Facets(x => x.Category).Facets(x => x.Category));
        Assert.Throws<ArgumentException>(() => index.Search().Facets(x => x.Category, maxValues: 0));
    }

    [Fact]
    public void DistinctKeepsTheBestHitPerValueAndPagesConsistently()
    {
        using SearchIndex<Product> index = Catalog();
        // Every score is equal for a match-all search, so the best document of a category is its lowest-numbered one.
        SearchResults<Product> d = index.Search().Distinct(x => x.Category).Take(10).Execute();
        Assert.Equal(["p1", "p3", "p5"], d.Select(h => h.Id));
        Assert.Equal(3, d.TotalHits);
        Assert.True(d.TotalHitsExact);
        Assert.Equal(["p3"], index.Search().Distinct(x => x.Category).Skip(1).Take(1).Execute().Select(h => h.Id));
        Assert.Equal(["p5"], index.Search().Distinct(x => x.Category).Skip(2).Take(5).Execute().Select(h => h.Id));

        // With scores: each category is represented by its best-ranked hit.
        List<SearchHit<Product>> ranked = [.. index.Search("running").Take(10).Execute()];
        SearchResults<Product> byCategory = index.Search("running").Distinct(x => x.Category).Take(10).Execute();
        Assert.Equal(ranked.Select(h => h.Document.Category).Distinct().Count(), byCategory.Count);
        Assert.Equal(byCategory.Count, byCategory.TotalHits);
        foreach (SearchHit<Product> h in byCategory)
        {
            Assert.Equal(ranked.First(x => x.Document.Category == h.Document.Category).Id, h.Id);
        }

        Assert.Throws<ArgumentException>(() => index.Search().Distinct(x => x.Title));
    }

    [Fact]
    public void AggregateComposesBucketsAndMetricsWithSubAggregations()
    {
        using SearchIndex<Product> index = Catalog();
        SearchResults<Product> r = index.Search().Take(0).Aggregate(a => a
            .Terms("by_category", x => x.Category, size: 5, sub: s => s.Stats("price", x => x.Price).Cardinality("tags", x => x.Tags))
            .Range("price_band", x => x.Price, new AggregationRange(null, 50), new AggregationRange(50, null, "50+"))
            .Histogram("rating", x => x.Rating, 1)
            .DateHistogram("per_month", x => x.Added, DateInterval.Month)
            .Percentiles("p", x => x.Price, 50, 100)).Execute();
        BucketsResult cat = r.Aggregations.Get<BucketsResult>("by_category");
        Assert.Equal(["shoes=2", "socks=2", "boots=1"], cat.Buckets.Select(b => $"{b.Key}={b.DocCount}"));
        StatsResult shoes = cat.Buckets[0].Aggregations.Get<StatsResult>("price");
        Assert.Equal((2, 80.0, 95.0, 175.0, 87.5), (shoes.Count, shoes.Min, shoes.Max, shoes.Sum, shoes.Avg));
        Assert.Equal(3, cat.Buckets[0].Aggregations.Get<CardinalityResult>("tags").Value);
        Assert.Equal(["*-50=2", "50+=3"], r.Aggregations.Get<BucketsResult>("price_band").Buckets.Select(b => $"{b.Key}={b.DocCount}"));
        Assert.Equal(["3=1", "4=3", "5=1"], r.Aggregations.Get<BucketsResult>("rating").Buckets.Select(b => $"{b.Key}={b.DocCount}"));
        Assert.Equal(["2023-12-01T00:00:00.000Z=1", "2024-01-01T00:00:00.000Z=1", "2024-02-01T00:00:00.000Z=2", "2024-03-01T00:00:00.000Z=1"],
            r.Aggregations.Get<BucketsResult>("per_month").Buckets.Select(b => $"{b.Key}={b.DocCount}"));
        Assert.Equal(150, r.Aggregations.Get<PercentilesResult>("p").Values[1].Value);
        Assert.Throws<ArgumentException>(() => index.Search().Aggregate(a => a.Stats("s", x => x.Category)));
        Assert.Throws<ArgumentException>(() => index.Search().Aggregate(a => a.Stats("s", x => x.Price).Stats("s", x => x.Price)));
    }

    // ---- facet value search ----

    private static List<FacetValue> Values(params (string Value, long Count)[] items)
        => [.. items.Select(i => new FacetValue(new BucketKey(i.Value, 0, double.NaN), i.Count))];

    [Fact]
    public void FacetValueSearchMatchesPrefixesWordsCaseAccentsAndTypos()
    {
        List<FacetValue> values = Values(("Café", 5), ("cafe au lait", 3), ("Straße", 2), ("STRASSE", 1), ("ﬁsh", 4), ("Zürich", 6), ("résumé", 2), ("New York", 7), ("Ångström", 1), ("naïve", 1), ("ΣΊΣΥΦΟΣ", 1), ("日本語", 2), ("Résumé Writing", 1));
        string[] Find(string q, int limit = 100) => [.. FacetValueSearch.Match(values, q, limit, FacetSort.Count).Select(v => v.Value)];

        Assert.Equal(["Café", "cafe au lait"], Find("cafe")); // accents and case folded; count order
        Assert.Equal(["Café", "cafe au lait"], Find("CAF"));
        Assert.Equal(["cafe au lait"], Find("au lai"));
        Assert.Equal(["Straße", "STRASSE"], Find("strasse")); // ß folds to ss
        Assert.Equal(["Straße", "STRASSE"], Find("straße"));
        Assert.Equal(["ﬁsh"], Find("fish")); // NFKC expands the ligature
        Assert.Equal(["New York"], Find("york")); // a later word
        Assert.Equal(["New York"], Find("new y"));
        Assert.Empty(Find("york new z"));
        Assert.Equal(["Ångström"], Find("angstrom"));
        Assert.Equal(["日本語"], Find("日本"));
        Assert.Equal(["ΣΊΣΥΦΟΣ"], Find("σισυφος"));
        Assert.Equal(["résumé", "Résumé Writing"], Find("RESUME"));

        // Typo budget: none up to 4 letters, one from 5, two from 9.
        Assert.Empty(Find("cfe")); // 3 letters: no typos
        Assert.Equal(["Zürich"], Find("zurcih")); // one swap
        Assert.Equal(["Zürich"], Find("zuerich")); // one insertion
        Assert.Equal(["résumé", "Résumé Writing"], Find("resumee"));
        Assert.Empty(Find("zxxich")); // two edits, 6 letters
        Assert.Equal(["New York"], Find("newyorkk".Insert(3, " "))); // "new yorkk": word "yorkk" (5) is one edit from "york"

        // Empty input matches everything, most documents first; the limit applies.
        Assert.Equal(["New York", "Zürich", "Café"], Find(string.Empty, 3));
        Assert.Equal(13, Find(null!).Length);
        // The default order is by value, as Meilisearch's facet search; a limit keeps the first values in that order.
        Assert.Equal(["Café", "cafe au lait"], FacetValueSearch.Match(values, "cafe").Select(v => v.Value));
        Assert.Equal(["STRASSE", "Straße"], FacetValueSearch.Match(values, "strasse").Select(v => v.Value));
        Assert.Equal(["Café", "New York", "Résumé Writing"], FacetValueSearch.Match(values, null, 3).Select(v => v.Value));
        Assert.Equal("cafe", FacetValueSearch.Normalize("CAFÉ"));
    }

    [Fact]
    public void FacetValueSearchCountsOverTheQueryMatches()
    {
        using AggCorpus corpus = AggCorpus.Build(5, documents: 120, merge: false);
        IReadOnlyList<FacetValue> found = FacetValueSearch.Search(corpus.Searcher, new TermQuery("body", "alpha"), "tags", "b");
        var expected = corpus.Live.Where(x => x.Body.Contains("alpha")).SelectMany(x => x.Tags.Distinct()).Where(t => t == "b").Count();
        Assert.Single(found);
        Assert.Equal("b", found[0].Value);
        Assert.Equal(expected, found[0].Count);
        // "ß" (folds to "ss") and "ss" are different keywords that both match "ss".
        IReadOnlyList<FacetValue> ss = FacetValueSearch.Search(corpus.Searcher, new MatchAllQuery(), "tags", "ss");
        Assert.Equal(["ss", "ß"], ss.Select(v => v.Value).Order(StringComparer.Ordinal));
    }

    // ---- hot path ----

    [Fact]
    public void CollectingDocumentsAllocatesNothing()
    {
        using AggCorpus corpus = AggCorpus.Build(3, documents: 3000, merge: true, buffer: 100_000);
        LeafContext leaf = corpus.Searcher.Context.Leaves[0];
        int maxDoc = leaf.Reader.MaxDoc;
        var collectors = new (string Name, Collector Collector)[]
        {
            ("facets", new FacetCollector(AggCorpus.Schema, [new FacetRequest("cat"), new FacetRequest("tags"), new FacetRequest("n"), new FacetRequest("flag")])),
            ("terms with sub-aggregations", new AggregationCollector(AggCorpus.Schema, [new TermsAggregation("t", "tags") { SubAggregations = [new StatsAggregation("s", "price"), new TermsAggregation("c", "cat")] }])),
            ("numeric terms", new AggregationCollector(AggCorpus.Schema, [new TermsAggregation("t", "ns"), new TermsAggregation("p", "price")])),
            ("range and histograms", new AggregationCollector(AggCorpus.Schema, [new RangeAggregation("r", "n", [new AggregationRange(0, 10), new AggregationRange(5, null)]), new HistogramAggregation("h", "n", 4), new DateHistogramAggregation("d", "when") { CalendarInterval = DateInterval.Month }])),
            ("stats, cardinality, percentiles", new AggregationCollector(AggCorpus.Schema, [new StatsAggregation("s", "price"), new CardinalityAggregation("c", "tags"), new CardinalityAggregation("n", "ns"), new PercentilesAggregation("p", "n")])),
        };
        foreach ((string name, Collector collector) in collectors)
        {
            LeafCollector lc = collector.GetLeafCollector(leaf);
            TestHelpers.AssertNoAllocation($"collecting {maxDoc} documents for {name}", () =>
            {
                for (int doc = 0; doc < maxDoc; doc++)
                {
                    lc.Collect(doc);
                }
            }, windows: 8);
        }

        // Per query, with results: report.
        var q = new AggregationCollector(AggCorpus.Schema, [new TermsAggregation("t", "tags"), new StatsAggregation("s", "n")]);
        corpus.Searcher.Search(new MatchAllQuery(), q);
        _ = q.GetResults();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var fc = new FacetCollector(AggCorpus.Schema, [new FacetRequest("cat"), new FacetRequest("tags")]);
        corpus.Searcher.Search(new MatchAllQuery(), fc);
        _ = fc.GetResults();
        output.WriteLine($"a two-facet query over {maxDoc} documents allocated {GC.GetAllocatedBytesForCurrentThread() - before} bytes in total");
    }

    [Fact]
    public void MultiCollectorSharesOnePassAndRefusesToMixSkipping()
    {
        using AggCorpus corpus = AggCorpus.Build(11, documents: 80, merge: false);
        var top = new TopScoreCollector(5, countExactly: true);
        var count = new CountCollector();
        corpus.Searcher.Search(new TermQuery("body", "beta"), new MultiCollector([top, count]));
        Assert.Equal(count.Count, top.GetTopDocs().TotalHits);
        Assert.Equal(corpus.Live.Count(x => x.Body.Contains("beta")), count.Count);
        Assert.Throws<ArgumentException>(() => new MultiCollector([new TopScoreCollector(5, countExactly: false), count]));
        Assert.Throws<ArgumentException>(() => new MultiCollector([]));
    }
}
