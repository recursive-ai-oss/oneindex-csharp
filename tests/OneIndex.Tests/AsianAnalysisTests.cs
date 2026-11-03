using System.Text;
using OneIndex.Analysis;
using OneIndex.Analysis.Asian;

namespace OneIndex.Tests;

/// <summary>The Asian-language dictionaries, segmenters and analyzers.</summary>
public class AsianAnalysisTests
{
    private static LatticeLexicon TinyLexicon()
    {
        // Two classes, a three-by-three connection matrix, and a handful of words: 東京 + 都 beats 東 + 京都 because of connection costs.
        var b = new LatticeLexiconBuilder();
        b.AddClass("DEFAULT", false, true, 0);
        b.AddClass("KANJI", false, false, 2);
        b.MapRange(0x4E00, 0x9FFF, "KANJI");
        b.AddUnknown("DEFAULT", 1, 1, 5000, "UNK");
        b.AddUnknown("KANJI", 1, 1, 6000, "UNK");
        b.AddWord("東京", 1, 1, 100, "名詞", null);
        b.AddWord("都", 2, 2, 100, "名詞", null);
        b.AddWord("京都", 1, 1, 100, "名詞", null);
        b.AddWord("東", 1, 1, 300, "名詞", null);
        b.AddWord("へ", 2, 2, 50, "助詞", null);
        short[] m = new short[3 * 3];
        // matrix[previousRight * 3 + nextLeft]
        m[(1 * 3) + 2] = 0;
        m[(1 * 3) + 1] = 400;
        m[(2 * 3) + 1] = 0;
        m[(0 * 3) + 1] = 0;
        m[(0 * 3) + 2] = 0;
        b.SetMatrix(3, 3, m);
        return LatticeLexicon.Load(b.Build());
    }

    [Fact]
    public void LatticeChoosesCheapestPath()
    {
        LatticeSegmenter seg = new(TinyLexicon(), LatticeOptions.Normal);
        var output = new List<Segment>();
        seg.Segment("東京都", output);
        Assert.Equal(["東京", "都"], output.Select(s => "東京都".Substring(s.Start, s.Length)));
        seg.Segment("東京都へ", output);
        Assert.Equal(4, output.Sum(s => s.Length));
    }

    [Fact]
    public void LatticeBuildsUnknownWordsAndCoversTheText()
    {
        LatticeSegmenter seg = new(TinyLexicon(), LatticeOptions.Normal);
        var output = new List<Segment>();
        seg.Segment("鬱東京abc", output);
        int pos = 0;
        foreach (Segment s in output)
        {
            Assert.Equal(pos, s.Start);
            Assert.True(s.Length > 0);
            pos += s.Length;
        }

        Assert.Equal(6, pos);
        Assert.Contains(output, s => s.Word == -1);
    }

    [Fact]
    public void LatticeNeverFailsOnArbitraryText()
    {
        LatticeSegmenter seg = new(TinyLexicon());
        var output = new List<Segment>();
        var rng = new Random(7);
        for (int round = 0; round < 500; round++)
        {
            var sb = new StringBuilder();
            for (int i = 0, n = rng.Next(0, 60); i < n; i++)
            {
                sb.Append(rng.Next(6) switch { 0 => '東', 1 => '京', 2 => '\uD83D', 3 => '\uDE00', 4 => 'x', _ => (char)rng.Next(0x4E00, 0x4E80) });
            }

            string text = sb.ToString();
            seg.Segment(text, output);
            Assert.Equal(text.Length, output.Sum(s => s.Length));
        }
    }

    [Fact]
    public void CommonPrefixSearchFindsAllPrefixesShortestFirst()
    {
        byte[] bytes = WordList.Build([("ab", 0u), ("a", 0u), ("abc", 0u), ("b", 0u), ("abd", 0u)]);
        WordList list = WordList.Load(bytes);
        Assert.True(list.Contains("abc"));
        Assert.False(list.Contains("abcd"));
        var found = new List<(int, int)>();
        list.CommonPrefixSearch("abcd", found);
        Assert.Equal([1, 2, 3], found.Select(f => f.Item2));
    }

    [Fact]
    public void UnigramPrefersTheMoreProbablePath()
    {
        WordList list = WordList.Load(WordList.Build([("北京", 1000u), ("大学", 1000u), ("北京大学", 5000u), ("京大", 10u), ("北", 5u), ("京", 5u), ("大", 5u), ("学", 5u)]));
        var seg = new UnigramSegmenter(list, searchMode: false);
        var output = new List<Segment>();
        seg.Segment("北京大学", output);
        Assert.Single(output);
        var search = new UnigramSegmenter(list, searchMode: true);
        search.Segment("北京大学", output);
        Assert.True(output.Count > 1 && output.Skip(1).All(s => s.Stacked));
    }

    [Fact]
    public void MaximalMatchPrefersFewerUnmatchedCharacters()
    {
        WordList list = WordList.Load(WordList.Build([("กิน", 0u), ("ข้าว", 0u), ("กินข้าว", 0u), ("ข้า", 0u)]));
        var seg = new MaximalMatchSegmenter(list);
        var output = new List<Segment>();
        seg.Segment("กินข้าวxกิน", output);
        Assert.Equal(["กินข้าว", "x", "กิน"], output.Select(s => "กินข้าวxกิน".Substring(s.Start, s.Length)));
        Assert.Equal([0, -1, 0], output.Select(s => s.Word < 0 ? -1 : 0));
    }

    [Fact]
    public void DictionaryFilesRejectDamage()
    {
        byte[] good = WordList.Build([("ab", 1u), ("cd", 2u)]);
        _ = WordList.Load(good);
        Assert.False(WordList.TryLoad(good.AsSpan(0, good.Length - 3), out _));
        Assert.False(WordList.TryLoad([], out _));
        for (int i = 0; i < good.Length; i++)
        {
            byte[] bad = (byte[])good.Clone();
            bad[i] ^= 0x5A;
            Assert.False(WordList.TryLoad(bad, out _), $"flip at {i}");
        }

        // A word list is not a lattice dictionary.
        Assert.False(LatticeLexicon.TryLoad(good, out _));
        Assert.Throws<DictionaryFormatException>(() => LatticeLexicon.Load(good));
    }

    [Fact]
    public void RegisteredAnalyzersResolveByName()
    {
        AsianAnalyzers.Register();
        foreach (string name in new[] { "ja", "zh", "ko", "th", "lo", "km", "my" })
        {
            Assert.True(AnalyzerRegistry.TryCreate(name, out Analyzer? a));
            Assert.Equal(name, a.Name);
        }

        Assert.Throws<ArgumentException>(() => AnalyzerRegistry.Register("en", () => new StandardAnalyzer()));
    }

    private static Analyzer? PackedOrNull(Func<Analyzer> make)
    {
        try
        {
            Analyzer a = make();
            _ = a.Analyze("x");
            return a;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [Fact]
    public void PackedJapaneseSplitsCompoundsAndDropsParticles()
    {
        Analyzer? a = PackedOrNull(() => new JapaneseAnalyzer());
        if (a is null)
        {
            Assert.Skip("no packed Japanese dictionary in this build");
            return;
        }

        string[] terms = [.. a.Analyze("関西国際空港に行きました").Select(t => t.Term)];
        Assert.Contains("空港", terms);
        Assert.Contains("行く", terms);
        Assert.DoesNotContain("に", terms);
        IReadOnlyList<AnalyzedToken> mixed = a.Analyze("Tokyoで2020年");
        Assert.Contains(mixed, t => t.Term == "tokyo" && t.StartOffset == 0 && t.EndOffset == 5);
        Assert.Contains(mixed, t => t.Term == "2020");
    }

    [Fact]
    public void PackedKoreanStemsAndDecomposes()
    {
        Analyzer? a = PackedOrNull(() => new KoreanAnalyzer());
        if (a is null)
        {
            Assert.Skip("no packed Korean dictionary in this build");
            return;
        }

        string[] terms = [.. a.Analyze("나는 학교에 갔습니다 한국어").Select(t => t.Term)];
        Assert.Contains("학교", terms);
        Assert.Contains("가", terms);
        Assert.DoesNotContain("에", terms);
        Assert.Contains("한국어", terms);
        Assert.Contains("한국", terms);
    }

    [Fact]
    public void PackedChineseAndSoutheastAsianBreakWords()
    {
        Analyzer? zh = PackedOrNull(() => new ChineseAnalyzer());
        Analyzer? th = PackedOrNull(WordBreakAnalyzer.Thai);
        if (zh is null || th is null)
        {
            Assert.Skip("no packed dictionaries in this build");
            return;
        }

        Assert.Contains("北京", zh.Analyze("我来到北京清华大学").Select(t => t.Term));
        Assert.Equal(["ฉัน", "กิน", "ข้าว"], th.Analyze("ฉันกินข้าว").Select(t => t.Term));
        foreach (Analyzer lang in new[] { WordBreakAnalyzer.Lao(), WordBreakAnalyzer.Khmer(), WordBreakAnalyzer.Myanmar() })
        {
            Assert.Empty(lang.Analyze("   "));
        }
    }

    [Fact]
    public void AnalyzersKeepOffsetsOnOriginalText()
    {
        Analyzer? a = PackedOrNull(() => new JapaneseAnalyzer());
        if (a is null)
        {
            Assert.Skip("no packed Japanese dictionary in this build");
            return;
        }

        string text = "  日本語の文章 and 東京";
        foreach (AnalyzedToken t in a.Analyze(text))
        {
            Assert.InRange(t.StartOffset, 0, text.Length);
            Assert.InRange(t.EndOffset, t.StartOffset, text.Length);
        }
    }
}
