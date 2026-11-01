using OneIndex.Search;

namespace OneIndex.AotSmoke;

/// <summary>A place with a rating and a geo point, for sorting, search-after paging and geo queries.</summary>
public class Landmark
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Keyword]
    [Sortable]
    public string? City { get; set; }

    [Filterable]
    [Sortable]
    public int Rating { get; set; }

    [Geo]
    public GeoPoint? Location { get; set; }
}

/// <summary>OrderBy, a search-after walk, geo filters and distance sorting, through the happy path, query JSON and a filter string.</summary>
internal static class SortingGeoScenario
{
    public static void Run()
    {
        using SearchIndex<Landmark> index = SearchIndex.InMemory<Landmark>();
        index.Upsert(new Landmark { Id = "louvre", City = "Paris", Rating = 5, Location = new GeoPoint(48.8606, 2.3376) });
        index.Upsert(new Landmark { Id = "eiffel", City = "Paris", Rating = 4, Location = new GeoPoint(48.8584, 2.2945) });
        index.Upsert(new Landmark { Id = "big-ben", City = "London", Rating = 4, Location = new GeoPoint(51.5007, -0.1246) });
        index.Upsert(new Landmark { Id = "fiji", Rating = 3, Location = new GeoPoint(-17.7, 179.9) });
        index.Commit();

        SearchResults<Landmark> sorted = index.Search().OrderByDescending(x => x.Rating).ThenBy(x => x.City).Take(10).Execute();
        Require(string.Join(",", sorted.Select(h => h.Id)) == "louvre,big-ben,eiffel,fiji" && sorted.TotalHitsExact, "order by");

        var walked = new List<string>();
        SortCursor? cursor = null;
        while (true)
        {
            SearchQuery<Landmark> q = index.Search().OrderBy(x => x.City, MissingValuePlacement.First).ThenBy(x => x.Id).Take(3);
            SearchResults<Landmark> page = (cursor is null ? q : q.SearchAfter(cursor.ToToken())).Execute();
            if (page.Count == 0)
            {
                break;
            }

            walked.AddRange(page.Select(h => h.Id));
            cursor = page.NextCursor;
        }

        Require(string.Join(",", walked) == "fiji,big-ben,eiffel,louvre", "search after");

        Require(index.Search().Where(x => Geo.WithinRadius(x.Location!.Value, 48.85, 2.3, 10_000)).OrderBy(x => x.Id).Execute().Select(h => h.Id).SequenceEqual(["eiffel", "louvre"]), "radius");
        Require(index.Search().Where(x => Geo.WithinBoundingBox(x.Location!.Value, 0, -30, 170, -170)).Execute().Select(h => h.Id).SequenceEqual(["fiji"]), "box across the antimeridian");
        SearchResults<Landmark> near = index.Search().OrderByDistance(x => x.Location, 48.85, 2.3).Take(2).Execute();
        Require(near[0].Id == "eiffel" && near[1].Id == "louvre" && near[0].GeoDistance < near[1].GeoDistance, "distance sort");

        Require(index.Parse("{\"v\":1,\"filter\":\"_geoRadius(48.85, 2.3, 10000)\",\"sort\":[\"Rating:desc\",\"_geoPoint(48.85, 2.3)\"]}").Execute().Select(h => h.Id).SequenceEqual(["louvre", "eiffel"]), "query json");
        Require(SearchTool.For<Landmark>().Contains("geoRadius", StringComparison.Ordinal), "tool schema");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("sorting and geo check failed: " + what);
        }
    }
}
