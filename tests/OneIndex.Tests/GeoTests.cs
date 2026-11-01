using System.Globalization;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Tests;

public class Place
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Filterable]
    [Sortable]
    public int Rating { get; set; }

    [Geo]
    public GeoPoint? Location { get; set; }
}

/// <summary>Geo search against a brute-force scan with the same haversine function, plus the haversine against known distances.</summary>
public sealed class GeoTests(ITestOutputHelper output)
{
    private static int Cases => int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_DIFF_CASES"), CultureInfo.InvariantCulture, out int n) ? n : 40;

    [Fact]
    public void HaversineMatchesKnownDistances()
    {
        // One degree of latitude on the mean-radius sphere: R * pi / 180.
        Assert.Equal(GeoMath.EarthRadiusMeters * Math.PI / 180, GeoMath.HaversineMeters(10, 20, 11, 20), 1e-6);
        // Antipodes are half the circumference apart.
        Assert.Equal(GeoMath.EarthRadiusMeters * Math.PI, GeoMath.HaversineMeters(0, 0, 0, 180), 1e-3);
        Assert.Equal(GeoMath.EarthRadiusMeters * Math.PI, GeoMath.HaversineMeters(90, 0, -90, 0), 1e-3);
        // Longitude wraps: 179 and -179 are two degrees apart on the equator.
        Assert.Equal(GeoMath.EarthRadiusMeters * 2 * Math.PI / 180, GeoMath.HaversineMeters(0, 179, 0, -179), 1e-6);
        // Paris to London: about 344 km on a sphere (the published great-circle distance is 343.5 to 344 km).
        double paris = GeoMath.HaversineMeters(48.8566, 2.3522, 51.5074, -0.1278);
        Assert.InRange(paris, 343_000, 345_000);
        Assert.Equal(0, GeoMath.HaversineMeters(12.5, 99.25, 12.5, 99.25));
    }

    [Fact]
    public void PointsOutsideTheRangesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(90.5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(0, -180.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(double.NaN, 0));
    }

    private static GeoPoint RandomPoint(Random r, GeoPoint? near)
    {
        switch (r.Next(10))
        {
            case 0:
                return new GeoPoint(r.Next(2) == 0 ? 90 : -90, r.NextDouble() * 360 - 180); // on a pole
            case 1:
                return new GeoPoint((r.NextDouble() * 4) + (r.Next(2) == 0 ? 86 : -90), r.NextDouble() * 360 - 180); // near a pole
            case 2:
                return new GeoPoint(r.NextDouble() * 180 - 90, r.Next(2) == 0 ? 180 : -180); // on the antimeridian
            case 3:
                return new GeoPoint(r.NextDouble() * 180 - 90, r.Next(2) == 0 ? 180 - (r.NextDouble() * 2) : -180 + (r.NextDouble() * 2));
            case 4 when near is { } c:
            {
                double dLat = (r.NextDouble() - 0.5) * 2;
                double dLng = (r.NextDouble() - 0.5) * 2;
                return new GeoPoint(Math.Clamp(c.Latitude + dLat, -90, 90), Math.Clamp(c.Longitude + dLng, -180, 180));
            }

            default:
                return new GeoPoint(Math.Asin((r.NextDouble() * 2) - 1) * 180 / Math.PI, (r.NextDouble() * 360) - 180);
        }
    }

    private static bool InBox(GeoPoint p, double north, double south, double west, double east)
        => p.Latitude >= south && p.Latitude <= north && (west <= east ? p.Longitude >= west && p.Longitude <= east : p.Longitude >= west || p.Longitude <= east);

    [Fact]
    public void RadiusBoxAndDistanceSortMatchABruteForceScan()
    {
        long radiusQueries = 0;
        long boxQueries = 0;
        long sorts = 0;
        long matches = 0;
        for (int seed = 1; seed <= Cases; seed++)
        {
            var r = new Random(seed * 7919);
            using SearchIndex<Place> index = SearchIndex.InMemory<Place>();
            var places = new Dictionary<string, Place>();
            GeoPoint hub = RandomPoint(r, null);
            int n = r.Next(1, 700);
            for (int i = 0; i < n; i++)
            {
                var p = new Place { Id = "p" + i.ToString("D4", CultureInfo.InvariantCulture), Rating = r.Next(0, 5), Location = r.Next(20) == 0 ? null : RandomPoint(r, hub) };
                places[p.Id] = p;
                index.Upsert(p);
                if (r.Next(150) == 0)
                {
                    index.Commit();
                }
            }

            index.Commit();
            // Updates and deletes after the first commit, so a merge has dead points to drop.
            for (int i = 0; i < n / 5; i++)
            {
                string id = "p" + r.Next(n).ToString("D4", CultureInfo.InvariantCulture);
                if (r.Next(2) == 0)
                {
                    index.Delete(id);
                    places.Remove(id);
                }
                else
                {
                    var p = new Place { Id = id, Rating = r.Next(0, 5), Location = RandomPoint(r, hub) };
                    places[id] = p;
                    index.Upsert(p);
                }
            }

            index.Commit();

            for (int q = 0; q < 12; q++)
            {
                // A centre near the data about half the time; a radius from nothing to the whole Earth, sometimes exactly a point's distance.
                GeoPoint c = r.Next(2) == 0 ? hub : RandomPoint(r, hub);
                double radius = r.Next(6) switch
                {
                    0 => 0,
                    1 => Math.Exp(r.NextDouble() * Math.Log(20_100_000)),
                    2 when places.Values.Where(x => x.Location is not null).ToList() is { Count: > 0 } withLocation
                        => withLocation[r.Next(withLocation.Count)].Location!.Value.DistanceMetersTo(c),
                    _ => Math.Exp(r.NextDouble() * Math.Log(3_000_000)),
                };
                double rad = radius;
                string[] expected = [.. places.Values.Where(x => x.Location is { } l && GeoMath.HaversineMeters(c.Latitude, c.Longitude, l.Latitude, l.Longitude) <= rad).Select(x => x.Id).Order(StringComparer.Ordinal)];
                SearchResults<Place> got = index.Search().Where(x => Geo.WithinRadius(x.Location!.Value, c.Latitude, c.Longitude, rad)).OrderBy(x => x.Id).Take(5000).Execute();
                string[] actual = [.. got.Select(h => h.Id)];
                if (!expected.SequenceEqual(actual))
                {
                    string lost = expected.Except(actual).FirstOrDefault() ?? "-";
                    GeoMath.BoundingBox(c.Latitude, c.Longitude, rad, out double bs, out double bn, out double bw, out double be);
                    Assert.Fail($"seed {seed}: centre {c} radius {rad:R}; box {bs},{bn},{bw},{be}; missing {lost} at {(places.TryGetValue(lost, out Place? pl) ? pl.Location : null)}; extra {string.Join(",", actual.Except(expected))}");
                }

                radiusQueries++;
                matches += expected.Length;

                // The same radius composed with another condition, and negated.
                string[] composed = [.. expected.Where(id => places[id].Rating >= 2)];
                Assert.Equal(composed, index.Search().Where(x => Geo.WithinRadius(x.Location!.Value, c.Latitude, c.Longitude, rad) && x.Rating >= 2).OrderBy(x => x.Id).Take(5000).Execute().Select(h => h.Id).ToArray());
                string[] outside = [.. places.Keys.Except(expected).Order(StringComparer.Ordinal)];
                Assert.Equal(outside, index.Search().Where(x => !Geo.WithinRadius(x.Location!.Value, c.Latitude, c.Longitude, rad)).OrderBy(x => x.Id).Take(5000).Execute().Select(h => h.Id).ToArray());

                double south = (r.NextDouble() * 180) - 90;
                double north = Math.Min(90, south + (r.NextDouble() * (r.Next(3) == 0 ? 180 : 20)));
                double west = r.Next(5) == 0 ? -180 : (r.NextDouble() * 360) - 180;
                double east = r.Next(5) == 0 ? 180 : r.Next(2) == 0 ? Math.Min(180, west + (r.NextDouble() * 60)) : (r.NextDouble() * 360) - 180; // sometimes west > east: across the antimeridian
                double s = south;
                double nth = north;
                double w = west;
                double e = east;
                string[] inBox = [.. places.Values.Where(x => x.Location is { } l && InBox(l, nth, s, w, e)).Select(x => x.Id).Order(StringComparer.Ordinal)];
                Assert.Equal(inBox, index.Search().Where(x => Geo.WithinBoundingBox(x.Location!.Value, nth, s, w, e)).OrderBy(x => x.Id).Take(5000).Execute().Select(h => h.Id).ToArray());
                boxQueries++;
                matches += inBox.Length;
            }

            for (int q = 0; q < 3; q++)
            {
                GeoPoint c = RandomPoint(r, hub);
                bool far = r.Next(2) == 0;
                var model = places.Values.Where(x => x.Location is not null)
                    .Select(x => (x.Id, D: GeoMath.HaversineMeters(c.Latitude, c.Longitude, x.Location!.Value.Latitude, x.Location.Value.Longitude)))
                    .OrderBy(x => far ? -x.D : x.D).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
                var missing = places.Values.Where(x => x.Location is null).Select(x => x.Id).Order(StringComparer.Ordinal).ToList();
                SearchResults<Place> got = index.Search().OrderByDistance(x => x.Location, c.Latitude, c.Longitude, far ? SortDirection.Descending : SortDirection.Ascending).Take(5000).Execute();
                Assert.Equal([.. model.Select(x => x.Id), .. missing], got.Select(h => h.Id).ToArray());
                for (int i = 0; i < model.Count; i++)
                {
                    Assert.Equal(model[i].D, got[i].GeoDistance);
                }

                sorts++;
            }
        }

        output.WriteLine($"{Cases} corpora: {radiusQueries} radius queries (each also composed and negated), {boxQueries} bounding boxes, {sorts} distance sorts, {matches} matches checked");
    }

    [Fact]
    public void GeoQueriesRejectBadInputWithTheFieldName()
    {
        using SearchIndex<Place> index = SearchIndex.InMemory<Place>();
        index.Upsert(new Place { Id = "a", Location = new GeoPoint(1, 2) });
        index.Commit();
        Assert.Throws<QueryException>(() => index.SearchBy(new GeoRadiusQuery("Location", 95, 0, 10)).Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(new GeoRadiusQuery("Location", 0, 0, -1)).Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(new GeoRadiusQuery("Rating", 0, 0, 1)).Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(new GeoBoundingBoxQuery("Location", 10, 20, 0, 1)).Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(new GeoBoundingBoxQuery("Nope", 20, 10, 0, 1)).Execute());
        Assert.Throws<ArgumentException>(() => index.Search().OrderBy(x => x.Location));
        Assert.Throws<ArgumentException>(() => index.Search().OrderByDistance(x => x.Rating, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search().OrderByDistance(x => x.Location, 100, 0));
        Assert.Throws<Linq.UnsupportedExpressionException>(() => index.Search().Where(x => Geo.WithinRadius(x.Location!.Value, 0, 0, x.Rating)));
        Assert.Throws<NotSupportedException>(() => Geo.WithinRadius(default, 0, 0, 1));
    }

    [Fact]
    public void AGeoPointSurvivesTheRoundTripAndAMerge()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            using (SearchIndex<Place> index = SearchIndex.Open<Place>(dir))
            {
                for (int i = 0; i < 6; i++)
                {
                    index.Upsert(new Place { Id = "p" + i, Rating = i, Location = new GeoPoint(i * 10, -i * 20) });
                    index.Commit();
                }
            }

            using SearchIndex<Place> reopened = SearchIndex.Open<Place>(dir);
            SearchResults<Place> r = reopened.Search().Where(x => Geo.WithinRadius(x.Location!.Value, 10, -20, 1000)).Execute();
            Assert.Equal("p1", Assert.Single(r).Id);
            Assert.Equal(new GeoPoint(10, -20), r[0].Document.Location);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheGeoIndexGivesTheSameAnswersBeforeAndAfterAForcedMerge()
    {
        Schema schema = new SchemaBuilder().Id("id").GeoPoint("where").Build();
        var storage = new Storage.MemoryStorage();
        using IndexWriter w = IndexWriter.Open(storage, schema, new IndexWriterOptions { MaxBufferedDocuments = 40, AutoMerge = false });
        var r = new Random(5);
        var points = new Dictionary<string, GeoPoint>();
        for (int i = 0; i < 400; i++)
        {
            string id = "d" + i.ToString("D4", CultureInfo.InvariantCulture);
            var p = new GeoPoint(Math.Asin((r.NextDouble() * 2) - 1) * 180 / Math.PI, (r.NextDouble() * 360) - 180);
            points[id] = p;
            w.Upsert(new Document().Add("id", id).Add("where", FieldValue.FromGeoPoint(p.Latitude, p.Longitude)));
        }

        w.Commit();
        for (int i = 0; i < 400; i += 3)
        {
            string id = "d" + i.ToString("D4", CultureInfo.InvariantCulture);
            w.DeleteById(id);
            points.Remove(id);
        }

        w.Commit();
        Assert.True(w.SegmentCount > 1);
        GeoRadiusQuery near = new("where", 20, 30, 5_000_000);
        GeoBoundingBoxQuery box = new("where", 60, -10, 170, -170);
        int before = Count(w, near);
        int boxBefore = Count(w, box);
        Assert.Equal(points.Values.Count(p => GeoMath.HaversineMeters(20, 30, p.Latitude, p.Longitude) <= 5_000_000), before);
        Assert.Equal(points.Values.Count(p => p.Latitude is >= -10 and <= 60 && (p.Longitude >= 170 || p.Longitude <= -170)), boxBefore);
        w.ForceMerge(1);
        w.Commit();
        Assert.Equal(1, w.SegmentCount);
        Assert.Equal(before, Count(w, near));
        Assert.Equal(boxBefore, Count(w, box));

        static int Count(IndexWriter writer, Search.Query query)
        {
            using IndexSnapshot snap = writer.OpenSnapshot();
            var counter = new CountCollector();
            SnapshotSearch.CreateSearcher(snap).Search(query, counter);
            return (int)counter.Count;
        }
    }
}
