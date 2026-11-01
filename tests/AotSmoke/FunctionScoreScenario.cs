using OneIndex.Search;

namespace OneIndex.AotSmoke;

/// <summary>Function scores (field value factor, decay, geo decay, a custom function), explain and a filter string, through the happy path and query JSON.</summary>
internal static class FunctionScoreScenario
{
    public static void Run()
    {
        using SearchIndex<Landmark> index = SearchIndex.InMemory<Landmark>();
        index.Upsert(new Landmark { Id = "louvre", City = "Paris", Rating = 5, Location = new GeoPoint(48.8606, 2.3376) });
        index.Upsert(new Landmark { Id = "eiffel", City = "Paris", Rating = 4, Location = new GeoPoint(48.8584, 2.2945) });
        index.Upsert(new Landmark { Id = "big-ben", City = "London", Rating = 4, Location = new GeoPoint(51.5007, -0.1246) });
        index.Upsert(new Landmark { Id = "fiji", Rating = 3, Location = new GeoPoint(-17.7, 179.9) });
        index.Commit();

        SearchResults<Landmark> byRating = index.Search().ScoreBy(b =>
        {
            b.FieldValue(x => x.Rating, modifier: FieldValueModifier.Sqrt);
            b.BoostMode = FunctionBoostMode.Replace;
        }).Explain().Take(4).Execute();
        Require(byRating[0].Id == "louvre" && Math.Abs(byRating[0].Score - Math.Sqrt(5)) < 1e-6, "field value factor");
        Require(byRating.All(h => h.Explanation is { } e && e.Value == h.Score), "explain equals score");

        SearchResults<Landmark> nearest = index.Search().ScoreBy(b =>
        {
            b.Distance(x => x.Location, DecayShape.Gauss, 48.8584, 2.2945, 5_000);
            b.Decay(x => x.Rating, DecayShape.Linear, 5, 10, weight: 0.5f);
            b.BoostMode = FunctionBoostMode.Replace;
        }).Explain().Take(4).Execute();
        Require(nearest[0].Id == "eiffel" && nearest[3].Id == "fiji" && nearest.All(h => h.Explanation!.Value == h.Score), "geo decay");

        SearchResults<Landmark> custom = index.Search().ScoreBy(b =>
        {
            b.Add(new CustomFunction("rating squared", reader =>
            {
                IColumnReader? rating = reader.GetColumn("Rating");
                return doc => rating is not null && rating.HasValue(doc) ? rating.GetInt64(doc) * (double)rating.GetInt64(doc) : null;
            }));
            b.BoostMode = FunctionBoostMode.Replace;
        }).Take(1).Execute();
        Require(custom[0].Id == "louvre" && custom[0].Score == 25f, "custom function");

        SearchResults<Landmark> json = index.Parse("{\"v\":1,\"query\":{\"type\":\"functionScore\",\"functions\":[{\"type\":\"exp\",\"field\":\"Location\",\"origin\":{\"lat\":51.5,\"lng\":-0.12},\"scale\":100000}],\"boostMode\":\"replace\"},\"limit\":1}").Execute();
        Require(json.Count == 1 && json[0].Id == "big-ben", "query json");
        Require(index.Search().Where("Rating >= 4").Where(x => x.City == "Paris").Execute().Select(h => h.Id).Order().SequenceEqual(["eiffel", "louvre"]), "filter string");
        Require(SearchTool.For<Landmark>().Contains("functionScore", StringComparison.Ordinal), "tool schema");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("function score check failed: " + what);
        }
    }
}
