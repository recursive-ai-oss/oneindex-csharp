using System.Text.Json;
using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

[RankingRules("words", "typo", "proximity", "attribute", "sort", "exactness")]
[Proximity(1.5f)]
[TypoTolerance(Enabled = false)]
[PrefixSearch(false)]
public class RuledDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;

    [FullText]
    public string Body { get; set; } = string.Empty;

    [Sortable]
    public long Price { get; set; }
}

[TypoTolerance(Enabled = false)]
[PrefixSearch(false)]
public class ProximityDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Body { get; set; } = string.Empty;

    [Sortable]
    public int Price { get; set; }
}

[Proximity(0f)]
[TypoTolerance(Enabled = false)]
[PrefixSearch(false)]
public class NoProximityDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Body { get; set; } = string.Empty;
}

public sealed class RankingTests
{
    private static readonly string[] Vocabulary = ["red", "apple", "pie", "big", "tree", "green", "farm", "juicy", "fruit", "sweet", "cider", "seed"];

    private static string[] RandomTokens(Random rng, int max)
    {
        int n = rng.Next(1, max);
        var t = new string[n];
        for (int i = 0; i < n; i++)
        {
            // Skewed so that some words are common and some rare.
            t[i] = Vocabulary[Math.Min(rng.Next(Vocabulary.Length), rng.Next(Vocabulary.Length))];
        }

        return t;
    }

    private sealed class LowLevel : IDisposable
    {
        private readonly IndexSnapshot _snapshot;

        public LowLevel(IReadOnlyList<string[]> docs, int segmentEvery)
        {
            Schema schema = new SchemaBuilder().Id("id").Add(new FieldDefinition("t", FieldType.Text) { Analyzer = "whitespace-lowercase" }).Build();
            var storage = new MemoryStorage();
            using (IndexWriter writer = IndexWriter.Open(storage, schema, new IndexWriterOptions { MaxBufferedDocuments = segmentEvery, AutoMerge = false }))
            {
                for (int i = 0; i < docs.Count; i++)
                {
                    writer.Upsert(new Document().Add("id", "d" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).Add("t", string.Join(' ', docs[i])));
                }

                writer.Commit();
            }

            _snapshot = IndexSnapshot.Open(storage);
            Searcher = SnapshotSearch.CreateSearcher(_snapshot);
        }

        public IndexSearcher Searcher { get; }

        public void Dispose() => _snapshot.Dispose();
    }

    // The proximity bonus from the tokens alone: pairs of occurrences of two different query words with no query word between them.
    private static double Bonus(string[] tokens, string[] words, double[] idf, double k1, double b, double averageLength)
    {
        var acc = new double[words.Length];
        var at = new List<int>();
        for (int i = 0; i < tokens.Length; i++)
        {
            if (Array.IndexOf(words, tokens[i]) >= 0)
            {
                at.Add(i);
            }
        }

        for (int a = 0; a + 1 < at.Count; a++)
        {
            int wa = Array.IndexOf(words, tokens[at[a]]);
            int wb = Array.IndexOf(words, tokens[at[a + 1]]);
            if (wa == wb)
            {
                continue;
            }

            double d = at[a + 1] - at[a];
            acc[wa] += idf[wb] / (d * d);
            acc[wb] += idf[wa] / (d * d);
        }

        double norm = k1 * (1 - b + (b * tokens.Length / averageLength)); // K of the paper
        double sum = 0;
        for (int w = 0; w < words.Length; w++)
        {
            if (acc[w] > 0)
            {
                sum += Math.Min(1.0, idf[w]) * ((k1 + 1) * acc[w] / (acc[w] + norm));
            }
        }

        return sum;
    }

    [Theory]
    [InlineData(1, 40)]
    [InlineData(2, 200)]
    [InlineData(3, 600)]
    public void TheProximityBonusEqualsADirectComputationFromPositions(int seed, int docCount)
    {
        var rng = new Random(seed);
        var docs = new List<string[]>();
        for (int i = 0; i < docCount; i++)
        {
            docs.Add(RandomTokens(rng, 30));
        }

        using var index = new LowLevel(docs, 97);
        double averageLength = docs.Average(d => (double)d.Length);
        for (int q = 0; q < 40; q++)
        {
            string[] words = [.. Vocabulary.OrderBy(_ => rng.Next()).Take(rng.Next(2, 5))];
            double[] idf = [.. words.Select(w =>
            {
                int n = docs.Count(d => Array.IndexOf(d, w) >= 0);
                return (double)(float)Math.Log(1.0 + ((docs.Count - n + 0.5) / (n + 0.5)));
            })];
            float weight = (float)(0.25 * rng.Next(1, 8));
            Search.Query inner = new BooleanQuery([.. words.Select(w => new BooleanClause(new TermQuery("t", w), Occur.Should))]);
            Search.Query prox = new ProximityQuery(inner, [.. words.Select(w => (Search.Query)new TermQuery("t", w))]) { Weight = weight };
            TopDocs plain = index.Searcher.Search(inner, docs.Count);
            TopDocs with = index.Searcher.Search(prox, docs.Count, true);
            Assert.Equal(plain.Hits.Count, with.Hits.Count);
            var baseScore = plain.Hits.ToDictionary(h => h.DocId, h => h.Score);
            foreach (ScoreDoc hit in with.Hits)
            {
                double expected = baseScore[hit.DocId] + (weight * Bonus(docs[hit.DocId], words, idf, 0.9, 0.4, averageLength));
                Assert.True(Math.Abs(expected - hit.Score) <= 1e-4 * Math.Max(1, expected), $"seed {seed} query {string.Join(' ', words)} doc {hit.DocId}: {hit.Score} vs {expected}");
                // The explanation is the score, bit for bit.
                Assert.Equal(hit.Score, index.Searcher.Explain(prox, hit.DocId)!.Value);
            }
        }
    }

    [Theory]
    [InlineData(11, 300, 1)]
    [InlineData(12, 900, 3)]
    public void PrunedTopKEqualsExhaustiveScoringWithProximity(int seed, int docCount, int mustWords)
    {
        var rng = new Random(seed);
        var docs = new List<string[]>();
        for (int i = 0; i < docCount; i++)
        {
            docs.Add(RandomTokens(rng, 40));
        }

        using var index = new LowLevel(docs, 128);
        int cases = 0;
        for (int q = 0; q < 60; q++)
        {
            string[] words = [.. Vocabulary.OrderBy(_ => rng.Next()).Take(rng.Next(2, 6))];
            var clauses = new List<BooleanClause>();
            for (int w = 0; w < words.Length; w++)
            {
                clauses.Add(new BooleanClause(new TermQuery("t", words[w]), w < mustWords ? Occur.Must : Occur.Should));
            }

            Search.Query prox = new ProximityQuery(new BooleanQuery(clauses), [.. words.Select(w => (Search.Query)new TermQuery("t", w))]) { Weight = 0.5f + (rng.Next(0, 4) * 0.5f) };
            int k = rng.Next(1, 25);
            var exhaustive = new TopScoreCollector(k, true);
            index.Searcher.Search(prox, exhaustive);
            var pruned = new TopScoreCollector(k, false);
            index.Searcher.Search(prox, pruned);
            Assert.Equal(exhaustive.GetTopDocs().Hits.Select(h => (h.DocId, h.Score)), pruned.GetTopDocs().Hits.Select(h => (h.DocId, h.Score)));
            cases++;
        }

        Assert.Equal(60, cases);
    }

    [Fact]
    public void UserTextRanksNearbyWordsFirstAndTheWeightCanBeTurnedOff()
    {
        string[] bodies = ["red apple pie with cream", "red big green tree and a juicy sweet apple", "apple farm in a big place far from the red barn", "red cider"];
        using SearchIndex<ProximityDoc> on = SearchIndex.InMemory<ProximityDoc>();
        using SearchIndex<NoProximityDoc> off = SearchIndex.InMemory<NoProximityDoc>();
        for (int i = 0; i < bodies.Length; i++)
        {
            on.Upsert(new ProximityDoc { Id = "d" + i, Body = bodies[i] });
            off.Upsert(new NoProximityDoc { Id = "d" + i, Body = bodies[i] });
        }

        on.Commit();
        off.Commit();
        List<SearchHit<ProximityDoc>> hits = on.Search("red apple").Explain().ToList();
        Assert.Equal(["d0", "d1", "d2"], hits.Select(h => h.Id)); // the closest pair first
        Assert.All(hits, h => Assert.Equal(h.Score, h.Explanation!.Value));
        Assert.Contains("proximity", hits[0].Explanation!.ToString());
        Assert.IsNotType<ProximityQuery>(off.Search("red apple").ToQuery());
        Assert.IsType<ProximityQuery>(on.Search("red apple").ToQuery());
        Assert.IsNotType<ProximityQuery>(on.Search("red").ToQuery()); // one word has nothing to be near
        Assert.True(hits[0].Score > off.Search("red apple").First().Score);
    }

    [Fact]
    public void ProximityAndRankingSettingsSurviveReopening()
    {
        var builder = new SchemaBuilder().Id("id");
        builder.Add(new FieldDefinition("t", FieldType.Text));
        builder.Settings = new TextSettings { ProximityWeight = 1.25f, RankingRules = ["words", "price:desc", "exactness"] };
        Schema schema = builder.Build();
        Schema read = Schema.Deserialize(schema.Serialize());
        Assert.Equal(1.25f, read.Settings.ProximityWeight);
        Assert.Equal(["words", "price:desc", "exactness"], read.Settings.RankingRules);
        Assert.True(schema.Equals(read));
        Schema plain = new SchemaBuilder().Id("id").Add(new FieldDefinition("t", FieldType.Text)).Build();
        Assert.Equal(Search.Proximity.DefaultWeight, Schema.Deserialize(plain.Serialize()).Settings.ProximityWeight);
        Assert.Null(Schema.Deserialize(plain.Serialize()).Settings.RankingRules);
        Assert.Throws<ArgumentException>(() => RankingRule.ParseList(["words", "words"]));
        Assert.Throws<ArgumentException>(() => RankingRule.ParseList(["nope"]));
        Assert.Throws<ArgumentException>(() => RankingRule.ParseList(["price:sideways"]));
    }

    // The bucket values of a document for a query, from the tokens alone (no typos or prefixes in this corpus).
    private static (int Missing, int Proximity, long Attribute) Buckets(string[] title, string[] body, string[] words)
    {
        int missing = 0;
        long attribute = 0;
        var matched = new List<int>();
        for (int w = 0; w < words.Length; w++)
        {
            int ft = Array.IndexOf(title, words[w]);
            int fb = Array.IndexOf(body, words[w]);
            if (ft < 0 && fb < 0)
            {
                missing++;
                continue;
            }

            matched.Add(w);
            attribute += ft >= 0 ? ft : (100_000_000L * 1) + fb;
        }

        int prox = 0;
        for (int i = 1; i < matched.Count; i++)
        {
            int best = 8;
            foreach (string[] field in new[] { title, body })
            {
                for (int a = 0; a < field.Length; a++)
                {
                    if (field[a] != words[matched[i - 1]])
                    {
                        continue;
                    }

                    for (int b = 0; b < field.Length; b++)
                    {
                        if (field[b] == words[matched[i]])
                        {
                            best = Math.Min(best, b >= a ? Math.Max(1, b - a) : a - b + 1);
                        }
                    }
                }
            }

            prox += best;
        }

        return (missing, prox, attribute);
    }

    [Theory]
    [InlineData(21, 80)]
    [InlineData(22, 250)]
    [InlineData(23, 400)]
    public void RankingRulesOrderEqualsABruteForceSortByTheRuleTuple(int seed, int docCount)
    {
        var rng = new Random(seed);
        using SearchIndex<RuledDoc> index = SearchIndex.InMemory<RuledDoc>();
        var titles = new List<string[]>();
        var bodies = new List<string[]>();
        var prices = new List<long>();
        for (int i = 0; i < docCount; i++)
        {
            string[] title = RandomTokens(rng, 5);
            string[] body = RandomTokens(rng, 18);
            titles.Add(title);
            bodies.Add(body);
            prices.Add(rng.Next(0, 6));
            index.Upsert(new RuledDoc { Id = "d" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), Title = string.Join(' ', title), Body = string.Join(' ', body), Price = prices[i] });
            if (i % 70 == 69)
            {
                index.Commit();
            }
        }

        index.Commit();
        int cases = 0;
        string[][] rulesSets =
        [
            ["words", "typo", "proximity", "attribute", "sort", "exactness"],
            ["proximity", "words", "attribute"],
            ["words", "Price:asc", "proximity"],
            ["Price:desc", "words", "attribute", "proximity"],
        ];
        foreach (string[] rules in rulesSets)
        {
            for (int q = 0; q < 12; q++)
            {
                string[] words = [.. Vocabulary.OrderBy(_ => rng.Next()).Take(rng.Next(1, 5))];
                List<SearchHit<RuledDoc>> hits = index.Search(string.Join(' ', words)).RankingRules(rules).Take(docCount).ToList();
                var expected = new List<(int Doc, long[] Key)>();
                for (int d = 0; d < docCount; d++)
                {
                    (int missing, int prox, long attribute) = Buckets(titles[d], bodies[d], words);
                    if (missing == words.Length)
                    {
                        continue;
                    }

                    var key = new List<long>();
                    foreach (string rule in rules)
                    {
                        switch (rule)
                        {
                            case "words": key.Add(missing); break;
                            case "typo": key.Add(0); break;
                            case "proximity": key.Add(prox); break;
                            case "attribute": key.Add(attribute); break;
                            case "exactness": key.Add(0); break;
                            case "Price:asc": key.Add(prices[d]); break;
                            case "Price:desc": key.Add(-prices[d]); break;
                        }
                    }

                    expected.Add((d, [.. key]));
                }

                // Without the words rule every word is required.
                if (!rules.Contains("words"))
                {
                    expected.RemoveAll(e => Buckets(titles[e.Doc], bodies[e.Doc], words).Missing > 0);
                }

                Assert.Equal(expected.Count, hits.Count);
                Assert.Equal(expected.Select(e => "d" + e.Doc.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal), hits.Select(h => h.Id).Order(StringComparer.Ordinal));
                var byId = expected.ToDictionary(e => "d" + e.Doc.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), e => e.Key);
                long[][] actual = [.. hits.Select(h => byId[h.Id])];
                long[][] sorted = [.. expected.Select(e => e.Key).OrderBy(k => k, Comparer<long[]>.Create(static (a, b) =>
                {
                    for (int i = 0; i < a.Length; i++)
                    {
                        int c = a[i].CompareTo(b[i]);
                        if (c != 0)
                        {
                            return c;
                        }
                    }

                    return 0;
                }))];
                for (int i = 0; i < actual.Length; i++)
                {
                    Assert.Equal(sorted[i], actual[i]);
                }

                cases++;
            }
        }

        Assert.Equal(48, cases);
    }

    [Fact]
    public void RankingRulesShowTheirBucketsInExplainAndHonourTheSortRule()
    {
        using SearchIndex<RuledDoc> index = SearchIndex.InMemory<RuledDoc>();
        index.Upsert(new RuledDoc { Id = "a", Title = "red apple", Body = "x", Price = 5 });
        index.Upsert(new RuledDoc { Id = "b", Title = "red apple", Body = "y", Price = 1 });
        index.Upsert(new RuledDoc { Id = "c", Title = "red", Body = "pie", Price = 0 });
        index.Commit();
        // The type declares the rules; OrderBy takes effect at the sort rule, so a price tie-break separates a and b.
        List<SearchHit<RuledDoc>> hits = index.Search("red apple").OrderBy(x => x.Price).Explain().ToList();
        Assert.Equal(["b", "a", "c"], hits.Select(h => h.Id));
        Assert.All(hits, h => Assert.Equal(h.Score, h.Explanation!.Value));
        string text = hits[0].Explanation!.ToString();
        Assert.Contains("words: 0 of 2 query words missing", text);
        Assert.Contains("proximity: 1 summed distance", text);
        // No ordering and no sort rule: relevance breaks the tie.
        Assert.Equal(["b", "a", "c"], index.Search("red apple").RankingRules("words", "Price:asc").ToList().Select(h => h.Id).Take(3));
        // Turning the rules off ranks by score with the proximity bonus.
        Assert.Equal(["a", "b"], index.Search("red apple").RankByRelevance().ToList().Select(h => h.Id));
        // A cursor continues the ordering.
        SearchHit<RuledDoc> first = index.Search("red apple").OrderBy(x => x.Price).Take(1).First();
        Assert.Equal("b", first.Id);
        Assert.NotNull(first.Cursor);
        List<SearchHit<RuledDoc>> rest = index.Search("red apple").OrderBy(x => x.Price).Take(5).SearchAfter(first.Cursor!).ToList();
        Assert.Equal(["a", "c"], rest.Select(h => h.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void QueryJsonCarriesTheProximityNodeAndTheRankingRules()
    {
        using SearchIndex<ProximityDoc> index = SearchIndex.InMemory<ProximityDoc>();
        // Rules keep the text, so that the words can be ranked after a round trip.
        QueryRequest request = QueryJson.Read("""{"v":1,"q":"red apple","rankingRules":["words","Price:asc"]}""", index.Schema);
        Assert.Equal(["words", "Price:asc"], request.RankingRules);
        Assert.Equal("red apple", request.Text);
        Assert.IsType<ProximityQuery>(request.Query);
        string written = QueryJson.Write(request);
        Assert.Equal("""{"v":1,"q":"red apple","rankingRules":["words","Price:asc"]}""", written);
        QueryRequest again = QueryJson.Read(written, index.Schema);
        Assert.Equal(request, again);
        Assert.Equal(written, QueryJson.Write(again));
        // Without rules a text request is written as its compiled tree, with the proximity node.
        QueryRequest plain = QueryJson.Read("""{"v":1,"q":"red apple"}""", index.Schema);
        string tree = QueryJson.Write(plain);
        Assert.Contains("\"type\":\"proximity\"", tree);
        Assert.Equal(plain, QueryJson.Read(tree, index.Schema));
        Assert.Equal(tree, QueryJson.Write(QueryJson.Read(tree, index.Schema)));
        using JsonDocument parsed = JsonDocument.Parse(tree);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        QueryParseException e = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"rankingRules":["words","bogus"]}""", index.Schema));
        Assert.Contains("$.rankingRules[1]", e.Message);
        _ = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"rankingRules":["words","words"]}""", index.Schema));
        _ = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"query":{"type":"proximity","query":{"type":"matchAll"},"words":[{"type":"matchAll"}]}}""", index.Schema));
    }
}
