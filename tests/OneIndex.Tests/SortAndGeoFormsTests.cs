using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex.Tests;

public sealed class SortAndGeoFormsTests
{
    private static SearchIndex<Place> Sample()
    {
        SearchIndex<Place> index = SearchIndex.InMemory<Place>();
        index.Upsert(new Place { Id = "louvre", Rating = 5, Location = new GeoPoint(48.8606, 2.3376) });
        index.Upsert(new Place { Id = "eiffel", Rating = 4, Location = new GeoPoint(48.8584, 2.2945) });
        index.Upsert(new Place { Id = "big-ben", Rating = 4, Location = new GeoPoint(51.5007, -0.1246) });
        index.Upsert(new Place { Id = "fiji", Rating = 3, Location = new GeoPoint(-17.7, 179.9) });
        index.Upsert(new Place { Id = "nowhere", Rating = 1 });
        index.Commit();
        return index;
    }

    [Fact]
    public void FilterStringsReadAndPrintGeoConditions()
    {
        using SearchIndex<Place> index = Sample();
        Search.Query q = FilterString.Parse("_geoRadius(48.85, 2.3, 5000) AND Rating > 3", index.Schema);
        Assert.Equal(new BooleanQuery([new BooleanClause(new GeoRadiusQuery("Location", 48.85, 2.3, 5000), Occur.Filter), new BooleanClause(new RangeQuery("Rating", FieldValue.FromInt64(3), null) { IncludeLower = false }, Occur.Filter)]), q);
        Assert.Equal(q, FilterString.Parse(FilterString.Print(q, index.Schema), index.Schema));

        Search.Query box = FilterString.Parse("_geoBoundingBox(Location, [0, -170], [-20, 170])", index.Schema);
        Assert.Equal(new GeoBoundingBoxQuery("Location", 0, -20, 170, -170), box);
        Assert.Equal(box, FilterString.Parse(FilterString.Print(box, index.Schema), index.Schema));
        Assert.Equal(["eiffel", "louvre"], index.SearchBy(q).OrderBy(x => x.Id).Execute().Select(h => h.Id).ToArray());
        Assert.Equal(["fiji"], index.SearchBy(box).Execute().Select(h => h.Id).ToArray());

        QueryParseException bad = Assert.Throws<QueryParseException>(() => FilterString.Parse("_geoRadius(95, 2.3, 5000)", index.Schema));
        Assert.Equal(11, bad.Offset); // points at the latitude
        Assert.Throws<QueryParseException>(() => FilterString.Parse("_geoRadius(1, 2)", index.Schema));
        Assert.Throws<QueryParseException>(() => FilterString.Parse("_geoRadius(Rating, 1, 2, 3)", index.Schema));
        Assert.Throws<QueryParseException>(() => FilterString.Parse("_geoBoundingBox([1, 2], [3, 4])", index.Schema)); // bottom left above top right
        Assert.Throws<QueryParseException>(() => FilterString.Parse("_geoRadius(1, 2, -3)", index.Schema));
    }

    [Fact]
    public void QueryJsonCarriesGeoNodesSortAndCursorsAndRoundTrips()
    {
        using SearchIndex<Place> index = Sample();
        const string json = """
            {"v":1,"filter":{"type":"geoRadius","field":"Location","lat":48.85,"lng":2.3,"meters":600000},
             "sort":["Rating:desc","_geoPoint(48.85, 2.3):asc"],"limit":2}
            """;
        QueryRequest request = QueryJson.Read(json, index.Schema);
        Assert.Equal(2, request.Sort!.Keys.Count);
        string canonical = QueryJson.Write(request);
        Assert.Equal(request, QueryJson.Read(canonical, index.Schema));
        Assert.Equal(canonical, QueryJson.Write(QueryJson.Read(canonical, index.Schema)));

        SearchResults<Place> page1 = index.Parse(json).Execute();
        Assert.Equal(["louvre", "eiffel"], page1.Select(h => h.Id).ToArray());
        Assert.Equal(3, page1.TotalHits); // the radius holds louvre, eiffel and big-ben
        Assert.True(page1[0].GeoDistance < page1[1].GeoDistance + 10_000);

        string next = QueryJson.Write(request with { SearchAfter = page1.NextCursor!.ToToken() });
        SearchResults<Place> page2 = index.Parse(next).Execute();
        Assert.Equal(["big-ben"], page2.Select(h => h.Id).ToArray());

        QueryParseException e = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"sort":["Nope"]}""", index.Schema));
        Assert.Equal("$.sort[0]", e.Path);
        Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"sort":["Location"]}""", index.Schema));
        Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"searchAfter":"AAAA"}""", index.Schema));
        Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"sort":["Rating"],"searchAfter":"AAAA"}""", index.Schema));
        Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"query":{"type":"geoRadius","field":"Rating","lat":1,"lng":1,"meters":1}}""", index.Schema));
        Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"query":{"type":"geoRadius","field":"Location","lat":91,"lng":1,"meters":1}}""", index.Schema));
        Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"query":{"type":"geoRadius","field":"Location","lat":1,"lng":1}}""", index.Schema));
    }

    [Fact]
    public void CursorsFitOnlyTheirOwnOrderAndTokensNeverThrow()
    {
        using SearchIndex<Place> index = Sample();
        SearchResults<Place> page = index.Search().OrderBy(x => x.Rating).Take(2).Execute();
        SortCursor cursor = page.NextCursor!;
        Assert.Throws<ArgumentException>(() => index.Search().OrderByDescending(x => x.Rating).SearchAfter(cursor));
        Assert.Throws<InvalidOperationException>(() => index.Search().SearchAfter(cursor));
        Assert.Throws<InvalidOperationException>(() => index.Search().OrderBy(x => x.Rating).SearchAfter(cursor).Skip(1).Take(1).Execute());
        Assert.Throws<ArgumentException>(() => index.Search().OrderBy(x => x.Rating).SearchAfter("not a cursor"));
        Assert.Throws<InvalidOperationException>(() => index.Search().ThenBy(x => x.Rating));
        Assert.Throws<NotSupportedException>(() => index.Search().OrderBy(x => x.Rating).Distinct(x => x.Rating).Execute());

        foreach (string junk in new[] { string.Empty, "A", "AQ", "AQAAAAAA", "////", new string('A', 100_000), "AQAAAAAB_____w" })
        {
            Assert.False(SortCursor.TryParse(junk, out _));
        }

        // Tokens are deterministic text.
        Assert.Equal(cursor.ToToken(), page.NextCursor!.ToToken());
        Assert.True(SortCursor.TryParse(cursor.ToToken(), out SortCursor? back));
        Assert.Equal(cursor.ToToken(), back!.ToToken());
    }

    [Fact]
    public void ARelevanceKeyScoresAndTiesFallToTheNextKey()
    {
        using SearchIndex<Venue> index = SearchIndex.InMemory<Venue>();
        index.Upsert(new Venue { Id = "a", Title = "red red red", Rating = 1 });
        index.Upsert(new Venue { Id = "b", Title = "red", Rating = 1 });
        index.Upsert(new Venue { Id = "c", Title = "red", Rating = 2 });
        index.Commit();
        Assert.Equal(["c", "a", "b"], index.Search("red").OrderByDescending(x => x.Rating).ThenByRelevance().Execute().Select(h => h.Id).ToArray());
        SearchResults<Venue> byScore = index.Search("red").OrderByRelevance().Execute();
        Assert.Equal("a", byScore[0].Id);
        Assert.True(byScore[0].Score > byScore[1].Score);
        Assert.Equal(0, index.Search("red").OrderBy(x => x.Rating).Execute()[0].Score); // no score key: scores are not computed
    }

    [Fact]
    public void TheLowLevelCollectorSortsByColumnWithoutAnIdentifierField()
    {
        Schema schema = new SchemaBuilder().Keyword("tag").Integral("n").Build();
        using IndexWriter w = IndexWriter.Open(new Storage.MemoryStorage(), schema, new IndexWriterOptions { MaxBufferedDocuments = 3 });
        for (int i = 0; i < 10; i++)
        {
            w.Upsert(new Document().Add("tag", "t" + (i % 3)).Add("n", i % 4));
        }

        w.Commit();
        using IndexSnapshot snap = w.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
        SortCriterion[] keys = [new FieldSort("n") { Direction = SortDirection.Descending }, new FieldSort("tag")];
        var collector = new TopFieldCollector(keys, 10);
        searcher.Search(new MatchAllQuery(), collector);
        TopFieldDocs top = collector.GetTopDocs();
        Assert.Equal(10, top.TotalHits);
        long[] ns = [.. top.Hits.Select(h => h.Values[0].Number)];
        Assert.Equal(ns.OrderByDescending(x => x), ns);
        // Without an identifier field, ties break by global document id and a cursor carries it.
        Assert.Null(top.Hits[0].Id);
        var after = new TopFieldCollector(keys, 10, SortCursor.After(keys, top.Hits[3]));
        searcher.Search(new MatchAllQuery(), after);
        Assert.Equal(top.Hits.Skip(4).Select(h => h.DocId), after.GetTopDocs().Hits.Select(h => h.DocId));
        Assert.Throws<QueryException>(() =>
        {
            var bad = new TopFieldCollector([new GeoDistanceSort("n", 0, 0)], 3);
            searcher.Search(new MatchAllQuery(), bad);
        });
    }

    [Fact]
    public void TheToolSchemaAcceptsGeoNodesAndSortAsTheReaderDoes()
    {
        string schemaText = SearchTool.SchemaFor<Place>();
        Assert.Contains("geoRadius", schemaText, StringComparison.Ordinal);
        Assert.Contains("searchAfter", schemaText, StringComparison.Ordinal);
        string? python = Environment.GetEnvironmentVariable("ONEINDEX_JSONSCHEMA_PYTHON");
        if (string.IsNullOrEmpty(python))
        {
            return; // the validator is an optional black-box oracle
        }

        using SearchIndex<Place> index = Sample();
        string[] documents =
        [
            """{"v":1,"query":{"type":"geoRadius","field":"Location","lat":48.8,"lng":2.3,"meters":1000},"sort":["Rating:desc","_geoPoint(48.8, 2.3):asc"]}""",
            """{"v":1,"filter":{"type":"geoBoundingBox","field":"Location","north":10,"south":-10,"west":170,"east":-170},"sort":["Id:asc:first"]}""",
            """{"v":1,"query":{"type":"geoRadius","field":"Location","lat":95,"lng":2.3,"meters":1000}}""",
            """{"v":1,"query":{"type":"geoRadius","field":"Rating","lat":1,"lng":2.3,"meters":1000}}""",
            """{"v":1,"query":{"type":"geoRadius","field":"Location","lat":1,"lng":2.3}}""",
            """{"v":1,"sort":[]}""",
            """{"v":1,"searchAfter":3}""",
        ];
        bool[] valid = [true, true, false, false, false, false, false];
        string dir = Path.Combine(Path.GetTempPath(), "oneindex-jsonschema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "schema.json"), schemaText);
            File.WriteAllLines(Path.Combine(dir, "docs.jsonl"), documents);
            var start = new System.Diagnostics.ProcessStartInfo(python, $"\"{Path.Combine(AppContext.BaseDirectory, "jsonschema_check.py")}\" \"{dir}/schema.json\" \"{dir}/docs.jsonl\"") { RedirectStandardOutput = true, RedirectStandardError = true };
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd();
            string errors = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, errors);
            string[] verdicts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < documents.Length; i++)
            {
                Assert.Equal(valid[i], verdicts[i].StartsWith("valid", StringComparison.Ordinal));
                Assert.Equal(valid[i], index.TryParse(documents[i], out _, out _));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
