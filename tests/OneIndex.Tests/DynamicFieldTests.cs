using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Storage;

namespace OneIndex.Tests;

public class Activity
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;

    [Dynamic]
    public JsonElement Meta { get; set; }
}

public class DynamicFieldTests
{
    private static readonly string[] Names = ["acme", "bob", "carol", "dave corp", "e.f"];
    private static readonly string[] Colors = ["red", "green", "blue"];

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static Activity Make(string id, string json) => new() { Id = id, Title = "t " + id, Meta = Json(json) };

    private static string Ids(SearchIndex<Activity> index, Expression<Func<Activity, bool>> predicate)
        => string.Join(",", index.Search().Where(predicate).Take(1000).ToList().Select(h => h.Id).Order(StringComparer.Ordinal));

    [Fact]
    public void FiltersByPathWithTheDocumentedAccessor()
    {
        using SearchIndex<Activity> index = SearchIndex.InMemory<Activity>();
        index.Upsert(Make("1", """{"customer":{"name":"acme","age":41},"tags":["red","blue"],"score":2.5,"active":true,"when":"2024-03-01"}"""));
        index.Upsert(Make("2", """{"customer":{"name":"bob","age":17},"tags":["green"],"score":[1,9],"active":false,"when":"2022-01-01T10:00:00Z"}"""));
        index.Upsert(Make("3", """{"customer":{"name":"acme"},"misc":null}"""));
        index.Upsert(Make("4", """{"customer":{"name":"carol","age":"unknown"}}"""));
        index.Commit();

        Assert.Equal("1,3", Ids(index, x => x.Meta.Path("customer.name") == "acme"));
        Assert.Equal("1", Ids(index, x => x.Meta.Path("customer.age") >= 18));
        Assert.Equal("1,2", Ids(index, x => x.Meta.Path("tags") == "red" || x.Meta.Path("tags") == "green"));
        Assert.Equal("2", Ids(index, x => x.Meta.Path("score") > 5));
        Assert.Equal("1", Ids(index, x => x.Meta.Path("active") == true));
        Assert.Equal("1", Ids(index, x => x.Meta.Path("when") >= new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("1,2", Ids(index, x => x.Meta.Path("customer.age").Exists() && x.Meta.Path("score").Exists()));
        Assert.Equal("", Ids(index, x => x.Meta.Path("nothing.here") == "x"));
        Assert.Equal("1,2,3,4", Ids(index, x => x.Meta.Path("customer.name") != "bob" || x.Meta.Path("customer.name") != "acme"));

        // The stored value comes back whole.
        SearchHit<Activity> hit = index.Search().Where(x => x.Meta.Path("customer.name") == "carol").ToList().Single();
        Assert.Equal("unknown", hit.Document.Meta.GetProperty("customer").GetProperty("age").GetString());
    }

    [Fact]
    public void FilterStringsAndQueryJsonReachPathsByTheKindOfTheValue()
    {
        using SearchIndex<Activity> index = SearchIndex.InMemory<Activity>();
        index.Upsert(Make("1", """{"customer":{"name":"acme","age":41},"when":"2024-03-01","active":true}"""));
        index.Upsert(Make("2", """{"customer":{"name":"bob","age":17},"when":"2022-01-01","active":false}"""));
        index.Commit();

        string Run(string json) => string.Join(",", index.Parse(json).Take(10).ToList().Select(h => h.Id).Order(StringComparer.Ordinal));

        Assert.Equal("1", Run("""{"v":1,"query":{"type":"filter","filter":"Meta.customer.name = \"acme\""}}"""));
        Assert.Equal("1", Run("""{"v":1,"query":{"type":"filter","filter":"Meta.customer.age >= 18"}}"""));
        Assert.Equal("1", Run("""{"v":1,"query":{"type":"filter","filter":"Meta.active = true"}}"""));
        Assert.Equal("1", Run("""{"v":1,"query":{"type":"filter","filter":"Meta.when > 2023-01-01"}}"""));
        Assert.Equal("2", Run("""{"v":1,"query":{"type":"filter","filter":"Meta.customer.age 10 TO 20"}}"""));
        Assert.Equal("2", Run("""{"v":1,"query":{"type":"filter","filter":"NOT Meta.customer.name = \"acme\""}}"""));
        Assert.Equal("1", Run("""{"v":1,"query":{"type":"term","field":"Meta.customer.name","value":"acme"}}"""));
        Assert.Equal("2", Run("""{"v":1,"query":{"type":"range","field":"Meta.customer.age","lt":18}}"""));
        Assert.Equal("1,2", Run("""{"v":1,"query":{"type":"exists","field":"Meta.when"}}"""));
        Assert.Equal("1", Run("""{"v":1,"query":{"type":"prefix","field":"Meta.customer.name","prefix":"ac"}}"""));
    }

    [Fact]
    public void ADottedKeyIsEscapedInThePath()
    {
        using SearchIndex<Activity> index = SearchIndex.InMemory<Activity>();
        index.Upsert(Make("1", """{"a.b":{"c":"x"},"a":{"b":{"c":"y"}}}"""));
        index.Commit();
        Assert.Equal("1", Ids(index, x => x.Meta.Path("a\\.b.c") == "x"));
        Assert.Equal("1", Ids(index, x => x.Meta.Path("a.b.c") == "y"));
        Assert.Equal("", Ids(index, x => x.Meta.Path("a.b.c") == "x"));
    }

    [Fact]
    public void PathsSurviveReopenAndMerges()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            using (SearchIndex<Activity> index = SearchIndex.Open<Activity>(dir))
            {
                for (int i = 0; i < 30; i++)
                {
                    index.Upsert(Make("e" + i, "{\"n\":" + i + ",\"s\":\"v" + (i % 3) + "\"" + (i % 2 == 0 ? ",\"only_even\":true" : string.Empty) + "}"));
                    if (i % 7 == 6)
                    {
                        index.Commit();
                    }
                }

                index.Commit();
            }

            using (SearchIndex<Activity> index = SearchIndex.Open<Activity>(dir))
            {
                Assert.Equal(30, index.Count);
                Assert.Equal(10, index.Search().Where(x => x.Meta.Path("s") == "v1").CountExactly().Take(1).Count());
                Assert.Equal(15, index.Search().Where(x => x.Meta.Path("only_even") == true).CountExactly().Take(1).Count());
                Assert.Equal(5, index.Search().Where(x => x.Meta.Path("n") >= 25).CountExactly().Take(1).Count());
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LimitsKeepHostileJsonFromGrowingTheSchema()
    {
        var limits = new IndexLimits { MaxDynamicPaths = 5, MaxDynamicDepth = 3, MaxDynamicPathBytes = 20, MaxDynamicValues = 50, MaxDynamicKeywordBytes = 8 };
        var storage = new MemoryStorage();
        Schema schema = new SchemaBuilder(limits).Id("id").Json("meta").Build();
        using var writer = IndexWriter.Open(storage, schema, new IndexWriterOptions { Limits = limits });
        var sb = new StringBuilder("{");
        for (int i = 0; i < 500; i++)
        {
            sb.Append("\"k").Append(i).Append("\":").Append(i).Append(',');
        }

        sb.Append("\"deep\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}},\"long_key_name_beyond_the_limit\":1,\"s\":\"much longer than eight bytes\"}");
        writer.Upsert(new Document().Add("id", "1").Add("meta", FieldValue.FromJson(Json(sb.ToString()))));
        writer.Commit();
        int derived = writer.Schema.Fields.Count(f => f.DynamicParent == "meta");
        Assert.InRange(derived, 1, 5);
        Assert.DoesNotContain(writer.Schema.Fields, f => f.Name.StartsWith("meta.deep", StringComparison.Ordinal) && f.Name.Contains("d#", StringComparison.Ordinal));
        Assert.DoesNotContain(writer.Schema.Fields, f => f.Name.Contains("long_key", StringComparison.Ordinal));
        Assert.DoesNotContain(writer.Schema.Fields, f => f.Name == "meta.s#k");

        // The whole value is still stored.
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        Assert.Equal(1, snapshot.LiveDocCount);
    }

    [Fact]
    public void ADerivedSchemaRoundTripsAsVersionTwoAndAPlainOneStaysVersionOne()
    {
        Schema declared = new SchemaBuilder().Id("id").Json("meta").Build();
        Schema grown = declared.WithAppended([DynamicFields.Define("meta.a#k", DynamicKind.Keyword, "meta"), DynamicFields.Define("meta.b#n", DynamicKind.Number, "meta")], IndexLimits.Default);
        byte[] bytes = grown.Serialize();
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal(grown, Schema.Deserialize(bytes));
        Assert.Equal(declared, grown.WithoutDynamicFields());
        Assert.Equal(1u, BitConverter.ToUInt32(declared.Serialize(), 4));
    }

    [Fact]
    public void ADocumentTypeThatChangesBetweenRunsEvolvesTheIndex()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            using (SearchIndex<EvolvingV1> v1 = SearchIndex.Open<EvolvingV1>(dir))
            {
                for (int i = 0; i < 40; i++)
                {
                    v1.Upsert(new EvolvingV1 { Id = "e" + i, Subject = i % 2 == 0 ? "printer running out of toner" : "login broken", Queue = "queue" + (i % 4), Meta = Json("{\"n\":" + i + "}") });
                    if (i % 10 == 9)
                    {
                        v1.Commit();
                    }
                }
            }

            using (SearchIndex<EvolvingV2> v2 = SearchIndex.Open<EvolvingV2>(dir))
            {
                Assert.Equal(40, v2.Count);
                Assert.True(v2.SchemaEvolution.InProgress);
                v2.CompleteSchemaEvolution();
                Assert.False(v2.SchemaEvolution.InProgress);
                Assert.Equal(10, v2.Search().Where(x => x.Queue == "queue1").CountExactly().Take(1).Count());
                Assert.Equal(20, v2.Search("run").CountExactly().Take(1).Count());
                Assert.Equal(15, v2.Search().Where(x => x.Meta.Path("n") >= 25).CountExactly().Take(1).Count());
            }

            Assert.Throws<SchemaEvolutionException>(() => SearchIndex.Open<EvolvingBad>(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- differential against LINQ-to-Objects over the same JSON ----

    private static string RandomJson(Random r)
    {
        var parts = new List<string>();
        if (r.Next(10) < 8)
        {
            string name = r.Next(10) < 9 ? "\"" + Names[r.Next(Names.Length)] + "\"" : r.Next(3) == 0 ? "null" : r.Next(100).ToString();
            string age = r.Next(10) switch { < 6 => r.Next(10, 70).ToString(), 6 => "\"" + r.Next(10, 70) + "\"", 7 => "null", _ => string.Empty };
            string customer = "\"name\":" + name + (age.Length > 0 ? ",\"age\":" + age : string.Empty);
            parts.Add("\"customer\":" + (r.Next(8) == 0 ? "[{" + customer + "},{\"name\":\"" + Names[r.Next(Names.Length)] + "\"}]" : "{" + customer + "}"));
        }

        if (r.Next(10) < 6)
        {
            int n = r.Next(0, 4);
            parts.Add("\"tags\":[" + string.Join(",", Enumerable.Range(0, n).Select(_ => "\"" + Colors[r.Next(Colors.Length)] + "\"")) + "]");
        }

        if (r.Next(10) < 6)
        {
            parts.Add("\"score\":" + (r.Next(3) == 0 ? "[" + r.Next(10) + "," + r.Next(10) + ".5]" : (r.Next(20) / 2.0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (r.Next(10) < 5)
        {
            parts.Add("\"active\":" + (r.Next(5) == 0 ? "\"true\"" : r.Next(2) == 0 ? "true" : "false"));
        }

        if (r.Next(10) < 5)
        {
            parts.Add("\"when\":" + (r.Next(6) == 0 ? "12345" : "\"" + (2020 + r.Next(6)) + "-0" + (1 + r.Next(9)) + "-1" + r.Next(10) + (r.Next(2) == 0 ? "T08:30:00Z" : string.Empty) + "\""));
        }

        if (r.Next(10) < 3)
        {
            parts.Add("\"nested\":{\"x\":{\"y\":" + r.Next(5) + "}}");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static List<Expression<Func<Activity, bool>>> Predicates(Random r)
    {
        string name = Names[r.Next(Names.Length)];
        string name2 = Names[r.Next(Names.Length)];
        string color = Colors[r.Next(Colors.Length)];
        double age = r.Next(10, 70);
        double score = r.Next(20) / 2.0;
        var when = new DateTimeOffset(2020 + r.Next(6), 1 + r.Next(9), 1, 0, 0, 0, TimeSpan.Zero);
        bool flag = r.Next(2) == 0;
        string lo = Names[r.Next(Names.Length)];
        return
        [
            x => x.Meta.Path("customer.name") == name,
            x => x.Meta.Path("customer.name") != name,
            x => x.Meta.Path("customer.age") >= age,
            x => x.Meta.Path("customer.age") < age,
            x => x.Meta.Path("customer.age") == age,
            x => x.Meta.Path("tags") == color,
            x => x.Meta.Path("tags") != color,
            x => x.Meta.Path("score") > score,
            x => x.Meta.Path("score") <= score,
            x => x.Meta.Path("active") == flag,
            x => x.Meta.Path("when") >= when,
            x => x.Meta.Path("when") < when,
            x => x.Meta.Path("customer.name").Exists(),
            x => x.Meta.Path("nested.x.y") == 3,
            x => x.Meta.Path("missing") == "x",
            x => x.Meta.Path("customer.name") == name || x.Meta.Path("customer.name") == name2,
            x => x.Meta.Path("customer.name") == name && x.Meta.Path("customer.age") > age,
            x => !(x.Meta.Path("customer.age") < age),
            x => x.Meta.Path("customer.name") >= lo,
            x => x.Meta.Path("customer.name") < lo && x.Meta.Path("tags") == color,
        ];
    }

    [Fact]
    public void PathFiltersAgreeWithLinqToObjectsOnGeneratedDocuments()
    {
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_DYNAMIC_SEEDS"), out int s) ? s : 8;
        int cases = 0;
        int matched = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            var r = new Random(seed);
            var docs = new List<Activity>();
            for (int i = 0; i < 150; i++)
            {
                docs.Add(Make("d" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), RandomJson(r)));
            }

            using SearchIndex<Activity> index = SearchIndex.InMemory<Activity>();
            for (int i = 0; i < docs.Count; i++)
            {
                index.Upsert(docs[i]);
                if (i % 40 == 39)
                {
                    index.Commit(); // several segments, so paths appear in later ones only
                }
            }

            index.Commit();
            for (int round = 0; round < 6; round++)
            {
                foreach (Expression<Func<Activity, bool>> predicate in Predicates(r))
                {
                    Func<Activity, bool> run = predicate.Compile();
                    string expected = string.Join(",", docs.Where(run).Select(d => d.Id).Order(StringComparer.Ordinal));
                    string actual = Ids(index, predicate);
                    Assert.True(expected == actual, $"seed {seed}: {predicate}\nexpected {expected}\nactual   {actual}");
                    cases++;
                    if (expected.Length > 0)
                    {
                        matched++;
                    }
                }
            }
        }

        Assert.True(matched > cases / 4, $"only {matched} of {cases} predicates matched anything; the generator is too sparse");
    }
}

public class EvolvingV1
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Subject { get; set; } = string.Empty;

    [Keyword]
    public string Queue { get; set; } = string.Empty;

    [Dynamic]
    public JsonElement Meta { get; set; }
}

public class EvolvingV2
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Language = "en")]
    public string Subject { get; set; } = string.Empty;

    [Keyword]
    [Filterable]
    public string Queue { get; set; } = string.Empty;

    [Dynamic]
    public JsonElement Meta { get; set; }
}

public class EvolvingBad
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Filterable]
    public int Subject { get; set; }
}
