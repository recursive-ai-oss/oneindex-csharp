using OneIndex.Analysis;

namespace OneIndex.Tests;

/// <summary>Corpus-statistics decompounding and its token filter.</summary>
public class DecompoundingTests
{
    private static CompoundSplitter German(bool frequencies)
    {
        CompoundVocabulary v = frequencies
            ? CompoundVocabulary.FromFrequencies([("haus", 900), ("tür", 500), ("liebe", 800), ("brief", 700), ("haustür", 3), ("liebesbrief", 2), ("bahn", 400), ("hof", 300), ("bahnhof", 600), ("park", 100), ("ett", 2)])
            : CompoundVocabulary.FromWords(["haus", "tür", "liebe", "brief", "bahn", "hof"]);
        return new CompoundSplitter(v, new CompoundOptions { LinkingElements = CompoundOptions.For("de").LinkingElements, RequirePartsMoreFrequentThanWord = frequencies });
    }

    private static string[] Split(CompoundSplitter s, string word)
    {
        var parts = new List<(int Start, int Length)>();
        return s.TrySplit(word, parts) ? [.. parts.Select(p => word.Substring(p.Start, p.Length))] : [];
    }

    [Fact]
    public void SplitsAtVocabularyWordsAndLinkingElements()
    {
        CompoundSplitter s = German(false);
        Assert.Equal(["Haus", "tür"], Split(s, "Haustür"));
        Assert.Equal(["Liebe", "brief"], Split(s, "Liebesbrief"));
        Assert.Equal(["HAUS", "TÜR"], Split(s, "HAUSTÜR"));
        Assert.Empty(Split(s, "Haus"));
        Assert.Empty(Split(s, "Fahrrad"));
    }

    [Fact]
    public void FrequentWholeWordsStayWhole()
    {
        CompoundSplitter s = German(true);
        Assert.Empty(Split(s, "Bahnhof")); // the compound is more frequent than the geometric mean of its parts
        Assert.Equal(["Haus", "tür"], Split(s, "Haustür")); // rare compound, frequent parts
        Assert.Empty(Split(new CompoundSplitter(CompoundVocabulary.FromFrequencies([("park", 100), ("ett", 2)]), new CompoundOptions { MinPartLength = 4 }), "Parkett")); // parts shorter than the minimum are not tried
    }

    [Fact]
    public void FilterEmitsCompoundThenPartsAtOnePosition()
    {
        var analyzer = new CustomAnalyzer("test", () => new StandardTokenizer(), s => new CaseFoldFilter(s), s => new DecompoundFilter(s, German(false)));
        IReadOnlyList<AnalyzedToken> tokens = analyzer.Analyze("Die Haustür");
        Assert.Equal(["die", "haustür", "haus", "tür"], tokens.Select(t => t.Term));
        Assert.Equal([0, 1, 1, 1], tokens.Select(t => t.Position));
        Assert.All(tokens.Skip(1), t => Assert.Equal((4, 11), (t.StartOffset, t.EndOffset)));
        Assert.Equal(TokenType.Synonym, tokens[2].Type);
    }

    [Fact]
    public void NeverFailsOnArbitraryWords()
    {
        CompoundSplitter s = German(true);
        var rng = new Random(3);
        var parts = new List<(int, int)>();
        for (int i = 0; i < 2000; i++)
        {
            string w = string.Concat(Enumerable.Range(0, rng.Next(0, 40)).Select(_ => "haustürliebebrief\uD83D\uDE00s"[rng.Next(18)]));
            if (s.TrySplit(w, parts))
            {
                Assert.True(parts.Count >= 2 && parts.All(p => p.Item2 >= 3 && p.Item1 + p.Item2 <= w.Length));
            }
        }
    }
}
