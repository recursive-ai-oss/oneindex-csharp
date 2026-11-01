using System.Security.Cryptography;
using System.Text;
using OneIndex.Queries;
using OneIndex.Vectors;

namespace OneIndex.Tests;

/// <summary>A deterministic reranker that counts what it is asked to score.</summary>
public sealed class ScriptedReranker : IReranker
{
    private int _calls;
    private int _pairs;

    public int Calls => Volatile.Read(ref _calls);

    public int Pairs => Volatile.Read(ref _pairs);

    public bool Failing { get; set; }

    public bool NonFinite { get; set; }

    public string ModelId => "scripted/1";

    public static float ScoreOf(string query, string passage)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(query + "\0" + passage));
        return (BitConverter.ToUInt32(h, 0) / (float)uint.MaxValue * 10f) + (passage.Length % 7);
    }

    public void Score(string query, IReadOnlyList<string> passages, Span<float> scores, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        if (Failing)
        {
            throw new InvalidOperationException("the model is not available");
        }

        Interlocked.Add(ref _pairs, passages.Count);
        for (int i = 0; i < passages.Count; i++)
        {
            scores[i] = NonFinite ? float.NaN : ScoreOf(query, passages[i]);
        }
    }

    public ValueTask ScoreAsync(string query, IReadOnlyList<string> passages, Memory<float> scores, CancellationToken cancellationToken = default)
    {
        Score(query, passages, scores.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A type that asks for reranking: only the body is read.</summary>
public sealed class RerankDoc
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText(Boost = 2)]
    public string Title { get; set; } = "";

    [FullText(Rerank = true)]
    public string Body { get; set; } = "";

    [Filterable, Facet, Keyword]
    public string Group { get; set; } = "";

    [Filterable, Sortable]
    public long Rank { get; set; }
}

/// <summary>A type that does not ask: <c>Rerank()</c> reads every full-text field.</summary>
public sealed class RerankPlainDoc
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Title { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";
}

public sealed class RerankTests
{
    private static readonly string[] Words = ["river", "stone", "cloud", "ember", "quartz", "moss", "tide", "frost", "dune", "fern", "glass", "harbor"];

    private static SearchIndex<RerankDoc> Corpus(int seed, int n, IReranker? reranker)
    {
        SearchIndex<RerankDoc> index = SearchIndex.InMemory<RerankDoc>(null, null, reranker);
        var rng = new Random(seed);
        string Text(int count) => string.Join(' ', Enumerable.Range(0, count).Select(_ => Words[rng.Next(Words.Length)]));
        for (int i = 0; i < n; i++)
        {
            index.Upsert(new RerankDoc { Id = "d" + i, Title = Text(1 + rng.Next(3)), Body = Text(3 + rng.Next(30)), Group = "g" + rng.Next(4), Rank = rng.Next(100) });
        }

        index.Commit();
        return index;
    }

    private static (string Id, float Score)[] Direct(SearchIndex<RerankDoc> index, string text, int window, Func<SearchQuery<RerankDoc>, SearchQuery<RerankDoc>>? shape = null)
    {
        SearchQuery<RerankDoc> first = index.Search(text).WithoutRerank();
        first = shape is null ? first : shape(first);
        List<SearchHit<RerankDoc>> top = first.Take(window).ToList();
        return [.. top.Select((h, i) => (h.Id, Score: ScriptedReranker.ScoreOf(text, h.Document.Body), Rank: i)).OrderByDescending(static x => x.Score).ThenBy(static x => x.Rank).Select(static x => (x.Id, x.Score))];
    }

    [Fact]
    public void TheStageEqualsAScoringOfTheSameWindowByHand()
    {
        var reranker = new ScriptedReranker();
        int cases = 0, compared = 0;
        foreach (int seed in new[] { 1, 2, 3, 4 })
        {
            using SearchIndex<RerankDoc> index = Corpus(seed, 300, reranker);
            var rng = new Random(seed * 17);
            for (int c = 0; c < 15; c++)
            {
                string text = string.Join(' ', Enumerable.Range(0, 1 + rng.Next(2)).Select(_ => Words[rng.Next(Words.Length)]));
                int window = new[] { 5, 20, 60 }[rng.Next(3)];
                int take = 1 + rng.Next(window);
                string? group = rng.Next(2) == 0 ? null : "g" + rng.Next(4);
                SearchQuery<RerankDoc> Shape(SearchQuery<RerankDoc> q) => group is null ? q : q.Where(x => x.Group == group);

                (string Id, float Score)[] want = Direct(index, text, window, Shape);
                SearchResults<RerankDoc> got = Shape(index.Search(text)).Rerank(window).Take(take).Execute();
                Assert.Equal(Math.Min(take, want.Length), got.Count);
                for (int i = 0; i < got.Count; i++)
                {
                    Assert.Equal(want[i].Id, got[i].Id);
                    Assert.Equal(want[i].Score, got[i].Score);
                    Assert.NotNull(got[i].FirstStageScore);
                    compared++;
                }

                cases++;
            }
        }

        Assert.True(compared > 300);
        Assert.Equal(60, cases);
    }

    [Fact]
    public void FirstStageScoresAreTheScoresWithoutTheStage()
    {
        using SearchIndex<RerankDoc> index = Corpus(5, 200, new ScriptedReranker());
        Dictionary<string, float> before = index.Search("river stone").WithoutRerank().Take(30).ToList().ToDictionary(static h => h.Id, static h => h.Score);
        SearchResults<RerankDoc> after = index.Search("river stone").Rerank(30).Take(30).Execute();
        Assert.Equal(before.Count, after.Count);
        foreach (SearchHit<RerankDoc> h in after)
        {
            Assert.Equal(before[h.Id], h.FirstStageScore);
            Assert.NotEqual(h.Score, h.FirstStageScore);
        }

        Assert.All(index.Search("river stone").WithoutRerank().Take(3).ToList(), static h => Assert.Null(h.FirstStageScore));
    }

    [Fact]
    public void PagesAreSlicesOfOneOrderAndDeepPagesGrowTheWindow()
    {
        using SearchIndex<RerankDoc> index = Corpus(6, 300, new ScriptedReranker());
        string[] all = [.. index.Search("river").Rerank(50).Take(50).Execute().Select(static h => h.Id)];
        Assert.Equal(50, all.Length);
        Assert.Equal(all[10..20], index.Search("river").Rerank(50).Skip(10).Take(10).Execute().Select(static h => h.Id));

        // Past the window the stage reranks as many hits as the page needs: Skip 45 + Take 10 reads 55.
        string[] deep = [.. index.Search("river").Rerank(50).Skip(45).Take(10).Execute().Select(static h => h.Id)];
        string[] wide = [.. index.Search("river").Rerank(55).Take(55).Execute().Select(static h => h.Id)];
        Assert.Equal(wide[45..55], deep);
    }

    [Fact]
    public void FacetsAndTotalsCountAllMatchesNotTheWindow()
    {
        using SearchIndex<RerankDoc> index = Corpus(7, 400, new ScriptedReranker());
        SearchResults<RerankDoc> plain = index.Search("river").WithoutRerank().Facets(x => x.Group).Take(5).Execute();
        SearchResults<RerankDoc> reranked = index.Search("river").Facets(x => x.Group).Rerank(10).Take(5).Execute();
        Assert.True(plain.TotalHits > 10);
        Assert.Equal(plain.TotalHits, reranked.TotalHits);
        Assert.Equal(plain.Facets["Group"].Values.Select(static v => (v.Value, v.Count)), reranked.Facets["Group"].Values.Select(static v => (v.Value, v.Count)));
        Assert.True(reranked.Facets["Group"].Values.Sum(static v => v.Count) > 10);

        // The reranked hits are the best 10 of the first stage, reordered.
        HashSet<string> window = [.. index.Search("river").WithoutRerank().Facets(x => x.Group).Take(10).Execute().Select(static h => h.Id)];
        Assert.All(index.Search("river").Facets(x => x.Group).Rerank(10).Take(10).Execute(), h => Assert.Contains(h.Id, window));
    }

    [Fact]
    public void ATypeThatAsksRerankesPlainTextAutomaticallyAndATypeThatDoesNotIsLeftAlone()
    {
        var reranker = new ScriptedReranker();
        using SearchIndex<RerankDoc> asking = Corpus(8, 100, reranker);
        Assert.NotNull(asking.Search("river").Take(5).Execute()[0].FirstStageScore);
        Assert.Null(asking.Search("river").WithoutRerank().Take(5).Execute()[0].FirstStageScore);
        Assert.Null(asking.Search().Take(5).Execute()[0].FirstStageScore); // no search text: nothing to read
        Assert.Null(asking.SearchBy(new Search.TermQuery("Body", "river")).Take(5).Execute()[0].FirstStageScore); // query objects have no text either

        using SearchIndex<RerankPlainDoc> plain = SearchIndex.InMemory<RerankPlainDoc>(null, null, reranker);
        plain.Upsert(new RerankPlainDoc { Id = "a", Title = "river banks", Body = "the stone path" });
        plain.Upsert(new RerankPlainDoc { Id = "b", Title = "stone", Body = "river river" });
        plain.Commit();
        Assert.Null(plain.Search("river").Execute()[0].FirstStageScore);
        SearchResults<RerankPlainDoc> on = plain.Search("river").Rerank().Execute();
        Assert.All(on, static h => Assert.NotNull(h.FirstStageScore));
        float a = on.Single(static h => h.Id == "a").Score;
        Assert.Equal(ScriptedReranker.ScoreOf("river", "river banks the stone path"), a); // every full-text field, in order, joined by a space
    }

    [Fact]
    public void TheIndexDefaultAndAnExplicitRerankerAreUsed()
    {
        var def = new ScriptedReranker();
        var mine = new ScriptedReranker();
        SearchRerankers.Default = def;
        try
        {
            using SearchIndex<RerankDoc> index = SearchIndex.InMemory<RerankDoc>();
            index.Upsert(new RerankDoc { Id = "a", Body = "river" });
            index.Commit();
            _ = index.Search("river").Execute();
            Assert.Equal(1, def.Calls);
            _ = index.Search("river").Rerank(10, mine).Execute();
            Assert.Equal(1, mine.Calls);
            Assert.Equal(1, def.Calls);
        }
        finally
        {
            SearchRerankers.Default = null;
        }
    }

    [Fact]
    public void RerankNeedsARerankerTextAndNoOrdering()
    {
        using SearchIndex<RerankDoc> none = Corpus(9, 20, null);
        Assert.Throws<InvalidOperationException>(() => none.Search("river").Rerank().Execute());
        Assert.Null(none.Search("river").Take(2).Execute()[0].FirstStageScore); // asking by attribute needs a reranker too

        using SearchIndex<RerankDoc> index = Corpus(9, 20, new ScriptedReranker());
        Assert.Throws<InvalidOperationException>(() => index.Search().Rerank().Execute());
        Assert.Throws<NotSupportedException>(() => index.Search("river").OrderBy(x => x.Rank).Rerank().Execute());
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("river").Rerank(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("river").Rerank(10_001));
        // With the query given the text of a query-object search can be read.
        Assert.NotEmpty(index.SearchBy(new Search.TermQuery("Body", "river")).Rerank(10, query: "river").Execute());
    }

    [Fact]
    public void ExplainShowsTheStageAndKeepsTheFirstStageBreakdown()
    {
        using SearchIndex<RerankDoc> index = Corpus(10, 100, new ScriptedReranker());
        SearchHit<RerankDoc> hit = index.Search("river").Explain().Rerank(20).Take(3).Execute()[0];
        Assert.NotNull(hit.Explanation);
        Assert.Equal(hit.Score, hit.Explanation!.Value);
        Assert.Contains("rerank by scripted/1", hit.Explanation.Description);
        Assert.Single(hit.Explanation.Details);
        Assert.Equal(hit.FirstStageScore!.Value, hit.Explanation.Details[0].Value, 4);
    }

    [Fact]
    public async Task TheAsyncTwinGivesTheSameHitsAndFailuresAreTyped()
    {
        var reranker = new ScriptedReranker();
        using SearchIndex<RerankDoc> index = Corpus(11, 100, reranker);
        string[] sync = [.. index.Search("stone").Rerank(30).Take(10).Execute().Select(static h => h.Id)];
        SearchResults<RerankDoc> async = await index.Search("stone").Rerank(30).Take(10).ExecuteAsync();
        Assert.Equal(sync, async.Select(static h => h.Id));

        reranker.Failing = true;
        RerankException e = Assert.Throws<RerankException>(() => index.Search("stone").Rerank(30).Execute());
        Assert.Contains("scripted/1", e.Message);
        await Assert.ThrowsAsync<RerankException>(async () => await index.Search("stone").Rerank(30).ExecuteAsync());
        reranker.Failing = false;
        reranker.NonFinite = true;
        Assert.Throws<RerankException>(() => index.Search("stone").Rerank(30).Execute());
    }

    [Fact]
    public void ASearchWithNoMatchesDoesNotCallTheReranker()
    {
        var reranker = new ScriptedReranker();
        using SearchIndex<RerankDoc> index = Corpus(12, 50, reranker);
        Assert.Empty(index.Search("zzzzunknown").Rerank().Execute());
        Assert.Equal(0, reranker.Calls);
        Assert.Empty(index.Search("river").Rerank().Take(0).Execute()); // Take(0) is a count
        Assert.Equal(0, reranker.Calls);
    }

    [Fact]
    public void QueryJsonRerankRoundTripsAndRunsTheStage()
    {
        using SearchIndex<RerankDoc> index = Corpus(13, 200, new ScriptedReranker());
        string json = "{\"v\":1,\"q\":\"river stone\",\"rerank\":{\"window\":25},\"filter\":\"Group = g1\",\"limit\":5}";
        QueryRequest request = QueryJson.Read(json, index.Schema);
        Assert.Equal(new RerankRequest("river stone", 25), request.Rerank);
        Assert.Null(request.Query); // q is held for the reranker, as for hybrid
        string written = QueryJson.Write(request);
        Assert.Equal(request, QueryJson.Read(written, index.Schema));
        Assert.Equal(written, QueryJson.Write(QueryJson.Read(written, index.Schema)));

        SearchResults<RerankDoc> viaJson = index.Parse(json).Execute();
        SearchResults<RerankDoc> direct = index.Search("river stone").Where(x => x.Group == "g1").Rerank(25).Take(5).Execute();
        Assert.Equal(direct.Select(static h => h.Id), viaJson.Select(static h => h.Id));
        Assert.Equal(5, viaJson.Count);
        Assert.All(viaJson, static h => Assert.NotNull(h.FirstStageScore));

        // The default window is not written; an empty object is valid.
        Assert.Equal(new RerankRequest("x", 100), QueryJson.Read("{\"v\":1,\"q\":\"x\",\"rerank\":{}}", index.Schema).Rerank);
        Assert.DoesNotContain("window", QueryJson.Write(QueryJson.Read("{\"v\":1,\"q\":\"x\",\"rerank\":{}}", index.Schema)));

        QueryParseException noQ = Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\"rerank\":{}}", index.Schema));
        Assert.Equal("$.rerank", noQ.Path);
        QueryParseException unknown = Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\"q\":\"x\",\"rerank\":{\"windo\":3}}", index.Schema));
        Assert.Contains("window", unknown.Message);
        Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\"q\":\"x\",\"rerank\":{\"window\":0}}", index.Schema));
        Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\"q\":\"x\",\"rerank\":true}", index.Schema));

        Assert.Contains("\"rerank\"", SearchTool.For<RerankDoc>());
    }
}
