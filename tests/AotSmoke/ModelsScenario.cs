using System.Text;
using OneIndex.Models;
using OneIndex.Models.Tokenization;
using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>The WordPiece tokenizer on a tiny vocabulary, and the default embedder when the build carries the packed weights.</summary>
internal static class ModelsScenario
{
    public static void Run()
    {
        string[] tokens = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "un", "##want", "##ed", "cafe", "e"];
        var tokenizer = new WordPieceTokenizer(WordPieceVocabulary.Parse(Encoding.UTF8.GetBytes(string.Join('\n', tokens))), null, UnicodeDecomposer.Instance);
        int[] ids = tokenizer.Encode("Unwanted Caf\u00e9");
        if (!ids.AsSpan().SequenceEqual([2, 5, 6, 7, 8, 3]))
        {
            throw new InvalidOperationException("WordPiece gave " + string.Join(' ', ids));
        }

        BgeSmallEmbedder embedder;
        try
        {
            embedder = BgeSmallEmbedder.Load();
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("models: tokenizer ok; embedder skipped (this build has no packed weights)");
            return;
        }

        using (embedder)
        {
            float[] v = embedder.Embed("A sentence to embed.");
            double norm = 0;
            foreach (float x in v)
            {
                norm += (double)x * x;
            }

            if (v.Length != 384 || Math.Abs(norm - 1) > 1e-3)
            {
                throw new InvalidOperationException($"embedding has length {v.Length} and squared norm {norm}");
            }

            Console.WriteLine($"models: tokenizer ok; embedded a sentence ({embedder.Inner.KernelPath})");
        }
    }
}
