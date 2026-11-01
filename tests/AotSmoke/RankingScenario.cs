namespace OneIndex.AotSmoke;

[RankingRules("words", "proximity", "exactness", "Price:asc")]
[Proximity(0.25f)]
[TypoTolerance(Enabled = false)]
[PrefixSearch(false)]
internal sealed class Shelf
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;

    [Sortable]
    public long Price { get; set; }
}

/// <summary>The proximity bonus of user text and the ranking rules, through the happy path, explain and query JSON.</summary>
internal static class RankingScenario
{
    public static void Run()
    {
        using SearchIndex<Shelf> index = SearchIndex.InMemory<Shelf>();
        index.Upsert(new Shelf { Id = "far", Title = "red cars and many other things and then a big apple", Price = 1 });
        index.Upsert(new Shelf { Id = "near", Title = "big red apple", Price = 3 });
        index.Upsert(new Shelf { Id = "one", Title = "red", Price = 2 });
        index.Commit();

        // Rules declared on the type: more words first, then closeness, then price.
        string[] ruled = [.. index.Search("big red apple").Take(3).Execute().Select(h => h.Id)];
        Require(ruled.SequenceEqual(["near", "far", "one"]), "declared rules");
        string[] loose = [.. index.Search("red apple").RankingRules("words", "Price:asc").Take(3).Execute().Select(h => h.Id)];
        Require(loose.SequenceEqual(["far", "near", "one"]), "words then price");
        SearchResults<Shelf> explained = index.Search("big red apple").Explain().Take(2).Execute();
        Require(explained.All(h => h.Explanation is not null), "explain");

        // Query JSON carries the rules and round-trips.
        SearchResults<Shelf> json = index.Parse("{\"v\":1,\"q\":\"big red apple\",\"rankingRules\":[\"words\",\"proximity\"],\"limit\":2}").Execute();
        Require(json.Count == 2 && json[0].Id == "near", "query json rules");
        Require(SearchTool.For<Shelf>().Contains("rankingRules", StringComparison.Ordinal), "tool schema");

        // Without rules the bonus puts the close pair first.
        string[] byScore = [.. index.Search("big red apple").RankByRelevance().Take(2).Execute().Select(h => h.Id)];
        Require(byScore[0] == "near", "bonus");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("ranking check failed: " + what);
        }
    }
}
