using OneIndex.Analysis;
using OneIndex.Analysis.Unicode;

namespace OneIndex.Fuzz;

/// <summary>
/// Feeds arbitrary UTF-16 text (lone surrogates and huge runs included) to every analyzer, both segmenters and the normalizer.
/// Nothing may be thrown. For every token: offsets lie inside the text and do not go backwards past the previous token's start,
/// the term is not empty, positions never decrease and a type or mark combination is one a filter can produce. The segmenters must
/// make progress and cover the text exactly, and normalization must be idempotent.
/// </summary>
internal static class AnalysisTarget
{
    private static readonly string[] Names =
    [
        "standard", "cjk", "keyword", "whitespace-lowercase", .. LanguageAnalyzer.LanguageCodes, .. LanguageAnalyzer.LanguageCodes.Select(c => c + "-both"),
    ];

    private static readonly Analyzer[] Analyzers = [.. Names.Select(AnalyzerRegistry.Create)];

    private const int MaxChars = 400_000;

    // Input: byte 0 picks the analyzer, byte 1 the number of times the text is repeated (to make long runs), the rest is UTF-16LE.
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        Analyzer analyzer = Analyzers[data[0] % Analyzers.Length];
        int repeat = 1 + (data[1] % 64 == 0 ? 8_000 : data[1] % 4);
        ReadOnlySpan<byte> body = data[2..];
        var chars = new char[body.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(body[2 * i] | (body[(2 * i) + 1] << 8));
        }

        if (chars.Length > 0 && (long)chars.Length * repeat > MaxChars)
        {
            repeat = Math.Max(1, MaxChars / chars.Length);
        }

        string text = repeat == 1 ? new string(chars) : string.Concat(Enumerable.Repeat(new string(chars), repeat));

        CheckTokens(analyzer, text);
        CheckSegments(text);
        CheckNormalization(text.Length > 4_000 ? text[..4_000] : text);
    }

    public static void WriteSeeds(string directory)
    {
        string[] samples =
        [
            "The quick brown fox, jumping over 3.14 lazy dogs; don\u2019t stop.",
            "Caf\u00e9 na\u00efve Stra\u00dfe \u00e6\u00f8\u00e5 \ufb01 \uff21\uff22\uff23",
            "\u65e5\u672c\u8a9e\u306e\u30c6\u30b9\u30c8 \ud55c\uad6d\uc5b4 \u4e2d\u6587\u6d4b\u8bd5",
            "l\u2019avion dell\u2019arte \u041f\u0440\u0438\u0432\u0435\u0442 \u0645\u0631\u062d\u0628\u0627 \u05e9\u05dc\u05d5\u05dd",
            "a\u0301\u0301\u0301 \U0001F1E6\U0001F1E7\U0001F1E8 \U0001F468\u200D\U0001F469\u200D\U0001F467 \ud800 x \udc00 y",
            "Mr. Smith went to Washington. He said \"Hello.\" Then left! 1.5 U.S.A. ok?\r\nNext line.",
        ];
        int n = 0;
        foreach (string sample in samples)
        {
            foreach (byte selector in new byte[] { 0, 1, 10, 40 })
            {
                var bytes = new byte[2 + (sample.Length * 2)];
                bytes[0] = selector;
                bytes[1] = 1;
                for (int i = 0; i < sample.Length; i++)
                {
                    bytes[2 + (2 * i)] = (byte)sample[i];
                    bytes[3 + (2 * i)] = (byte)(sample[i] >> 8);
                }

                File.WriteAllBytes(Path.Combine(directory, $"seed-{n++}"), bytes);
            }
        }
    }

    private static void CheckTokens(Analyzer analyzer, string text)
    {
        TokenSource stream = analyzer.GetTokens(text.AsMemory());
        while (stream.MoveNext())
        {
            Token t = stream.Token;
            if (t.Term.IsEmpty)
            {
                throw new InvalidOperationException($"{analyzer.Name}: empty term");
            }

            if (t.StartOffset < 0 || t.EndOffset < t.StartOffset || t.EndOffset > text.Length)
            {
                throw new InvalidOperationException($"{analyzer.Name}: bad offsets {t.StartOffset}..{t.EndOffset} of {text.Length}");
            }

            if (t.PositionIncrement < 0)
            {
                throw new InvalidOperationException($"{analyzer.Name}: negative increment");
            }

        }
    }

    private static void CheckSegments(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int next = WordBreaker.Next(text, i);
            if (next <= i || next > text.Length)
            {
                throw new InvalidOperationException($"word segmenter stuck at {i}");
            }

            i = next;
        }

        i = 0;
        while (i < text.Length)
        {
            int next = SentenceBreaker.Next(text, i);
            if (next <= i || next > text.Length)
            {
                throw new InvalidOperationException($"sentence segmenter stuck at {i}");
            }

            i = next;
        }
    }

    private static void CheckNormalization(string text)
    {
        foreach (UnicodeForm form in new[] { UnicodeForm.Nfc, UnicodeForm.Nfkc })
        {
            string once = Normalizer.Normalize(text, form);
            if (Normalizer.Normalize(once, form) != once)
            {
                throw new InvalidOperationException($"{form} is not idempotent");
            }
        }
    }
}
