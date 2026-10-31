using OneIndex.Analysis;

namespace OneIndex.Tests;

[Collection(AllocationMeasurement.Name)]
public class TextAnalysisTests
{
    private static string[] Terms(string analyzer, string text)
        => AnalyzerRegistry.Create(analyzer).Analyze(text).Select(t => t.Term).ToArray();

    [Fact]
    public void StandardIsUnicodeWordsAndCaseFolding()
    {
        Assert.Equal(["hello", "world", "it's", "3.14", "e", "mail", "foo_bar", "strasse"], Terms("standard", "Hello, World! It's 3.14 e-mail FOO_bar Straße"));
        Assert.Equal(["don't"], Terms("standard", "Don't"));
        Assert.Empty(Terms("standard", " ,.;-- \t\r\n _ "));
        Assert.Equal(["日", "本", "語"], Terms("standard", "日本語"));
    }

    [Fact]
    public void StandardKeepsOffsetsAndPositions()
    {
        IReadOnlyList<AnalyzedToken> tokens = AnalyzerRegistry.Create("standard").Analyze("Ab, cD  ef");
        Assert.Equal([(0, 2), (4, 6), (8, 10)], tokens.Select(t => (t.StartOffset, t.EndOffset)));
        Assert.Equal([0, 1, 2], tokens.Select(t => t.Position));
        Assert.Equal([TokenType.Word, TokenType.Word, TokenType.Word], tokens.Select(t => t.Type));
        Assert.Equal(TokenType.Number, AnalyzerRegistry.Create("standard").Analyze("2024")[0].Type);
        Assert.Equal(TokenType.Word, AnalyzerRegistry.Create("standard").Analyze("abc123")[0].Type);
    }

    [Fact]
    public void LongWordsAreSplitWithoutCuttingSurrogatePairs()
    {
        string text = new string('a', 600);
        Assert.Equal([255, 255, 90], AnalyzerRegistry.Create("standard").Analyze(text).Select(t => t.Term.Length));
        string pairs = string.Concat(Enumerable.Repeat("\U0001D400", 200)); // mathematical bold A, a letter outside the BMP
        foreach (AnalyzedToken t in AnalyzerRegistry.Create("standard").Analyze(pairs))
        {
            Assert.False(char.IsHighSurrogate(t.Term[^1]));
        }
    }

    [Fact]
    public void EnglishStopsStemsAndFolds()
    {
        Assert.Equal(["run", "dog", "cat", "quick", "cafe"], Terms("en", "The running dogs and the cats quickly Café"));
        IReadOnlyList<AnalyzedToken> tokens = AnalyzerRegistry.Create("en").Analyze("the quick dogs");
        Assert.Equal([1, 2], tokens.Select(t => t.Position)); // "the" left a gap
    }

    [Fact]
    public void BothKeepsTheWordAndAddsItsStemAtTheSamePosition()
    {
        IReadOnlyList<AnalyzedToken> tokens = AnalyzerRegistry.Create("en-both").Analyze("running dog");
        Assert.Equal(["running", "run", "dog"], tokens.Select(t => t.Term));
        Assert.Equal([0, 0, 1], tokens.Select(t => t.Position));
        Assert.Equal(TokenType.Synonym, tokens[1].Type);
        Assert.Equal(TokenMarks.Stemmed, tokens[1].Marks);
        Assert.Equal(TokenMarks.None, tokens[0].Marks);
        Assert.Equal((0, 7), (tokens[1].StartOffset, tokens[1].EndOffset));
    }

    [Fact]
    public void OtherLanguages()
    {
        Assert.Equal(["avion"], Terms("fr", "l\u2019avions"));
        Assert.Equal(["arte"], Terms("it", "dell'arte"));
        Assert.Equal(["kind", "kind"], Terms("de", "Kinder Kindern"));
        Assert.Equal(["привет", "мир"], Terms("ru", "Привет миру"));
        Assert.StartsWith("ıs", Terms("tr", "ISI")[0], StringComparison.Ordinal); // capital I folds to dotless ı
    }

    [Fact]
    public void CjkMakesBigrams()
    {
        Assert.Equal(["日本", "本語", "でし", "した"], Terms("cjk", "日本語でした"));
        Assert.Equal(["日本", "本語", "abc", "한국", "국어"], Terms("cjk", "日本語 abc 한국어"));
        Assert.Equal(["日"], Terms("cjk", "日"));
        IReadOnlyList<AnalyzedToken> t = AnalyzerRegistry.Create("cjk").Analyze("日本語");
        Assert.Equal([(0, 2), (1, 3)], t.Select(x => (x.StartOffset, x.EndOffset)));
        Assert.Equal([0, 1], t.Select(x => x.Position));
        Assert.Equal(["abc"], Terms("cjk", "ＡＢＣ"));
    }

    [Fact]
    public void EveryRegisteredNameCreatesAndNeverThrowsOnOddInput()
    {
        var random = new Random(29);
        string[] names = [.. new[] { "standard", "cjk", "keyword", "whitespace-lowercase" }, .. LanguageAnalyzer.LanguageCodes, .. LanguageAnalyzer.LanguageCodes.Select(c => c + "-both")];
        foreach (string name in names)
        {
            Analyzer analyzer = AnalyzerRegistry.Create(name);
            Assert.Equal(name, analyzer.Name);
            for (int i = 0; i < 40; i++)
            {
                var chars = new char[random.Next(0, 80)];
                for (int k = 0; k < chars.Length; k++)
                {
                    chars[k] = random.Next(4) switch
                    {
                        0 => (char)random.Next(0xD800, 0xE000), // lone surrogates
                        1 => (char)random.Next(0x300, 0x370),
                        2 => (char)random.Next(0x3040, 0xD7FF),
                        _ => (char)random.Next(32, 0x250),
                    };
                }

                string text = new(chars);
                int last = -1;
                foreach (AnalyzedToken t in analyzer.Analyze(text))
                {
                    Assert.InRange(t.StartOffset, 0, text.Length);
                    Assert.InRange(t.EndOffset, t.StartOffset, text.Length);
                    Assert.True(t.Position >= last);
                    last = t.Position;
                }
            }
        }
    }

    [Fact]
    public void StandardAndEnglishAllocateNothingPerToken()
    {
        string text = string.Join(' ', Enumerable.Repeat("The quick brown Foxes jumped over 3.5 lazy dogs, Café naïve Straße don't stop-gap 日本語.", 20));
        foreach (string name in new[] { "standard", "en", "en-both", "cjk" })
        {
            Analyzer analyzer = AnalyzerRegistry.Create(name);
            ReadOnlyMemory<char> memory = text.AsMemory();
            int tokens = 0;
            TestHelpers.AssertNoAllocation(name, () => tokens += Drain(analyzer, memory), windows: 10);
            Assert.True(tokens > 1000);
        }
    }

    private static int Drain(Analyzer analyzer, ReadOnlyMemory<char> text)
    {
        TokenSource stream = analyzer.GetTokens(text);
        int n = 0;
        while (stream.MoveNext())
        {
            n += stream.Token.Term.Length > 0 ? 1 : 0;
        }

        return n;
    }
}
