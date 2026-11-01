using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json.Nodes;
using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex.Tests;

public class Shop
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;

    [Filterable]
    [Sortable]
    public int? Rating { get; set; }

    [Filterable]
    [Sortable]
    public double? Price { get; set; }

    [Filterable]
    [Sortable]
    public DateTime? Opened { get; set; }

    [Filterable]
    public bool? Open { get; set; }

    [Geo]
    public GeoPoint? Location { get; set; }

    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Function scores against a direct computation of the documented formulas over the documents' own values (not the columns), with the text
/// score of the same search without functions as the inner score. Also explain (its root equals the hit's score exactly), the top-k against a
/// brute-force rescoring of every match, and the query JSON round trip.
/// </summary>
public sealed class FunctionScoreTests(ITestOutputHelper output)
{
    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
    private static readonly double[] Prices = [0.0, -0.0, 0.25, 1.5, 2.5, 10, 99.99, -4, 1e9];
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static int Seeds => int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_DIFF_CASES"), CultureInfo.InvariantCulture, out int n) ? n : 30;

    private static Shop Make(Random r, int i)
    {
        bool has() => r.Next(100) >= 15;
        var words = new List<string>();
        int n = r.Next(1, 6);
        for (int k = 0; k < n; k++)
        {
            words.Add(Words[r.Next(Words.Length)]);
        }

        return new Shop
        {
            Id = "s" + i.ToString(CultureInfo.InvariantCulture),
            Title = string.Join(' ', words),
            Rating = has() ? r.Next(0, 10) : null,
            Price = has() ? Prices[r.Next(Prices.Length)] : null,
            Opened = has() ? Epoch.AddDays(r.Next(0, 400)).AddHours(r.Next(0, 24)) : null,
            Open = has() ? r.Next(2) == 0 : null,
            Location = has() ? new GeoPoint(r.NextDouble() * 160 - 80, r.NextDouble() * 340 - 170) : null,
        };
    }

    private static double Ms(DateTime d) => (d - DateTime.UnixEpoch).TotalMilliseconds;

    private sealed record Model(Func<Shop, double?> Value, float Weight, Func<Shop, bool>? When);

    private sealed class Plan
    {
        public List<Action<FunctionScoreBuilder<Shop>>> Adds { get; } = [];
        public List<Model> Models { get; } = [];
        public FunctionScoreMode ScoreMode { get; set; }
        public FunctionBoostMode BoostMode { get; set; }
        public float MaxBoost { get; set; } = float.MaxValue;
        public float? MinScore { get; set; }
        public string Describe { get; set; } = string.Empty;
    }

    private static double Decay(DecayShape shape, double distance, double offset, double scale, double decay)
    {
        double d = Math.Max(0, distance - offset);
        switch (shape)
        {
            case DecayShape.Gauss:
                double sigma2 = -scale * scale / (2 * Math.Log(decay));
                return Math.Exp(-d * d / (2 * sigma2));
            case DecayShape.Exp:
                double lambda = Math.Log(decay) / scale;
                return Math.Exp(lambda * d);
            default:
                double s = scale / (1 - decay);
                return Math.Max((s - d) / s, 0);
        }
    }

    private static double Modify(FieldValueModifier m, double x) => m switch
    {
        FieldValueModifier.None => x,
        FieldValueModifier.Log => Math.Log10(x),
        FieldValueModifier.Log1p => Math.Log10(x + 1),
        FieldValueModifier.Log2p => Math.Log10(x + 2),
        FieldValueModifier.Ln => Math.Log(x),
        FieldValueModifier.Ln1p => Math.Log(x + 1),
        FieldValueModifier.Ln2p => Math.Log(x + 2),
        FieldValueModifier.Square => x * x,
        FieldValueModifier.Sqrt => Math.Sqrt(x),
        _ => 1 / x,
    };

    private static Plan MakePlan(Random r)
    {
        var plan = new Plan
        {
            ScoreMode = (FunctionScoreMode)r.Next(6),
            BoostMode = (FunctionBoostMode)r.Next(6),
            MaxBoost = r.Next(4) == 0 ? (float)(r.NextDouble() * 5 + 0.5) : float.MaxValue,
            MinScore = r.Next(5) == 0 ? (float)(r.NextDouble() * 2) : null,
        };
        int count = r.Next(1, 4);
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            float weight = r.Next(3) == 0 ? (float)(r.Next(0, 8) * 0.5) : 1f;
            bool filtered = r.Next(4) == 0;
            Expression<Func<Shop, bool>>? when = filtered ? x => x.Rating >= 4 : null;
            Func<Shop, bool>? whenModel = filtered ? s => s.Rating >= 4 : null;
            switch (r.Next(6))
            {
                case 0:
                    plan.Adds.Add(b => b.Weight(weight, when));
                    plan.Models.Add(new Model(_ => 1d, weight, whenModel));
                    names.Add("weight");
                    break;
                case 1:
                    {
                        var mod = (FieldValueModifier)r.Next(10);
                        double factor = r.Next(3) == 0 ? -1.5 : r.Next(1, 4) * 0.5;
                        double? missing = r.Next(2) == 0 ? r.Next(0, 5) : null;
                        int which = r.Next(4);
                        plan.Adds.Add(b =>
                        {
                            switch (which)
                            {
                                case 0: b.FieldValue(x => x.Rating, factor, mod, missing, weight, when); break;
                                case 1: b.FieldValue(x => x.Price, factor, mod, missing, weight, when); break;
                                case 2: b.FieldValue(x => x.Open, factor, mod, missing, weight, when); break;
                                default: b.FieldValue(x => x.Opened, factor / 1e9, mod, missing, weight, when); break;
                            }
                        });
                        plan.Models.Add(new Model(
                            s =>
                            {
                                double? v = which switch
                                {
                                    0 => s.Rating,
                                    1 => s.Price,
                                    2 => s.Open is { } o ? (o ? 1 : 0) : null,
                                    _ => s.Opened is { } d ? Ms(d) : null,
                                };
                                double? f = which == 3 ? factor / 1e9 : factor;
                                if (v is null && missing is null)
                                {
                                    return null;
                                }

                                double raw = Modify(mod, f.Value * (v ?? missing!.Value));
                                return double.IsFinite(raw) && raw >= 0 ? raw : null;
                            },
                            weight,
                            whenModel));
                        names.Add("fvf " + mod);
                        break;
                    }

                case 2:
                case 3:
                    {
                        var shape = (DecayShape)r.Next(3);
                        double origin = r.Next(0, 10);
                        double scale = r.Next(1, 6);
                        double offset = r.Next(0, 3);
                        double decay = new[] { 0.1, 0.33, 0.5, 0.9 }[r.Next(4)];
                        bool onPrice = r.Next(2) == 0;
                        plan.Adds.Add(b =>
                        {
                            if (onPrice)
                            {
                                b.Decay(x => x.Price, shape, origin, scale, offset, decay, weight, when);
                            }
                            else
                            {
                                b.Decay(x => x.Rating, shape, origin, scale, offset, decay, weight, when);
                            }
                        });
                        plan.Models.Add(new Model(
                            s =>
                            {
                                double? v = onPrice ? s.Price : s.Rating;
                                return v is null ? 1d : Decay(shape, Math.Abs(v.Value - origin), offset, scale, decay);
                            },
                            weight,
                            whenModel));
                        names.Add("decay " + shape);
                        break;
                    }

                case 4:
                    {
                        var shape = (DecayShape)r.Next(3);
                        DateTime origin = Epoch.AddDays(r.Next(0, 400));
                        double scaleDays = r.Next(5, 120);
                        double offsetDays = r.Next(0, 20);
                        double decay = new[] { 0.1, 0.5, 0.75 }[r.Next(3)];
                        plan.Adds.Add(b => b.Decay(x => x.Opened, shape, new DateTimeOffset(origin), TimeSpan.FromDays(scaleDays), TimeSpan.FromDays(offsetDays), decay, weight, when));
                        plan.Models.Add(new Model(
                            s => s.Opened is { } d ? Decay(shape, Math.Abs(Ms(d) - Ms(origin)), offsetDays * 86_400_000, scaleDays * 86_400_000, decay) : 1d,
                            weight,
                            whenModel));
                        names.Add("date decay " + shape);
                        break;
                    }

                default:
                    {
                        var shape = (DecayShape)r.Next(3);
                        double lat = r.NextDouble() * 160 - 80;
                        double lng = r.NextDouble() * 340 - 170;
                        double scale = r.Next(500_000, 6_000_000);
                        double offset = r.Next(0, 3) * 100_000;
                        double decay = new[] { 0.2, 0.5 }[r.Next(2)];
                        plan.Adds.Add(b => b.Distance(x => x.Location, shape, lat, lng, scale, offset, decay, weight, when));
                        plan.Models.Add(new Model(
                            s => s.Location is { } p ? Decay(shape, p.DistanceMetersTo(new GeoPoint(lat, lng)), offset, scale, decay) : 1d,
                            weight,
                            whenModel));
                        names.Add("geo decay " + shape);
                        break;
                    }
            }
        }

        plan.Describe = $"{string.Join('+', names)} score={plan.ScoreMode} boost={plan.BoostMode} max={plan.MaxBoost} min={plan.MinScore}";
        return plan;
    }

    // The documented formulas, in double: the values of the applicable functions, combined, capped, mixed with the inner score.
    private static double? Expected(Plan plan, Shop s, double inner)
    {
        var values = new List<double>();
        double weights = 0;
        foreach (Model m in plan.Models)
        {
            if (m.When is not null && !m.When(s))
            {
                continue;
            }

            if (m.Value(s) is not { } raw)
            {
                continue;
            }

            values.Add(raw * m.Weight);
            weights += m.Weight;
        }

        double result;
        if (values.Count == 0)
        {
            result = inner;
        }
        else
        {
            double combined = plan.ScoreMode switch
            {
                FunctionScoreMode.Multiply => values.Aggregate(1d, (a, b) => a * b),
                FunctionScoreMode.Sum => values.Sum(),
                FunctionScoreMode.Average => weights == 0 ? 0 : values.Sum() / weights,
                FunctionScoreMode.First => values[0],
                FunctionScoreMode.Max => values.Max(),
                _ => values.Min(),
            };
            combined = Math.Min(combined, plan.MaxBoost);
            result = plan.BoostMode switch
            {
                FunctionBoostMode.Multiply => inner * combined,
                FunctionBoostMode.Replace => combined,
                FunctionBoostMode.Sum => inner + combined,
                FunctionBoostMode.Average => (inner + combined) / 2,
                FunctionBoostMode.Max => Math.Max(inner, combined),
                _ => Math.Min(inner, combined),
            };
        }

        return plan.MinScore is { } min && result < min ? null : result;
    }

    private static bool Near(double expected, double actual) => Math.Abs(expected - actual) <= 1e-6 * Math.Max(1, Math.Abs(expected));

    [Fact]
    public void ScoresMatchTheDocumentedFormulasAndTopKMatchesBruteForce()
    {
        int cases = 0;
        int applied = 0;
        int explained = 0;
        int dropped = 0;
        double worst = 0;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            var random = new Random(seed * 7919);
            var docs = Enumerable.Range(0, 150).Select(i => Make(random, i)).ToList();
            using SearchIndex<Shop> index = SearchIndex.InMemory<Shop>();
            index.Upsert(docs);
            index.Commit();
            for (int round = 0; round < 2; round++)
            {
                foreach (Shop d in docs.Where(_ => random.Next(6) == 0).ToList())
                {
                    d.Rating = random.Next(0, 10);
                    d.Title += " " + Words[random.Next(Words.Length)];
                    index.Upsert(d);
                }

                foreach (Shop d in docs.Where(_ => random.Next(15) == 0).ToList())
                {
                    index.Delete(d.Id);
                    docs.Remove(d);
                }

                index.Commit();
            }

            Dictionary<string, Shop> byId = docs.ToDictionary(d => d.Id, StringComparer.Ordinal);
            for (int c = 0; c < 20; c++)
            {
                string text = random.Next(3) == 0 ? "alpha" : Words[random.Next(Words.Length)] + " " + Words[random.Next(Words.Length)];
                Dictionary<string, float> inner = index.Search(text).Take(1000).Select(h => (h.Id, h.Score)).ToDictionary(t => t.Id, t => t.Score, StringComparer.Ordinal);
                Plan plan = MakePlan(random);
                SearchQuery<Shop> baseSearch = index.Search(text).ScoreBy(b =>
                {
                    foreach (Action<FunctionScoreBuilder<Shop>> add in plan.Adds)
                    {
                        add(b);
                    }

                    b.ScoreMode = plan.ScoreMode;
                    b.BoostMode = plan.BoostMode;
                    b.MaxBoost = plan.MaxBoost;
                    b.MinScore = plan.MinScore;
                }).Explain();

                SearchResults<Shop> all = baseSearch.Take(1000).Execute();
                var model = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach ((string id, float innerScore) in inner)
                {
                    if (Expected(plan, byId[id], innerScore) is { } e)
                    {
                        model[id] = e;
                    }
                    else
                    {
                        dropped++;
                    }
                }

                string where = $"seed {seed} case {c} '{text}': {plan.Describe}";
                Assert.True(model.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(all.Select(h => h.Id).OrderBy(k => k, StringComparer.Ordinal)), $"{where}: matched sets differ ({model.Count} vs {all.Count})");
                foreach (SearchHit<Shop> hit in all)
                {
                    double expected = model[hit.Id];
                    Assert.True(Near(expected, hit.Score), $"{where}: {hit.Id} expected {expected} got {hit.Score}\n{hit.Explanation}");
                    worst = Math.Max(worst, Math.Abs(expected - hit.Score) / Math.Max(1, Math.Abs(expected)));
                    Assert.NotNull(hit.Explanation);
                    Assert.True(hit.Explanation.Value == hit.Score, $"{where}: {hit.Id} explain {hit.Explanation.Value:R} vs score {hit.Score:R}\n{hit.Explanation}");
                    explained++;
                    applied++;
                }

                // Top k: the page is the k best of the brute-force rescoring, up to ties within the tolerance.
                const int k = 7;
                List<SearchHit<Shop>> top = baseSearch.Take(k).ToList();
                double[] best = model.Values.OrderByDescending(v => v).Take(k).ToArray();
                Assert.Equal(best.Length, top.Count);
                for (int i = 0; i < top.Count; i++)
                {
                    Assert.True(Near(best[i], model[top[i].Id]), $"{where}: rank {i} has model score {model[top[i].Id]}, brute force says {best[i]}");
                }

                cases++;
            }
        }

        output.WriteLine($"cases {cases}, hits checked {applied}, explained {explained}, documents dropped by minScore {dropped}, worst relative deviation {worst:E2}");
        Assert.Equal(Seeds * 20, cases);
        Assert.True(applied > cases * 5);
    }

    [Fact]
    public void ExplainEqualsTheScoreOnEveryExecutionPath()
    {
        var random = new Random(11);
        using SearchIndex<Shop> index = SearchIndex.InMemory<Shop>();
        index.Upsert(Enumerable.Range(0, 200).Select(i => Make(random, i)));
        index.Commit();
        index.Upsert(Enumerable.Range(200, 50).Select(i => Make(random, i)));
        index.Commit();
        int checkedHits = 0;
        foreach (SearchQuery<Shop> q in new[]
        {
            index.Search("alpha beta").Explain(),
            index.Search("alpha OR gamma \"beta eta\"").Where("Rating >= 3").Explain(),
            index.Search("alpha").Where(x => x.Open == true).Facets(x => x.Rating).Explain(),
            index.Search("beta").OrderBy(x => x.Rating).ThenByRelevance().Explain(),
            index.Search("gamma").Explain().CountExactly().Skip(3),
            index.Search("alpha").ScoreBy(b => b.FieldValue(x => x.Rating, modifier: FieldValueModifier.Ln1p, missing: 0)).Where("Rating >= 1").Explain(),
        })
        {
            SearchResults<Shop> hits = q.Take(15).Execute();
            Assert.NotEmpty(hits);
            foreach (SearchHit<Shop> hit in hits)
            {
                Assert.NotNull(hit.Explanation);
                Assert.True(hit.Explanation.Value == hit.Score, $"{hit.Id}: {hit.Explanation.Value:R} vs {hit.Score:R}\n{hit.Explanation}");
                checkedHits++;
            }
        }

        Assert.Null(index.Search("alpha").Take(1).Single().Explanation);
        output.WriteLine($"explain checked on {checkedHits} hits over 6 execution paths");
    }

    [Fact]
    public void NoFunctionAppliesKeepsTheTextScore()
    {
        using SearchIndex<Shop> index = SearchIndex.InMemory<Shop>();
        index.Upsert(new Shop { Id = "a", Title = "alpha beta", Rating = 2 });
        index.Upsert(new Shop { Id = "b", Title = "alpha", Rating = null });
        index.Commit();
        float a = index.Search("alpha").Where(x => x.Rating == 2).Single().Score;
        SearchHit<Shop> hit = index.Search("alpha").ScoreBy(b => b.FieldValue(x => x.Rating, modifier: FieldValueModifier.Sqrt, weight: 3f)).Explain().OrderByRelevance().ToList().Single(h => h.Id == "b");
        float plain = index.Search("alpha").ToList().Single(h => h.Id == "b").Score;
        Assert.Equal(plain, hit.Score);
        Assert.Contains("no function applies", hit.Explanation!.Description);
        Assert.True(a > 0);
    }

    [Fact]
    public void CustomFunctionRunsOverColumns()
    {
        using SearchIndex<Shop> index = SearchIndex.InMemory<Shop>();
        for (int i = 0; i < 10; i++)
        {
            index.Upsert(new Shop { Id = "s" + i, Title = "same words", Rating = i });
        }

        index.Commit();
        var custom = new CustomFunction("rating squared", reader =>
        {
            IColumnReader? rating = reader.GetColumn("Rating");
            return doc => rating is not null && rating.HasValue(doc) ? rating.GetInt64(doc) * (double)rating.GetInt64(doc) : null;
        });
        SearchResults<Shop> hits = index.Search("same").ScoreBy(b =>
        {
            b.Add(custom);
            b.BoostMode = FunctionBoostMode.Replace;
        }).Explain().Take(10).Execute();
        Assert.Equal(10, hits.Count); // rating 0 squares to 0: the function still applies, and scores 0
        Assert.Equal(["s9", "s8", "s7", "s6", "s5", "s4", "s3", "s2", "s1", "s0"], hits.Select(h => h.Id));
        Assert.Equal(81f, hits[0].Score);
        Assert.Contains("rating squared", hits[0].Explanation!.ToString());
    }

    [Fact]
    public void BadFunctionsAreRejectedNamingTheProblem()
    {
        using SearchIndex<Shop> index = SearchIndex.InMemory<Shop>();
        index.Upsert(new Shop { Id = "a", Title = "alpha" });
        index.Commit();
        ArgumentException e = Assert.Throws<ArgumentException>(() => index.Search("alpha").ScoreBy(b => b.Decay(x => x.Rating, DecayShape.Gauss, 0, -1)));
        Assert.Contains("scale", e.Message);
        e = Assert.Throws<ArgumentException>(() => index.Search("alpha").ScoreBy(b => b.Decay(x => x.Rating, DecayShape.Gauss, 0, 1, decay: 1)));
        Assert.Contains("decay", e.Message);
        e = Assert.Throws<ArgumentException>(() => index.Search("alpha").ScoreBy(b => b.Weight(-1)));
        Assert.Contains("weight", e.Message);
        e = Assert.Throws<ArgumentException>(() => index.Search("alpha").ScoreBy(b => b.FieldValue(x => x.Title)));
        Assert.Contains("Title", e.Message);
        Assert.Throws<QueryException>(() => index.SearchBy(new FunctionScoreQuery(new MatchAllQuery(), [new FieldValueFactorFunction("Nope")])).Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(new FunctionScoreQuery(new MatchAllQuery(), [])).Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(new FunctionScoreQuery(new MatchAllQuery(), [new WeightFunction()]) { MaxBoost = 0 }).Execute());
    }

    [Fact]
    public void QueryJsonRoundTripsFunctionScoreNodes()
    {
        using SearchIndex<Shop> index = SearchIndex.InMemory<Shop>();
        string json = """
            {"v":1,"query":{"type":"functionScore","query":{"type":"text","text":"alpha"},
              "functions":[
                {"type":"fieldValueFactor","field":"Rating","factor":2,"modifier":"log1p","missing":1,"weight":2},
                {"type":"gauss","field":"Price","origin":0,"scale":20,"filter":"Rating >= 4"},
                {"type":"linear","field":"Opened","origin":"2020-06-01","scale":86400000,"offset":1000,"decay":0.25},
                {"type":"exp","field":"Location","origin":{"lat":48.85,"lng":2.29},"scale":2000,"weight":0.5},
                {"type":"weight","weight":3,"filter":{"type":"term","field":"Open","value":true}}],
              "scoreMode":"avg","boostMode":"sum","maxBoost":10,"minScore":0.5,"boost":2}}
            """;
        QueryRequest request = QueryJson.Read(json, index.Schema);
        var fs = Assert.IsType<FunctionScoreQuery>(request.Query);
        Assert.Equal(5, fs.Functions.Count);
        Assert.Equal(2f, fs.Boost);
        Assert.Equal(FunctionScoreMode.Average, fs.ScoreMode);
        var date = Assert.IsType<DecayFunction>(fs.Functions[2]);
        Assert.Equal(new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), date.Origin);
        Assert.IsType<GeoDecayFunction>(fs.Functions[3]);
        string written = QueryJson.Write(request);
        QueryRequest back = QueryJson.Read(written, index.Schema);
        Assert.True(request.Equals(back), written);
        Assert.Equal(written, QueryJson.Write(back));

        // Random function-score documents survive the round trip.
        var r = new Random(4242);
        for (int i = 0; i < 1500; i++)
        {
            Plan plan = MakePlan(r);
            var q = (FunctionScoreQuery)ToQuery(index, plan);
            var req = new QueryRequest { Query = q };
            string text = QueryJson.Write(req);
            QueryRequest again = QueryJson.Read(text, index.Schema);
            Assert.True(req.Equals(again), $"case {i}: {text}");
            Assert.Equal(text, QueryJson.Write(again));
        }
    }

    private static Search.Query ToQuery(SearchIndex<Shop> index, Plan plan)
    {
        SearchQuery<Shop> s = index.Search("alpha").ScoreBy(b =>
        {
            foreach (Action<FunctionScoreBuilder<Shop>> add in plan.Adds)
            {
                add(b);
            }

            b.ScoreMode = plan.ScoreMode;
            b.BoostMode = plan.BoostMode;
            b.MaxBoost = plan.MaxBoost;
            b.MinScore = plan.MinScore;
        });
        return s.ToQuery();
    }

    private static IEnumerable<JsonNode> FunctionMutations(string json)
    {
        JsonNode Fresh() => JsonNode.Parse(json)!;
        static JsonObject Node(JsonNode root) => root["query"]!.AsObject();
        static JsonObject First(JsonNode root) => Node(root)["functions"]![0]!.AsObject();

        JsonNode m = Fresh();
        First(m)["bogus"] = 1;
        yield return m;
        m = Fresh();
        Node(m).Remove("functions");
        yield return m;
        m = Fresh();
        Node(m)["scoreMode"] = "mean";
        yield return m;
        m = Fresh();
        Node(m)["maxBoost"] = -1;
        yield return m;
        m = Fresh();
        First(m)["weight"] = -2;
        yield return m;
        if (First(m)["type"]!.GetValue<string>() is "gauss" or "exp" or "linear")
        {
            m = Fresh();
            First(m)["scale"] = 0;
            yield return m;
            m = Fresh();
            First(m)["decay"] = 1;
            yield return m;
            m = Fresh();
            First(m)["field"] = "Title";
            yield return m;
            m = Fresh();
            First(m).Remove("origin");
            yield return m;
        }

        if (First(m)["type"]!.GetValue<string>() == "fieldValueFactor")
        {
            m = Fresh();
            First(m)["modifier"] = "bogus";
            yield return m;
        }
    }

    [Fact]
    public void ToolSchemaAgreesWithParseOnFunctionScoreNodes()
    {
        using SearchIndex<Shop> index = SearchIndex.InMemory<Shop>();
        string schemaText = SearchTool.SchemaFor<Shop>();
        var r = new Random(515);
        var documents = new List<(string Json, bool Valid)>();
        for (int i = 0; i < 250; i++)
        {
            Plan plan = MakePlan(r);
            string json = QueryJson.Write(new QueryRequest { Query = ToQuery(index, plan) });
            documents.Add((json, true));
            foreach (JsonNode mutated in FunctionMutations(json))
            {
                documents.Add((mutated.ToJsonString(), false));
            }
        }

        foreach ((string json, bool valid) in documents)
        {
            Assert.Equal(valid, index.TryParse(json, out _, out QueryParseException? err));
            Assert.True(valid || err is not null);
        }

        string? python = Environment.GetEnvironmentVariable("ONEINDEX_JSONSCHEMA_PYTHON");
        if (string.IsNullOrEmpty(python))
        {
            output.WriteLine($"{documents.Count} documents checked by Parse only (no ONEINDEX_JSONSCHEMA_PYTHON)");
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "oneindex-jsonschema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "schema.json"), schemaText);
            File.WriteAllLines(Path.Combine(dir, "docs.jsonl"), documents.Select(d => d.Json));
            string script = Path.Combine(AppContext.BaseDirectory, "jsonschema_check.py");
            var start = new System.Diagnostics.ProcessStartInfo(python, $"\"{script}\" \"{dir}/schema.json\" \"{dir}/docs.jsonl\"") { RedirectStandardOutput = true, RedirectStandardError = true };
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(start)!;
            string result = p.StandardOutput.ReadToEnd();
            string errors = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, errors);
            string[] verdicts = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(documents.Count, verdicts.Length);
            for (int i = 0; i < verdicts.Length; i++)
            {
                Assert.True(verdicts[i].StartsWith("valid", StringComparison.Ordinal) == documents[i].Valid, $"validator says {verdicts[i]} but Parse says {documents[i].Valid}: {documents[i].Json}");
            }

            output.WriteLine($"{documents.Count} documents agree between the validator and Parse");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void QueryJsonErrorsNameThePath()
    {
        using SearchIndex<Shop> index = SearchIndex.InMemory<Shop>();
        static void Fails(SearchIndex<Shop> i, string json, string path, string contains)
        {
            QueryParseException e = Assert.Throws<QueryParseException>(() => QueryJson.Read(json, i.Schema));
            Assert.Equal(path, e.Path);
            Assert.Contains(contains, e.Message);
        }

        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[]}}""", "$.query.functions", "1 to 32");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"gaus","field":"Price","origin":0,"scale":1}]}}""", "$.query.functions[0].type", "gauss");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"gauss","field":"Price","origin":0,"scale":0}]}}""", "$.query.functions[0].scale", "number");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"gauss","field":"Prise","origin":0,"scale":1}]}}""", "$.query.functions[0].field", "Price");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"fieldValueFactor","field":"Title"}]}}""", "$.query.functions[0]", "numeric");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"fieldValueFactor","field":"Rating","modifier":"sqr"}]}}""", "$.query.functions[0].modifier", "sqrt");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"weight"}],"scoreMode":"mean"}}""", "$.query.scoreMode", "avg");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"exp","field":"Location","origin":5,"scale":1}]}}""", "$.query.functions[0].origin", "lat");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"weight","filter":"Nope = 1"}]}}""", "$.query.functions[0].filter", "Nope");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"weight","extra":1}]}}""", "$.query.functions[0].extra", "extra");
        Fails(index, """{"v":1,"query":{"type":"functionScore","functions":[{"type":"linear","field":"Opened","origin":"yesterday","scale":1}]}}""", "$.query.functions[0].origin", "date");
    }
}
