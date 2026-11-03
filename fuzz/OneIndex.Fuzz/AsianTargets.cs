using System.Buffers.Binary;
using System.Text;
using OneIndex.Analysis;
using OneIndex.Analysis.Asian;
using OneIndex.Models.Tokenization;

namespace OneIndex.Fuzz;

/// <summary>
/// The dictionary and model readers on damaged files (byte 0 picks the reader: lattice dictionary, word list or SentencePiece model;
/// bit 7 repairs the checksum footer of a dictionary so mutations reach the section readers), and the Asian segmenters, the decompounder
/// and the SentencePiece tokenizer on arbitrary UTF-16 text over small synthetic dictionaries. Only the typed format exceptions may
/// escape a reader; segmenters must cover the text, keep offsets inside it, and never throw.
/// </summary>
internal static class AsianTargets
{
    private static readonly LatticeLexicon Lexicon = BuildLexicon();
    private static readonly WordList Words = WordList.Load(WordList.Build([("日本", 100u), ("日本語", 50u), ("語", 10u), ("กิน", 1u), ("ข้าว", 1u), ("กินข้าว", 1u)]));
    private static readonly CompoundSplitter Splitter = new(CompoundVocabulary.FromFrequencies([("haus", 90), ("tür", 50), ("brief", 40), ("liebe", 30)]), CompoundOptions.For("de"));
    private static readonly SentencePieceTokenizer Tokenizer = BuildTokenizer();

    private static LatticeLexicon BuildLexicon()
    {
        var b = new LatticeLexiconBuilder();
        b.AddClass("DEFAULT", false, true, 0);
        b.AddClass("KANJI", false, false, 2);
        b.MapRange(0x4E00, 0x9FFF, "KANJI");
        b.AddUnknown("DEFAULT", 1, 1, 5000, "UNK");
        b.AddUnknown("KANJI", 1, 1, 6000, "UNK");
        b.AddWord("日本", 1, 1, 100, "名詞", null);
        b.AddWord("語", 2, 2, 100, "名詞", null);
        b.AddWord("です", 2, 2, 50, "助動詞", "だ");
        b.SetMatrix(3, 3, new short[9]);
        return LatticeLexicon.Load(b.Build());
    }

    private static SentencePieceTokenizer BuildTokenizer()
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
        // Normalizer spec: name "identity" (field 3, then field 1 inside).
        byte[] spec = [0x0A, 0x08, .. "identity"u8];
        model.WriteByte(0x1A);
        model.WriteByte((byte)spec.Length);
        model.Write(spec);
        return SentencePieceTokenizer.Create(SentencePieceModel.Load(model.ToArray()));
    }

    internal static void RunDictionary(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte mode = data[0];
        byte[] body = data[1..].ToArray();
        if ((mode & 0x80) != 0 && (mode & 3) != 2 && body.Length >= 12)
        {
            // Recompute the checksum and closing magic so a mutated section is read rather than rejected up front.
            int end = body.Length - 12;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(end), Crc32C.Compute(body.AsSpan(0, end)));
            "OIDEND\u0000\u0001"u8.CopyTo(body.AsSpan(end + 4));
        }

        try
        {
            switch (mode & 3)
            {
                case 0:
                    LatticeLexicon lexicon = LatticeLexicon.Load(body);
                    var segmenter = new LatticeSegmenter(lexicon);
                    var output = new List<Segment>();
                    segmenter.Segment("日本語abc 123です\uD83D\uDE00", output);
                    CheckCover(output, 14);
                    break;
                case 1:
                    WordList list = WordList.Load(body);
                    _ = list.Contains("日本");
                    new UnigramSegmenter(list).Segment("日本語日本", []);
                    new MaximalMatchSegmenter(list).Segment("日本語日本", []);
                    break;
                default:
                    SentencePieceModel model = SentencePieceModel.Load(body);
                    try
                    {
                        _ = SentencePieceTokenizer.Create(model).Encode("abc ab 日本", []);
                    }
                    catch (SentencePieceFormatException)
                    {
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is DictionaryFormatException or SentencePieceFormatException)
        {
        }
    }

    private static void CheckCover(List<Segment> segments, int length)
    {
        int pos = 0;
        foreach (Segment s in segments)
        {
            if (s.Start != pos || s.Length < 1)
            {
                throw new InvalidOperationException("The segments do not cover the text.");
            }

            pos += s.Length;
        }

        if (pos != length)
        {
            throw new InvalidOperationException("The segments do not end at the end of the text.");
        }
    }

    internal static void RunText(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(data[2 * i] | (data[(2 * i) + 1] << 8));
        }

        string text = new(chars);
        var segments = new List<Segment>();
        new LatticeSegmenter(Lexicon).Segment(text, segments);
        if (text.Length > 0)
        {
            CheckCover(segments, text.Length);
        }

        new UnigramSegmenter(Words).Segment(text, segments);
        new MaximalMatchSegmenter(Words).Segment(text, segments);
        foreach (Analyzer analyzer in new Analyzer[] { new JapaneseAnalyzer(Lexicon), new ChineseAnalyzer(Words), new KoreanAnalyzer(Lexicon), new WordBreakAnalyzer(WordBreakScript.Thai, Words) })
        {
            foreach (AnalyzedToken t in analyzer.Analyze(text))
            {
                if (t.Term.Length == 0 || t.StartOffset < 0 || t.EndOffset < t.StartOffset || t.EndOffset > text.Length)
                {
                    throw new InvalidOperationException($"{analyzer.Name} produced a bad token.");
                }
            }
        }

        var parts = new List<(int Start, int Length)>();
        if (Splitter.TrySplit(text, parts) && (parts.Count < 2 || parts.Any(p => p.Start < 0 || p.Length < 1 || p.Start + p.Length > text.Length)))
        {
            throw new InvalidOperationException("The splitter returned bad parts.");
        }

        var ids = new List<int>();
        Tokenizer.Encode(text, ids);
        string normalized = Tokenizer.Normalize(text);
        if (normalized.Length == 0 && ids.Count != 0)
        {
            throw new InvalidOperationException("Pieces from empty text.");
        }
    }

    internal static void WriteSeeds(string directory)
    {
        File.WriteAllBytes(Path.Combine(directory, "text-1"), Encoding.Unicode.GetBytes("日本語です abc กินข้าว Haustür"));
        File.WriteAllBytes(Path.Combine(directory, "text-2"), Encoding.Unicode.GetBytes("\uD83D\uDE00 a\u0301 \u1100\u1161"));
    }

    internal static void WriteDictionarySeeds(string directory)
    {
        byte[] words = WordList.Build([("日本", 100u), ("語", 10u)]);
        File.WriteAllBytes(Path.Combine(directory, "words"), [0x81, .. words]);
        var b = new LatticeLexiconBuilder();
        b.AddClass("DEFAULT", false, true, 0);
        b.AddUnknown("DEFAULT", 1, 1, 5000, "UNK");
        b.AddWord("日本", 1, 1, 100, "名詞", null);
        b.SetMatrix(2, 2, new short[4]);
        File.WriteAllBytes(Path.Combine(directory, "lattice"), [0x80, .. b.Build()]);
    }
}
