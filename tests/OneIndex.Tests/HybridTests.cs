using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.Tests;

public sealed class HybridTests
{
    private static readonly string[] Words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa"];

    private static SearchIndex<EmbNote> Corpus(int seed, int count, CountingEmbedder embedder)
    {
        var rng = new Random(seed);
        var index = SearchIndex.InMemory<EmbNote>(embedder);
        var notes = new List<EmbNote>();
        for (int i = 0; i < count; i++)
        {
            string body = string.Join(' ', Enumerable.Range(0, 3 + rng.Next(8)).Select(_ => Words[rng.Next(Words.Length)])) + " uid" + i;
            notes.Add(new EmbNote { Id = "n" + i, Body = body, Group = "g" + rng.Next(4) });
            if (i % 70 == 69)
            {
                index.Upsert(notes);
                notes.Clear();
                index.Commit(); // several segments
            }
        }

        index.Upsert(notes);
        index.Commit();
        return index;
    }

    private static List<(string Id, float Score)> Arm(IEnumerable<SearchHit<EmbNote>> hits) => [.. hits.Select(static h => (h.Id, h.Score))];

    private static Dictionary<string, double> Rrf(List<(string Id, float Score)> text, List<(string Id, float Score)> vector, int k, double ratio)
    {
        var fused = new Dictionary<string, double>();
        for (int i = 0; i < text.Count; i++)
        {
            fused[text[i].Id] = fused.GetValueOrDefault(text[i].Id) + (2 * (1 - ratio) / (k + i + 1));
        }

        for (int i = 0; i < vector.Count; i++)
        {
            fused[vector[i].Id] = fused.GetValueOrDefault(vector[i].Id) + (2 * ratio / (k + i + 1));
        }

        return fused;
    }

    private static Dictionary<string, double> Linear(List<(string Id, float Score)> text, List<(string Id, float Score)> vector, double ratio)
    {
        static double Norm(List<(string Id, float Score)> l, int i)
        {
            float min = l.Min(static x => x.Score), max = l.Max(static x => x.Score);
            return max > min ? (l[i].Score - min) / (max - min) : 1.0;
        }

        var fused = new Dictionary<string, double>();
        for (int i = 0; i < text.Count; i++)
        {
            fused[text[i].Id] = fused.GetValueOrDefault(text[i].Id) + ((1 - ratio) * Norm(text, i));
        }

        for (int i = 0; i < vector.Count; i++)
        {
            fused[vector[i].Id] = fused.GetValueOrDefault(vector[i].Id) + (ratio * Norm(vector, i));
        }

        return fused;
    }

    [Fact]
    public void FusionEqualsADirectComputationOverTheTwoArms()
    {
        var embedder = new CountingEmbedder();
        int cases = 0, hitsCompared = 0;
        foreach (int seed in new[] { 1, 2, 3 })
        {
            using SearchIndex<EmbNote> index = Corpus(seed, 300, embedder);
            var rng = new Random(seed * 31);
            for (int c = 0; c < 12; c++)
            {
                string text = string.Join(' ', Enumerable.Range(0, 1 + rng.Next(3)).Select(_ => Words[rng.Next(Words.Length)]));
                string? group = rng.Next(2) == 0 ? null : "g" + rng.Next(4);
                int window = new[] { 10, 25, 50 }[rng.Next(3)];
                double ratio = new[] { 0.5, 0.3, 0.75 }[rng.Next(3)];
                int rank = new[] { 60, 10, 5 }[rng.Next(3)];
                float[] qv = CountingEmbedder.VectorOf(text);

                SearchQuery<EmbNote> Filtered(SearchQuery<EmbNote> q) => group is null ? q : q.Where(x => x.Group == group);
                List<(string Id, float Score)> textArm = Arm(Filtered(index.Search(text).Hybrid(0)).Take(window).ToList());
                List<(string Id, float Score)> vectorArm = Arm(Filtered(index.SearchVector(x => x.Embedding, qv, window)).Take(window).ToList());
                Assert.True(vectorArm.Count > 0);

                foreach (FusionMethod method in new[] { FusionMethod.ReciprocalRank, FusionMethod.Linear })
                {
                    List<SearchHit<EmbNote>> got = [.. Filtered(index.Search(text).Hybrid(ratio, method, rank, window)).Take(2 * window).ToList()];
                    Dictionary<string, double> want = method == FusionMethod.ReciprocalRank ? Rrf(textArm, vectorArm, rank, ratio) : Linear(textArm, vectorArm, ratio);
                    Assert.Equal(want.Count, got.Count);
                    foreach (SearchHit<EmbNote> h in got)
                    {
                        Assert.True(want.TryGetValue(h.Id, out double expected), $"{h.Id} is not in either arm (case {cases})");
                        Assert.InRange(Math.Abs(h.Score - expected), 0, 1e-5);
                        if (group is not null)
                        {
                            Assert.Equal(group, h.Document.Group);
                        }

                        hitsCompared++;
                    }

                    for (int i = 1; i < got.Count; i++)
                    {
                        Assert.True(got[i - 1].Score >= got[i].Score);
                    }

                    cases++;
                }
            }
        }

        Assert.Equal(72, cases);
        Assert.True(hitsCompared > 1000);
    }

    [Fact]
    public void TextSearchOnAnIndexWithAnEmbedderIsHybridAndRatioZeroIsNot()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = Corpus(7, 120, embedder);
        Assert.IsType<HybridQuery>(index.Search("alpha bravo").ToQuery());
        Assert.IsNotType<HybridQuery>(index.Search("alpha bravo").Hybrid(0).ToQuery());
        Assert.IsNotType<HybridQuery>(index.Search().ToQuery()); // blank text matches everything
        Assert.IsNotType<HybridQuery>(index.SearchBy(new TermQuery("Body", "alpha")).ToQuery());
        // Ratio 1 ranks like the vector search alone.
        List<string> semantic = index.Search("alpha bravo").Hybrid(1.0).Take(10).ToList().Select(static h => h.Id).ToList();
        List<string> vector = index.SearchVector(x => x.Embedding, CountingEmbedder.VectorOf("alpha bravo"), 100).Take(10).ToList().Select(static h => h.Id).ToList();
        Assert.Equal(vector, semantic);
        // Ratio 0 ranks like the text search alone.
        Assert.Equal(
            index.Search("alpha bravo").Hybrid(0).Take(10).ToList().Select(static h => h.Id),
            index.SearchBy(new UserTextCompiler(index.Schema).Compile("alpha bravo")).Take(10).ToList().Select(static h => h.Id));
    }

    [Fact]
    public void FiltersFacetsSortAndPagingCompose()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = Corpus(9, 300, embedder);
        SearchResults<EmbNote> r = index.Search("delta echo").Where(x => x.Group == "g2").Facets(x => x.Group).Take(15).Skip(5).Execute();
        Assert.Equal(15, r.Count);
        Assert.All(r, h => Assert.Equal("g2", h.Document.Group));
        Assert.Equal("g2", Assert.Single(r.Facets["Group"].Values).Key.ToString());
        // Pages are consecutive slices of the same ranking.
        List<string> all = index.Search("delta echo").Where(x => x.Group == "g2").Take(20).ToList().Select(static h => h.Id).ToList();
        Assert.Equal(all.Skip(5), r.Select(static h => h.Id));
        // Sorting orders the fused matches.
        List<SearchHit<EmbNote>> sorted = index.Search("delta echo").Where(x => x.Group != "g0").OrderBy(x => x.Id).Take(30).ToList();
        Assert.Equal(30, sorted.Count);
        Assert.Equal(sorted.Select(static h => h.Id).OrderBy(static x => x, StringComparer.Ordinal), sorted.Select(static h => h.Id));
        // TotalHits counts the documents of both windows: at most twice the window.
        SearchResults<EmbNote> counted = index.Search("delta echo").Hybrid(window: 20).Take(5).Execute();
        Assert.InRange(counted.TotalHits, 20, 40);
    }

    [Fact]
    public void AVectorYouComputedNeedsNoEmbedder()
    {
        using SearchIndex<EmbNote> index = Corpus(3, 100, new CountingEmbedder());
        // Reopen the same kind of index without an embedder is not possible in memory; give the vector explicitly instead.
        float[] qv = CountingEmbedder.VectorOf("kilo lima");
        List<SearchHit<EmbNote>> hits = index.Search("kilo lima").Hybrid(x => x.Embedding, qv).Take(5).ToList();
        Assert.Equal(5, hits.Count);
        Assert.Throws<ArgumentException>(() => index.Search("kilo").Hybrid(x => x.Embedding, new float[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("kilo").Hybrid(1.5));
    }

    [Fact]
    public void ExplainNamesTheRankInEachList()
    {
        using SearchIndex<EmbNote> index = Corpus(4, 100, new CountingEmbedder());
        SearchHit<EmbNote> top = index.Search("mike november").Explain().Take(1).ToList().Single();
        Assert.NotNull(top.Explanation);
        Assert.Contains("reciprocal rank fusion", top.Explanation!.Description);
        Assert.Equal(top.Score, top.Explanation.Value, 5);
    }

    [Fact]
    public void QueryJsonRoundTripsHybridNodesAndRequests()
    {
        Schema schema = SearchMappings.Require<EmbNote>().Schema;
        string node = """{"v":1,"query":{"type":"hybrid","text":{"type":"term","field":"Body","value":"alpha"},"vector":{"type":"knn","field":"Embedding","vector":[1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"k":5},"fusion":"linear","rankConstant":20,"semanticRatio":0.25,"window":40}}""";
        QueryRequest r = QueryJson.Read(node, schema);
        var h = Assert.IsType<HybridQuery>(r.Query);
        Assert.Equal(FusionMethod.Linear, h.Fusion);
        Assert.Equal(20, h.RankConstant);
        Assert.Equal(0.25, h.SemanticRatio);
        Assert.Equal(40, h.Window);
        string written = QueryJson.Write(r);
        Assert.Equal(r, QueryJson.Read(written, schema));
        Assert.Equal(written, QueryJson.Write(QueryJson.Read(written, schema)));

        QueryRequest req = QueryJson.Read("""{"v":1,"q":"alpha bravo","hybrid":{"semanticRatio":0.7,"fusion":"rrf","window":30,"field":"Embedding"},"filter":"Group = g1","limit":5}""", schema);
        Assert.Equal(new HybridRequest("alpha bravo", 0.7, FusionMethod.ReciprocalRank, 10, 30, "Embedding"), req.Hybrid);
        Assert.Null(req.Query);
        Assert.Equal(req, QueryJson.Read(QueryJson.Write(req), schema));

        QueryParseException e = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"hybrid":{}}""", schema));
        Assert.Contains("q", e.Message);
        e = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"q":"a","hybrid":{"semanticRatio":2}}""", schema));
        Assert.Contains("semanticRatio", e.Path ?? e.Message);
        e = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"q":"a","hybrid":{"fusoin":"rrf"}}""", schema));
        Assert.Contains("fusion", e.Message);
    }

    [Fact]
    public void ParseRunsAHybridRequestThroughTheEmbedder()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = Corpus(5, 150, embedder);
        SearchQuery<EmbNote> search = index.Parse("""{"v":1,"q":"golf hotel","hybrid":{"fusion":"rrf"},"filter":"Group = g1","limit":8}""");
        Assert.IsType<HybridQuery>(search.ToQuery());
        List<SearchHit<EmbNote>> hits = search.ToList();
        Assert.Equal(8, hits.Count);
        Assert.All(hits, h => Assert.Equal("g1", h.Document.Group));
        Assert.Equal(
            index.Search("golf hotel").Where(x => x.Group == "g1").Take(8).ToList().Select(static h => h.Id),
            hits.Select(static h => h.Id));
    }

    [Fact]
    public void SearchToolOffersHybridWhenTheSchemaHasAVectorField()
    {
        string tool = SearchTool.For<EmbNote>();
        Assert.Contains("semanticRatio", tool);
    }
}
