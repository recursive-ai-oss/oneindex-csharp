using System.Text;
using OneIndex.Analysis;
using OneIndex.Analysis.Asian;
using OneIndex.Models.Tokenization;

namespace OneIndex.AotSmoke;

/// <summary>
/// The Asian-language analyzers by registered name, the decompounder and the SentencePiece tokenizer. The analyzers and word lists
/// need the packed dictionaries (a build without them skips those checks and says so); the tokenizer runs on a tiny synthetic model.
/// </summary>
internal static class AsianScenario
{
    public static void Run()
    {
        RunSentencePiece();
        AsianAnalyzers.Register();
        int checkedLanguages = 0;
        foreach ((string name, string text, string expected) in new[]
        {
            ("ja", "\u95a2\u897f\u56fd\u969b\u7a7a\u6e2f\u306b\u884c\u304d\u307e\u3057\u305f", "\u7a7a\u6e2f"),
            ("zh", "\u6211\u6765\u5230\u5317\u4eac\u6e05\u534e\u5927\u5b66", "\u5317\u4eac"),
            ("th", "\u0e09\u0e31\u0e19\u0e01\u0e34\u0e19\u0e02\u0e49\u0e32\u0e27", "\u0e01\u0e34\u0e19"),
        })
        {
            Analyzer analyzer = AnalyzerRegistry.Create(name);
            string[] terms;
            try
            {
                terms = [.. analyzer.Analyze(text).Select(t => t.Term)];
            }
            catch (InvalidOperationException)
            {
                continue; // built without the packed dictionary
            }

            if (!terms.Contains(expected))
            {
                throw new InvalidOperationException($"{name} gave {string.Join(' ', terms)}");
            }

            checkedLanguages++;
        }

        string compound = "not run";
        try
        {
            var parts = new List<(int Start, int Length)>();
            CompoundSplitter splitter = CompoundSplitter.ForLanguage("de");
            if (!splitter.TrySplit("Regenschirm", parts) || parts.Count != 2)
            {
                throw new InvalidOperationException("Regenschirm was not split in two");
            }

            IReadOnlyList<AnalyzedToken> tokens = AnalyzerRegistry.Create("de-compound").Analyze("Regenschirm");
            if (tokens.Count < 3)
            {
                throw new InvalidOperationException("de-compound did not index the parts");
            }

            compound = "ok";
        }
        catch (FileNotFoundException)
        {
            compound = "skipped (no packed word lists)";
        }

        Console.WriteLine($"asian: {checkedLanguages} analyzers checked; decompounding {compound}; sentencepiece ok");
    }

    private static void RunSentencePiece()
    {
        var model = new MemoryStream();
        void Piece(string text, float score, int type)
        {
            byte[] t = Encoding.UTF8.GetBytes(text);
            var p = new MemoryStream();
            p.WriteByte(0x0A);
            p.WriteByte((byte)t.Length);
            p.Write(t);
            p.WriteByte(0x15);
            p.Write(BitConverter.GetBytes(score));
            p.WriteByte(0x18);
            p.WriteByte((byte)type);
            model.WriteByte(0x0A);
            model.WriteByte((byte)p.Length);
            p.WriteTo(model);
        }

        Piece("<unk>", 0, 2);
        Piece("<s>", 0, 3);
        Piece("</s>", 0, 3);
        Piece("\u2581", -3, 1);
        foreach (char c in "abcdefghij")
        {
            Piece(c.ToString(), -5 - ((c - 'a') * 0.1f), 1);
        }

        Piece("ab", -4, 1);
        Piece("\u2581a", -4.5f, 1);
        Piece("abc", -6, 1);
        byte[] spec = [0x0A, 0x08, .. "identity"u8];
        model.WriteByte(0x1A);
        model.WriteByte((byte)spec.Length);
        model.Write(spec);
        var ids = new List<int>();
        SentencePieceTokenizer.Create(SentencePieceModel.Load(model.ToArray())).Encode("abc ab", ids);
        if (ids.Count == 0)
        {
            throw new InvalidOperationException("SentencePiece produced no ids");
        }
    }
}
