using System.Text;
using OneIndex.Models;
using OneIndex.Models.Tokenization;

namespace OneIndex.Fuzz;

/// <summary>
/// The WordPiece tokenizer on arbitrary UTF-16 text (lone surrogates and controls included) over a small synthetic vocabulary:
/// nothing may be thrown, every id is in the vocabulary, the output fits, a shorter limit gives a prefix of a longer one, and the
/// result does not depend on how the call is made.
/// </summary>
internal static class WordPieceTarget
{
    private static readonly WordPieceTokenizer Tokenizer = Create();

    private static WordPieceTokenizer Create()
    {
        var tokens = new List<string> { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]" };
        for (char c = 'a'; c <= 'z'; c++)
        {
            tokens.Add(c.ToString());
            tokens.Add("##" + c);
        }

        tokens.AddRange(["the", "##ing", "e\u4e2d", "\u4e2d", ".", ",", "!", "[", "]", "-"]);
        return new WordPieceTokenizer(WordPieceVocabulary.Parse(Encoding.UTF8.GetBytes(string.Join('\n', tokens))), null, UnicodeDecomposer.Instance);
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(data[2 * i] | (data[(2 * i) + 1] << 8));
        }

        string text = new(chars);
        int[] full = Tokenizer.Encode(text, 512);
        Check(full, 512);
        int[] again = Tokenizer.Encode(text, 512);
        if (!full.AsSpan().SequenceEqual(again))
        {
            throw new InvalidOperationException("Encoding is not deterministic.");
        }

        foreach (int limit in new[] { 2, 3, 7, 40 })
        {
            int[] cut = Tokenizer.Encode(text, limit);
            Check(cut, limit);
            int content = cut.Length - 2;
            if (content > full.Length - 2 || !cut.AsSpan(1, content).SequenceEqual(full.AsSpan(1, content)) || (content < limit - 2 && content != full.Length - 2))
            {
                throw new InvalidOperationException("A truncated encoding is not a prefix of the full one.");
            }
        }

        TokenizedBatch batch = Tokenizer.EncodeBatch([text, ""], 512);
        if (!batch.InputIds.AsSpan(0, full.Length).SequenceEqual(full) || batch.Lengths[0] != full.Length)
        {
            throw new InvalidOperationException("The batch row differs from the single encoding.");
        }
    }

    private static void Check(int[] ids, int limit)
    {
        if (ids.Length < 2 || ids.Length > limit || ids[0] != Tokenizer.ClassId || ids[^1] != Tokenizer.SeparatorId)
        {
            throw new InvalidOperationException("Bad output shape.");
        }

        foreach (int id in ids)
        {
            if ((uint)id >= (uint)Tokenizer.Vocabulary.Count)
            {
                throw new InvalidOperationException("Id out of range.");
            }
        }
    }

    public static void WriteSeeds(string directory)
    {
        string[] seeds = ["the things, running!", "[CLS] [SEP]x[MASK]", "e\u4e2d\u4e2d \u00e9\u0301\u0130", "", "\ud83d\ude00 \ud800 x\u0000y", new string('a', 300) + " " + new string('b', 99)];
        for (int i = 0; i < seeds.Length; i++)
        {
            byte[] bytes = new byte[seeds[i].Length * 2];
            for (int c = 0; c < seeds[i].Length; c++)
            {
                bytes[2 * c] = (byte)seeds[i][c];
                bytes[(2 * c) + 1] = (byte)(seeds[i][c] >> 8);
            }

            File.WriteAllBytes(Path.Combine(directory, "seed-" + i), bytes);
        }
    }
}
