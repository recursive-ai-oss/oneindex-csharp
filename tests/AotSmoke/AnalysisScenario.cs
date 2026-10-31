using OneIndex.Analysis;
using OneIndex.Analysis.Snowball;

namespace OneIndex.AotSmoke;

/// <summary>
/// Text analysis under NativeAOT and in the browser runtime: registry lookups without reflection, the Unicode tables, and
/// normalization that must work with invariant globalization (where the platform's own normalization does nothing).
/// </summary>
internal static class AnalysisScenario
{
    public static void Run()
    {
        Expect("standard", "Hello, World! Stra\u00dfe", "hello world strasse");
        Expect("en", "The running dogs", "run dog");
        Expect("en-both", "running", "running run");
        Expect("cjk", "\u65e5\u672c\u8a9e", "\u65e5\u672c \u672c\u8a9e");
        Expect("cjk", "\uff21\uff22\uff23 e\u0301", "abc \u00e9"); // NFKC joins e + combining acute into one character
        Expect("fr", "l\u2019h\u00f4tels", "hotel");

        foreach (string code in LanguageAnalyzer.LanguageCodes)
        {
            Analyzer analyzer = AnalyzerRegistry.Create(code);
            _ = analyzer.Analyze("Das ist ein Test 123 \u0645\u0631\u062d\u0628\u0627 \u041f\u0440\u0438\u0432\u0435\u0442");
        }

        foreach (string language in Stemmers.Languages)
        {
            if (!Stemmers.TryCreate(language, out Stemmer? stemmer))
            {
                throw new InvalidOperationException("missing stemmer " + language);
            }

            _ = stemmer.Stem("testing");
        }
    }

    private static void Expect(string analyzer, string text, string expected)
    {
        string actual = string.Join(' ', AnalyzerRegistry.Create(analyzer).Analyze(text).Select(t => t.Term));
        if (actual != expected)
        {
            throw new InvalidOperationException($"{analyzer}: '{actual}' instead of '{expected}'");
        }
    }
}
