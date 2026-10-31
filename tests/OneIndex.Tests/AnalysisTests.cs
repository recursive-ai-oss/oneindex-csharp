using OneIndex.Analysis;

namespace OneIndex.Tests;

[Collection(AllocationMeasurement.Name)]
public class AnalysisTests
{
    [Fact]
    public void ReferenceAnalyzerSplitsLowercasesAndKeepsOffsets()
    {
        Analyzer analyzer = AnalyzerRegistry.Create("whitespace-lowercase");
        IReadOnlyList<AnalyzedToken> tokens = analyzer.Analyze("  Hello\tWORLD  Ärger ");
        Assert.Equal(["hello", "world", "ärger"], tokens.Select(t => t.Term));
        Assert.Equal([0, 1, 2], tokens.Select(t => t.Position));
        Assert.Equal([2, 8, 15], tokens.Select(t => t.StartOffset));
        Assert.Equal([7, 13, 20], tokens.Select(t => t.EndOffset));
        Assert.All(tokens, t => Assert.Equal(TokenType.Word, t.Type));
        Assert.Empty(analyzer.Analyze("   "));
        Assert.Empty(analyzer.Analyze(string.Empty));
    }

    [Fact]
    public void OffsetsIndexTheOriginalText()
    {
        const string text = "The Quick  brown";
        foreach (AnalyzedToken t in new WhitespaceLowercaseAnalyzer().Analyze(text))
        {
            Assert.Equal(t.Term, text[t.StartOffset..t.EndOffset].ToLowerInvariant());
        }
    }

    [Fact]
    public void KeywordAnalyzerEmitsTheWholeText()
    {
        IReadOnlyList<AnalyzedToken> t = new KeywordAnalyzer().Analyze("Exact Value 42");
        Assert.Single(t);
        Assert.Equal("Exact Value 42", t[0].Term);
        Assert.Equal((0, 14), (t[0].StartOffset, t[0].EndOffset));
        Assert.Empty(new KeywordAnalyzer().Analyze(string.Empty));
    }

    [Fact]
    public void RegistryRefusesUnknownNames()
    {
        Assert.False(AnalyzerRegistry.TryCreate("no-such-analyzer", out Analyzer? a));
        Assert.Null(a);
        Assert.Throws<ArgumentException>(() => AnalyzerRegistry.Create("no-such-analyzer"));
    }

    /// <summary>A filter that drops a word and carries its position increment to the next token, as stop-word removal must.</summary>
    private sealed class DropWordFilter(TokenSource input, string word) : TokenFilter(input)
    {
        public override bool MoveNext()
        {
            int skipped = 0;
            while (Input.MoveNext())
            {
                if (Token.Term.SequenceEqual(word))
                {
                    skipped += Token.PositionIncrement;
                    continue;
                }

                Token.PositionIncrement += skipped;
                return true;
            }

            return false;
        }
    }

    [Fact]
    public void FiltersComposeAndPositionsStayHonest()
    {
        var analyzer = new CustomAnalyzer("test-chain", static () => new WhitespaceTokenizer(), static s => new LowerCaseFilter(s), static s => new DropWordFilter(s, "the"));
        IReadOnlyList<AnalyzedToken> t = analyzer.Analyze("The cat and THE hat");
        Assert.Equal(["cat", "and", "hat"], t.Select(x => x.Term));
        Assert.Equal([1, 2, 4], t.Select(x => x.Position)); // the gaps left by "the" remain
        Assert.Equal("test-chain", analyzer.Name);
    }

    [Fact]
    public void ASteadyStateStreamAllocatesNothing()
    {
        Analyzer analyzer = new WhitespaceLowercaseAnalyzer();
        ReadOnlyMemory<char> text = "The Quick brown fox jumps over the lazy dog, again and AGAIN".AsMemory();

        static int Drain(Analyzer a, ReadOnlyMemory<char> text)
        {
            TokenSource stream = a.GetTokens(text);
            int n = 0;
            while (stream.MoveNext())
            {
                n += stream.Token.Term.Length;
            }

            return n;
        }

        int total = 0;
        TestHelpers.AssertNoAllocation("analysis", () => total += Drain(analyzer, text), windows: 10);
        Assert.True(total > 0);
    }

    [Fact]
    public void LongTermsGrowTheSharedBufferAndKeepWorking()
    {
        string word = new('Z', 10_000);
        IReadOnlyList<AnalyzedToken> t = new WhitespaceLowercaseAnalyzer().Analyze("a " + word + " b");
        Assert.Equal(3, t.Count);
        Assert.Equal(new string('z', 10_000), t[1].Term);
    }
}
