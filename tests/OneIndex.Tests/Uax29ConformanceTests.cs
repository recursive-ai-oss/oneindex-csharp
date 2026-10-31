using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using OneIndex.Analysis.Unicode;

namespace OneIndex.Tests;

/// <summary>Word and sentence boundaries against the Unicode Character Database 17.0.0 conformance files.</summary>
public class Uax29ConformanceTests
{
    private delegate int NextBreak(ReadOnlySpan<char> text, int start);

    [Fact]
    public void WordBreaksMatchWordBreakTest() => Run("WordBreakTest.txt", WordBreaker.Next, expectedCases: 1944);

    [Fact]
    public void SentenceBreaksMatchSentenceBreakTest() => Run("SentenceBreakTest.txt", SentenceBreaker.Next, expectedCases: 512);

    [Fact]
    public void HostileRunsAreLinear()
    {
        // A million combining marks, regional indicators and sentence terminators must not take quadratic time.
        var sb = new StringBuilder("a");
        sb.Append('\u0301', 1_000_000);
        AssertSegmentCount(sb.ToString(), WordBreaker.Next, 1);

        string flags = string.Concat(Enumerable.Repeat("\U0001F1E6", 1_000_000));
        AssertSegmentCount(flags, WordBreaker.Next, 500_000);

        string closers = "a." + new string(')', 500_000) + " b.";
        AssertSegmentCount(closers, SentenceBreaker.Next, 1);

        string dots = string.Concat(Enumerable.Repeat("1. 1 ", 200_000));
        _ = SentenceBreaker.Next(dots, 0);
    }

    private static void AssertSegmentCount(string text, NextBreak next, int expected)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i = next(text, i))
        {
            count++;
        }

        Assert.Equal(expected, count);
    }

    private static void Run(string fileName, NextBreak next, int expectedCases, [CallerFilePath] string here = "")
    {
        string path = Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "testdata", "ucd", fileName);
        int cases = 0;
        var failures = new List<string>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var text = new StringBuilder();
            var expected = new List<int>();
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (token)
                {
                    case "÷":
                        expected.Add(text.Length);
                        break;
                    case "×":
                        break;
                    default:
                        text.Append(char.ConvertFromUtf32(int.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                        break;
                }
            }

            string s = text.ToString();
            expected.RemoveAt(0); // the boundary at the start of the text is implicit
            var actual = new List<int>();
            for (int i = 0; i < s.Length;)
            {
                i = next(s, i);
                actual.Add(i);
            }

            cases++;
            if (!expected.SequenceEqual(actual))
            {
                failures.Add($"{line}: got [{string.Join(",", actual)}]");
            }
        }

        Assert.Equal(expectedCases, cases);
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} failed; first: {string.Join(" | ", failures.Take(5))}");
    }
}
