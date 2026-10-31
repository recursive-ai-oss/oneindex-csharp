using OneIndex.Harness;

namespace OneIndex.Relevance;

public class HarnessTests
{
    private static readonly Dictionary<string, Dictionary<string, int>> Qrels = new()
    {
        ["qa"] = new() { ["d1"] = 1, ["d2"] = 2 },
    };

    private static Dictionary<string, List<(string Doc, double Score)>> Run(params (string Doc, double Score)[] hits) =>
        new() { ["qa"] = [.. hits] };

    [Fact]
    public void NdcgMatchesAHandComputedValue()
    {
        // DCG = 2/log2(2) + 1/log2(4) = 2.5; ideal = 2/log2(2) + 1/log2(3).
        var (mean, queries) = TrecEval.NdcgAt(10, Qrels, Run(("d2", 3), ("d3", 2), ("d1", 1)));
        Assert.Equal(1, queries);
        Assert.Equal(2.5 / (2 + (1 / Math.Log2(3))), mean, 9);
    }

    [Fact]
    public void ScoreTiesBreakByDocumentIdDescendingLikeTrecEval()
    {
        // d2 and d1 tie; d2 (the larger id) ranks first, so the grade-2 document gets the full discount.
        var (tied, _) = TrecEval.NdcgAt(10, Qrels, Run(("d1", 1), ("d2", 1)));
        var (ordered, _) = TrecEval.NdcgAt(10, Qrels, Run(("d2", 2), ("d1", 1)));
        Assert.Equal(ordered, tied, 12);
    }

    [Fact]
    public void QueriesWithoutJudgementsAreIgnoredAndTheCutoffApplies()
    {
        var run = Run(("d3", 9), ("d1", 1));
        run["other"] = [("d1", 1)];
        var (mean, queries) = TrecEval.NdcgAt(1, Qrels, run);
        Assert.Equal(1, queries);
        Assert.Equal(0, mean);
    }

    [Fact]
    public void RunFilesRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oneindex-run-{Guid.NewGuid():N}.run");
        try
        {
            using (var w = new StreamWriter(path))
            {
                TrecEval.WriteRun(w, "qa", [new Hit("d2", 1.5f), new Hit("d1", 0.25f)], "t");
            }

            var read = TrecEval.ReadRun(path);
            Assert.Equal([("d2", 1.5), ("d1", 0.25)], read["qa"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Confirms the Lucene.NET comparator builds and answers every query kind on net10.0.</summary>
public class LuceneComparatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"oneindex-lucene-{Guid.NewGuid():N}");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void BeirProfileStemsAndScoresEveryLayout()
    {
        using var engine = new LuceneEngine();
        engine.Create(new EngineSettings(_dir, AnalysisKind.English, IndexProfile.Beir));
        engine.Add(new Doc("a", "Running shoes", "A guide to buying shoes for the runner."));
        engine.Add(new Doc("b", "Cooking", "Pasta and sauce recipes."));
        engine.Commit();
        engine.OpenSearcher();
        foreach (var layout in Enum.GetValues<FieldLayout>())
        {
            var result = engine.Search(new MatchQuery("the runners run", layout), 10);
            Assert.Equal(["a"], result.Hits.Select(h => h.Id));
            Assert.True(result.Hits[0].Score > 0);
        }

        engine.SetBm25(0.9f, 0.4f);
        Assert.Single(engine.Search(new MatchQuery("pasta", FieldLayout.Flat), 10).Hits);
    }

    [Fact]
    public void BenchProfileAnswersEveryRow()
    {
        using var engine = new LuceneEngine();
        engine.Create(new EngineSettings(_dir, AnalysisKind.Standard, IndexProfile.Bench));
        engine.Add(new Doc("1", "", "the quick brown fox jumps", 25, "Q"));
        engine.Add(new Doc("2", "", "quick brown dogs", 16, "Q"));
        engine.Add(new Doc("3", "", "a lazy fox", 10, "L"));
        engine.Commit();
        engine.OpenSearcher();

        Assert.Equal(2, engine.Search(new TermsQuery(["quick"]), 10).Total);
        Assert.Equal(3, engine.Search(new TermsQuery(["quick", "fox"]), 10).Total);
        Assert.Equal(2, engine.Search(new PhraseQuery(["quick", "brown"]), 10).Total);
        Assert.Equal(1, engine.Search(new PhraseQuery(["brown", "fox"]), 10).Total);
        Assert.Equal(2, engine.Search(new PrefixQuery([], "qui"), 10).Total);
        Assert.Equal(1, engine.Search(new PrefixQuery(["brown"], "jum"), 10).Total);
        Assert.Equal(2, engine.Search(new FuzzyQuery("quack", 2), 10).Total);
        Assert.Equal(1, engine.Search(new FilteredQuery(["fox"], 20, 30, ["Q"]), 10).Total);

        var facets = engine.Search(new FacetQuery(["fox"]), 10).Facets!;
        Assert.Equal([("L", 1L), ("Q", 1L)], facets.OrderBy(f => f.Value).ToArray());
        Assert.Equal((2L, 10L, 25L, 35L), engine.Search(new StatsQuery(["fox"]), 10).Stats);
        Assert.Equal(["1", "3"], engine.Search(new SortedQuery(["fox"]), 10).Hits.Select(h => h.Id));
        Assert.True(engine.IndexSizeBytes() > 0);
    }
}

public class OneIndexAdapterTests
{
    [Fact]
    public void ReportsUnavailableUntilTheEngineLands()
    {
        using var engine = new OneIndexEngine();
        Assert.False(engine.IsAvailable(out var reason));
        Assert.NotEmpty(reason);
        Assert.Contains("lucene", Engines.Available());
        Assert.DoesNotContain("oneindex", Engines.Available());
    }
}
