using System.Globalization;
using System.Text;
using OneIndex.Analysis.Unicode;

namespace OneIndex.Tests;

public class UnicodeNormalizationTests
{
    /// <summary>NormalizationTest.txt (Unicode 17.0.0) is 2.5 MB, so it is fetched into a cache and not committed.</summary>
    [Fact]
    public void MatchesNormalizationTest()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEINDEX_UCD_DIR");
        string? path = dir is null ? null : Path.Combine(dir, "NormalizationTest.txt");
        if (path is null || !File.Exists(path))
        {
            Assert.Skip("set ONEINDEX_UCD_DIR to a directory holding NormalizationTest.txt");
        }

        int cases = 0;
        var failures = new List<string>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0 || line[0] == '@')
            {
                continue;
            }

            string[] c = line.Split(';').Take(5).Select(Parse).ToArray();
            cases++;
            for (int k = 0; k < 5; k++)
            {
                Check(failures, line, "NFC", c[k], UnicodeForm.Nfc, k <= 2 ? c[1] : c[3]);
                Check(failures, line, "NFD", c[k], UnicodeForm.Nfd, k <= 2 ? c[2] : c[4]);
                Check(failures, line, "NFKC", c[k], UnicodeForm.Nfkc, c[3]);
                Check(failures, line, "NFKD", c[k], UnicodeForm.Nfkd, c[4]);
            }
        }

        Assert.True(cases > 18000, "too few cases: " + cases);
        Assert.True(failures.Count == 0, $"{failures.Count} failures in {cases} cases; first: {string.Join(" | ", failures.Take(5))}");
    }

    [Fact]
    public void AccentAndAsciiFolding()
    {
        var n = new Normalizer();
        Assert.Equal("cafe naive Zoe", Fold(n, "café naïve Zoë", ascii: false));
        Assert.Equal("Strasse oe ae o l d", Fold(n, "Straße œ æ ø ł đ", ascii: true));
        Assert.Equal("fi 1/4 A", Fold(n, "ﬁ ¼ Ａ", ascii: true).Replace("1⁄4", "1/4", StringComparison.Ordinal));
        Assert.Equal("한국어", Fold(n, "한국어", ascii: false));
        Assert.Equal("日本語", Fold(n, "日本語", ascii: true));
        Assert.Equal("αβγ", Fold(n, "άβγ", ascii: false));
    }

    [Fact]
    public void CaseFoldingIsFull()
    {
        Assert.Equal("strasse", FoldCase("Straße"));
        Assert.Equal("σασ", FoldCase("ΣΑΣ"));
        Assert.Equal("σασ", FoldCase("ΣΑς"));
        Assert.Equal("i\u0307", FoldCase("İ"));
        Assert.Equal("\U00010428", FoldCase("\U00010400"));
        Assert.Equal("\uD800x", FoldCase("\uD800X"));
    }

    private static string FoldCase(string s)
    {
        var buffer = new char[(s.Length * CaseFolder.MaxExpansion) + 1];
        return new string(buffer, 0, CaseFolder.Fold(s, buffer));
    }

    private static string Fold(Normalizer n, string s, bool ascii)
    {
        var buffer = new char[(s.Length * 18) + 16];
        bool ok = ascii ? n.TryFoldToAscii(s, buffer, out int w) : n.TryFoldAccents(s, buffer, out w);
        Assert.True(ok);
        return new string(buffer, 0, w);
    }

    private static void Check(List<string> failures, string line, string name, string input, UnicodeForm form, string expected)
    {
        string actual = Normalizer.Normalize(input, form);
        if (actual != expected)
        {
            failures.Add($"{name} {line}");
        }
    }

    private static string Parse(string field)
    {
        var sb = new StringBuilder();
        foreach (string hex in field.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }
}
