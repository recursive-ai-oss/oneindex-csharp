using OneIndex.Analysis;
using OneIndex.Search;

namespace OneIndex.Tests;

public class StopWordConfigurationTests
{
    private static readonly string[] Nyc = ["nyc"];

    private static string[] Terms(Analyzer a, string text) => [.. a.Analyze(text).Select(t => t.Term)];

    [Fact]
    public void DefaultListStillApplies()
    {
        var en = new LanguageAnalyzer("en");
        Assert.Equal(["great", "gatsbi"], Terms(en, "the great gatsby"));
    }

    [Fact]
    public void ReplaceDropsOnlyTheConfiguredWords()
    {
        var en = new LanguageAnalyzer("en", false, new StopWordSettings(["Foo"]));
        Assert.Equal(["the", "bar"], Terms(en, "the FOO bar"));
    }

    [Fact]
    public void EmptyReplaceTurnsStopWordsOff()
    {
        var en = new LanguageAnalyzer("en", false, new StopWordSettings([]));
        Assert.Equal(["the", "bar"], Terms(en, "the bar"));
    }

    [Fact]
    public void ExtendAddsToTheLanguageList()
    {
        var en = new LanguageAnalyzer("en", false, new StopWordSettings(["foo"]) { Mode = StopWordMode.Extend });
        Assert.Equal(["bar"], Terms(en, "the foo bar"));
    }

    [Fact]
    public void ExtendOnALanguageWithoutAListUsesTheConfiguredWords()
    {
        var pl = new LanguageAnalyzer("pl", false, new StopWordSettings(["xyz"]) { Mode = StopWordMode.Extend });
        Assert.DoesNotContain("xyz", Terms(pl, "abc xyz"));
    }

    [Fact]
    public void PositionsCollapseByDefaultAndKeepGapsOnRequest()
    {
        var collapsed = new LanguageAnalyzer("en", false, new StopWordSettings(["of"]));
        Assert.Equal([0, 1], collapsed.Analyze("king of queens").Select(t => t.Position));
        var gaps = new LanguageAnalyzer("en", false, new StopWordSettings(["of"]) { PreserveGaps = true });
        Assert.Equal([0, 2], gaps.Analyze("king of queens").Select(t => t.Position));
    }

    // Meilisearch: stop words are ignored inside phrase searches too ("the great gatsby" matches what "great gatsby" matches).
    [Fact]
    public void StopWordsAreIgnoredInsidePhrases()
    {
        var an = new LanguageAnalyzer("en", false, new StopWordSettings(["the", "of"]));
        Query with = TokenGraphQueries.ToPhrase(TokenGraph.FromText(an, "the great gatsby of the south"), "f");
        Query without = TokenGraphQueries.ToPhrase(TokenGraph.FromText(an, "great gatsby south"), "f");
        Assert.Equal(without, with);
        string[] doc = [.. an.Analyze("The Great Gatsby of the south, a novel").Select(t => t.Term)];
        Assert.True(MiniEval.Matches(with, doc));
        string[] other = [.. an.Analyze("A great gatsby south").Select(t => t.Term)];
        Assert.True(MiniEval.Matches(with, other), "text that never had the stop words matches too: they are ignored");
    }

    [Fact]
    public void PreservedGapsBecomeSlop()
    {
        var an = new LanguageAnalyzer("en", false, new StopWordSettings(["of"]) { PreserveGaps = true });
        var q = Assert.IsType<PhraseQuery>(TokenGraphQueries.ToPhrase(TokenGraph.FromText(an, "king of queens"), "f"));
        Assert.Equal(1, q.Slop);
    }

    // Documented: a query of only stop words has no terms, and builds the match-all query (an empty search returns every document).
    [Fact]
    public void AllStopWordQueriesMatchEverything()
    {
        var an = new LanguageAnalyzer("en", false, new StopWordSettings(["the", "a"]));
        Assert.IsType<MatchAllQuery>(TokenGraphQueries.ToWords(TokenGraph.FromText(an, "the a"), "f"));
        Assert.IsType<MatchAllQuery>(TokenGraphQueries.ToPhrase(TokenGraph.FromText(an, "The"), "f"));
    }

    [Fact]
    public void IndexAndQuerySideAgree()
    {
        // The same analyzer runs on both sides, so a word that is a stop word is never indexed and never queried.
        var an = new LanguageAnalyzer("de", false, new StopWordSettings(["haus"]) { Mode = StopWordMode.Extend });
        Assert.DoesNotContain("haus", Terms(an, "das Haus am See"));
        Assert.Equal(Terms(an, "am See"), Terms(an, "das Haus am See"));
    }

    [Fact]
    public void StopFilterWorksInACustomChain()
    {
        var settings = new StopWordSettings(["and"]);
        var an = new CustomAnalyzer("c", () => new StandardTokenizer(), s => new CaseFoldFilter(s), s => settings.CreateFilter(s, null));
        Assert.Equal(["salt", "pepper"], Terms(an, "Salt AND pepper"));
    }

    [Fact]
    public void SynonymsRespectStopWords()
    {
        var an = new LanguageAnalyzer("en", false, new StopWordSettings(["the"]));
        SynonymMap map = new SynonymRules().AddOneWay("the big apple", "nyc").Build(an);
        TokenGraph g = TokenGraph.FromText(an, "big apple", map);
        Assert.Contains(g.Edges, e => e.IsSynonym && e.Terms.SequenceEqual(Nyc) && e.From == 0 && e.To == 2);
    }
}
