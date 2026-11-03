using System.Text;

namespace OneIndex.Files.Tests;

public class ChunkerTests
{
    private static string RandomText(Random rng, int paragraphs, bool markdown)
    {
        var sb = new StringBuilder();
        for (int p = 0; p < paragraphs; p++)
        {
            if (markdown && rng.Next(5) == 0)
            {
                sb.Append('#', 1 + rng.Next(3)).Append(" Heading ").Append(rng.Next(100)).Append("\n\n");
            }

            int sentences = 1 + rng.Next(rng.Next(8) == 0 ? 80 : 5);
            for (int s = 0; s < sentences; s++)
            {
                int words = 3 + rng.Next(25);
                for (int w = 0; w < words; w++)
                {
                    sb.Append(w == 0 ? "Word" : "word").Append(rng.Next(500)).Append(w == words - 1 ? ". " : " ");
                }
            }

            sb.Append("\n\n");
        }

        return sb.ToString();
    }

    private static string Squash(string s) => new([.. s.Where(c => !char.IsWhiteSpace(c))]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChunksAreOrderedExactSubstringsThatCoverEveryBlock(bool markdown)
    {
        var options = new ChunkOptions { TargetTokens = 64, MaxTokens = 96, MinTokens = 16 };
        for (int seed = 0; seed < 150; seed++)
        {
            var rng = new Random(seed);
            string text = RandomText(rng, 1 + rng.Next(30), markdown);
            List<TextBlock> blocks = markdown ? TextExtractor.SplitMarkdown(text) : TextExtractor.SplitPlain(text, BlockKind.Paragraph);
            var unit = new ExtractedUnit(0, text, blocks);
            List<Chunk> chunks = Chunker.Cut(unit, options);

            int previousEnd = 0;
            var covered = new StringBuilder();
            foreach (Chunk c in chunks)
            {
                Assert.True(c.Start >= previousEnd, $"seed {seed}: chunks overlap");
                Assert.True(c.End > c.Start && c.End <= text.Length);
                int tokens = Chunker.EstimateTokens(text.AsSpan(c.Start, c.End - c.Start));
                Assert.True(tokens <= options.MaxTokens + 4, $"seed {seed}: a chunk of {tokens} tokens");
                covered.Append(text, c.Start, c.End - c.Start);
                previousEnd = c.End;
            }

            // Nothing a block holds may be lost: the characters of the chunks, whitespace aside, are those of the blocks.
            var inBlocks = new StringBuilder();
            foreach (TextBlock b in blocks)
            {
                inBlocks.Append(text, b.Start, b.End - b.Start);
            }

            Assert.Equal(Squash(inBlocks.ToString()), Squash(covered.ToString()));
        }
    }

    [Fact]
    public void AHeadingStartsAPassageAndItsPathIsKept()
    {
        string text = "# Guide\n\nIntro words here.\n\n## Install\n\nRun the installer.\n\n## Use\n\nOpen the app.\n";
        var unit = new ExtractedUnit(0, text, TextExtractor.SplitMarkdown(text));
        List<Chunk> chunks = Chunker.Cut(unit, new ChunkOptions { TargetTokens = 8, MaxTokens = 40, MinTokens = 2 });
        Assert.Equal(3, chunks.Count);
        Assert.Equal("Guide", chunks[0].Heading);
        Assert.Equal("Guide > Install", chunks[1].Heading);
        Assert.Equal("Guide > Use", chunks[2].Heading);
        Assert.StartsWith("## Install", text.Substring(chunks[1].Start, chunks[1].End - chunks[1].Start), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockWithoutSpacesIsCutAtTheLimit()
    {
        string text = string.Concat(Enumerable.Repeat("日本語のテキストを検索します。", 80));
        var unit = new ExtractedUnit(0, text, [new TextBlock(0, text.Length, BlockKind.Paragraph)]);
        List<Chunk> chunks = Chunker.Cut(unit, new ChunkOptions { TargetTokens = 50, MaxTokens = 80, MinTokens = 10 });
        Assert.True(chunks.Count > 10);
        Assert.All(chunks, c => Assert.True(Chunker.EstimateTokens(text.AsSpan(c.Start, c.End - c.Start)) <= 80));
        Assert.Equal(text.Length, chunks.Sum(c => c.End - c.Start));
    }

    [Fact]
    public void ATableIsCutBetweenRows()
    {
        var rows = Enumerable.Range(0, 60).Select(i => $"| item{i} | {i * 7} | note about item {i} |");
        string text = string.Join('\n', rows);
        var unit = new ExtractedUnit(0, text, [new TextBlock(0, text.Length, BlockKind.Table)]);
        List<Chunk> chunks = Chunker.Cut(unit, new ChunkOptions { TargetTokens = 60, MaxTokens = 100, MinTokens = 10 });
        Assert.True(chunks.Count > 3);
        foreach (Chunk c in chunks)
        {
            Assert.StartsWith("| item", text.Substring(c.Start, c.End - c.Start), StringComparison.Ordinal);
            Assert.EndsWith(" |", text.Substring(c.Start, c.End - c.Start), StringComparison.Ordinal);
        }
    }
}

public class IgnoreRulesTests
{
    [Theory]
    [InlineData("*.log", "a/b/c.log", false, true)]
    [InlineData("*.log", "a/b/c.txt", false, false)]
    [InlineData("build/", "build", true, true)]
    [InlineData("build/", "build", false, false)]
    [InlineData("/top.txt", "top.txt", false, true)]
    [InlineData("/top.txt", "sub/top.txt", false, false)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]
    [InlineData("docs/*.md", "other/docs/a.md", false, false)]
    [InlineData("**/cache", "a/b/cache", true, true)]
    [InlineData("a/**/z.txt", "a/z.txt", false, true)]
    [InlineData("a/**/z.txt", "a/b/c/z.txt", false, true)]
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("file[0-9].txt", "filex.txt", false, false)]
    [InlineData("file[!0-9].txt", "filex.txt", false, true)]
    public void PatternsMatchLikeGitignore(string pattern, string path, bool isDirectory, bool ignored)
    {
        Assert.Equal(ignored, IgnoreRules.Parse(pattern).Evaluate(path, isDirectory) == true);
    }

    [Fact]
    public void TheLastMatchingRuleWinsAndANegationReincludes()
    {
        IgnoreRules rules = IgnoreRules.Parse("*.txt\n!keep.txt\n# comment\n\n");
        Assert.True(rules.Evaluate("drop.txt", false));
        Assert.False(rules.Evaluate("keep.txt", false));
        Assert.Null(rules.Evaluate("other.md", false));
    }
}

public class ExtractorTests
{
    private static ExtractedUnit One(IFileExtractor e, string name, string content)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return Assert.Single(e.Extract(ms, name, CancellationToken.None).Units);
    }

    [Fact]
    public void MarkdownBlocksAreSlicesOfTheSource()
    {
        string md = "# Title\n\nPara one\ncontinues.\n\n- a\n- b\n  more\n\n```\ncode\n\nstill code\n```\n\n| x | y |\n|---|---|\n| 1 | 2 |\n\nSetext\n======\n\nEnd.\n";
        ExtractedUnit u = One(new TextExtractor(), "a.md", md);
        Assert.Equal(md, u.Text);
        string[] seen = [.. u.Blocks.Select(b => $"{b.Kind}:{md.Substring(b.Start, b.End - b.Start).Replace("\n", "|")}")];
        Assert.Equal(
            [
                "Heading:# Title",
                "Paragraph:Para one|continues.",
                "ListItem:- a",
                "ListItem:- b|  more",
                "Code:```|code||still code|```",
                "Table:| x | y |||---|---||| 1 | 2 |",
                "Heading:Setext|======",
                "Paragraph:End.",
            ],
            seen);
        Assert.Equal(1, u.Blocks[0].Level);
        Assert.Equal(1, u.Blocks[6].Level);
    }

    [Fact]
    public void HtmlKeepsWhatAReaderSeesAndDropsTheRest()
    {
        const string html = "<html><head><title>T</title><style>p{color:red}</style><script>var hidden = 'needle';</script></head><body><h1>Main  <b>title</b></h1><p>First<br>line &amp; more</p><ul><li>one</li><li>two</li></ul><table><tr><th>k</th><th>v</th></tr><tr><td>a</td><td>1</td></tr></table><pre>  keep\n  this</pre></body></html>";
        ExtractedUnit u = One(new HtmlExtractor(), "a.html", html);
        Assert.DoesNotContain("needle", u.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("color", u.Text, StringComparison.Ordinal);
        Assert.Equal("Main title\n\nFirst\nline & more\n\none\n\ntwo\n\nk\tv\na\t1\n\n  keep\n  this", u.Text);
        Assert.Equal([BlockKind.Heading, BlockKind.Paragraph, BlockKind.ListItem, BlockKind.ListItem, BlockKind.Table, BlockKind.Code], u.Blocks.Select(b => b.Kind));
    }

    [Fact]
    public void HtmlNestedAThousandDeepDoesNotOverflowTheStack()
    {
        string html = string.Concat(Enumerable.Repeat("<div>", 3000)) + "deep text" + string.Concat(Enumerable.Repeat("</div>", 3000));
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(html));
        // The parser refuses extreme nesting with a typed exception; either outcome is fine, a crash is not.
        try
        {
            ExtractedFile f = new HtmlExtractor().Extract(ms, "deep.html", CancellationToken.None);
            Assert.Equal("deep text", Assert.Single(f.Units).Text);
        }
        catch (RecursiveAI.OneHtml.HtmlLimitExceededException)
        {
        }
    }

    [Fact]
    public void TextIsDecodedFromUtf8WithOrWithoutMarkAndFromUtf16AndLatin1()
    {
        string s = "caf\u00e9 \u65e5\u672c";
        foreach (byte[] bytes in new[] { Encoding.UTF8.GetBytes(s), [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(s)], [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(s)] })
        {
            using var ms = new MemoryStream(bytes);
            Assert.Equal(s, Assert.Single(new TextExtractor().Extract(ms, "a.txt", CancellationToken.None).Units).Text);
        }

        using var latin = new MemoryStream(Encoding.Latin1.GetBytes("caf\u00e9"));
        Assert.Equal("caf\u00e9", Assert.Single(new TextExtractor().Extract(latin, "a.txt", CancellationToken.None).Units).Text);
    }
}
