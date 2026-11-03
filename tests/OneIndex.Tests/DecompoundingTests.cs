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

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    /// <summary>
    /// The hand-written samples in testdata/decompounding (compounds with accepted splits, and words that must stay whole) against the
    /// packed lists. The floors are set under the measured values (docs/decompounding.md).
    /// </summary>
    [Theory]
    [InlineData("de", 0.75, 0.60, 0.21)]
    [InlineData("nl", 0.75, 0.60, 0.21)]
    [InlineData("sv", 0.75, 0.60, 0.21)]
    [InlineData("da", 0.75, 0.60, 0.21)]
    [InlineData("no", 0.75, 0.60, 0.21)]
    [InlineData("fi", 0.75, 0.60, 0.21)]
    [InlineData("is", 0.75, 0.60, 0.21)]
    public void PackedListsSplitTheSample(string language, double minSplit, double minExact, double maxFalseSplit)
    {
        CompoundSplitter splitter;
        try
        {
            splitter = CompoundSplitter.ForLanguage(language);
        }
        catch (FileNotFoundException)
        {
            return; // built without the packed lists
        }

        string file = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "testdata", "decompounding", language + ".tsv");
        int compounds = 0, split = 0, exact = 0, whole = 0, falseSplit = 0;
        var parts = new List<(int Start, int Length)>();
        var misses = new List<string>();
        foreach (string line in File.ReadLines(file))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] cols = line.Split('\t');
            string word = cols[0];
            bool did = splitter.TrySplit(word, parts);
            string got = did ? string.Join('+', parts.Select(p => word.Substring(p.Start, p.Length))) : "-";
            if (cols[1] == "-")
            {
                whole++;
                if (did)
                {
                    falseSplit++;
                    misses.Add($"{word}: split {got}");
                }
            }
            else
            {
                compounds++;
                if (did)
                {
                    split++;
                }

                if (cols[1].Split('|').Contains(got))
                {
                    exact++;
                }
                else
                {
                    misses.Add($"{word}: {got} (wanted {cols[1]})");
                }
            }
        }

        string report = $"{language}: compounds {compounds}, split {split} ({split / (double)compounds:F3}), exact {exact} ({exact / (double)compounds:F3}); whole words {whole}, falsely split {falseSplit} ({falseSplit / (double)whole:F3})";
        if (Environment.GetEnvironmentVariable("ONEINDEX_DECOMPOUND_REPORT") is { Length: > 0 } reportFile)
        {
            File.AppendAllLines(reportFile, [report, .. misses.Select(m => "    " + m)]);
        }

        Assert.True(split / (double)compounds >= minSplit, report);
        Assert.True(exact / (double)compounds >= minExact, report);
        Assert.True(falseSplit / (double)whole <= maxFalseSplit, report);
    }
}
