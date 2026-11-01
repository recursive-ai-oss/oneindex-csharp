using System.Text;
using OneIndex.Models;
using OneIndex.Models.Tokenization;

namespace OneIndex.Tests.Models;

public class WordPieceTests
{
    // A tiny vocabulary in the layout of a BERT vocab.txt: ids are line numbers.
    private static readonly string[] Tokens =
    [
        "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]",
        "hello", "world", "un", "##want", "##ed", "##aff", "##able", "cafe", ",", "!", "e", "$", "a", "##b", "\u4e2d", "\u6587", "i",
    ];

    private static readonly WordPieceTokenizer Tokenizer = Create();

    private static WordPieceTokenizer Create(WordPieceOptions? options = null) =>
        new(WordPieceVocabulary.Parse(Encoding.UTF8.GetBytes(string.Join('\n', Tokens) + "\n")), options, UnicodeDecomposer.Instance);

    private static int Id(string token) => Array.IndexOf(Tokens, token);

    [Fact]
    public void SplitsWordsIntoPiecesAndWrapsInSpecialTokens()
    {
        Assert.Equal([2, Id("hello"), Id(","), Id("world"), Id("!"), 3], Tokenizer.Encode("Hello, WORLD!"));
        Assert.Equal([2, Id("un"), Id("##want"), Id("##ed"), 3], Tokenizer.Encode("unwanted"));
        Assert.Equal([2, Id("un"), Id("##aff"), Id("##able"), 3], Tokenizer.Encode("unaffable"));
    }

    [Fact]
    public void AWordThatCannotBeCutIsOneUnknownToken()
    {
        Assert.Equal([2, 1, 3], Tokenizer.Encode("unwantedx"));
        Assert.Equal([2, Id("hello"), 1, Id("world"), 3], Tokenizer.Encode("hello zzz world"));
    }

    [Fact]
    public void LongWordsAreUnknownWithoutMatching()
    {
        Assert.Equal([2, 1, 3], Tokenizer.Encode(new string('a', 101)));
        Assert.Equal([2, Id("a"), Id("##b"), 3], Tokenizer.Encode("ab"));
    }

    [Fact]
    public void AccentsAreStrippedAndCaseFolded()
    {
        Assert.Equal([2, Id("cafe"), 3], Tokenizer.Encode("Caf\u00e9"));
        Assert.Equal([2, Id("cafe"), 3], Tokenizer.Encode("cafe\u0301"));

        // Without accent stripping the precomposed letter is not in the vocabulary.
        WordPieceTokenizer keep = Create(new WordPieceOptions { StripAccents = false });
        Assert.Equal([2, 1, 3], keep.Encode("Caf\u00e9"));

        // U+0130 decomposes to "I" and a combining dot; the dot is stripped before lower-casing, leaving a plain "i".
        Assert.Equal([2, Id("i"), 3], Tokenizer.Encode("\u0130"));
    }

    [Fact]
    public void CjkIdeographsAreWordsOfTheirOwnAndControlsAreDropped()
    {
        Assert.Equal([2, Id("\u4e2d"), Id("\u6587"), 3], Tokenizer.Encode("\u4e2d\u6587"));
        Assert.Equal([2, Id("hello"), Id("\u4e2d"), Id("world"), 3], Tokenizer.Encode("hello\u4e2dworld"));
        Assert.Equal([2, Id("hello"), 3], Tokenizer.Encode("he\u0001l\u200bl\u0000o"));
        Assert.Equal([2, Id("hello"), Id("world"), 3], Tokenizer.Encode("hello\u00a0\u3000world"));
    }

    [Fact]
    public void AsciiSymbolsArePunctuation()
    {
        Assert.Equal([2, Id("$"), Id("a"), 3], Tokenizer.Encode("$a"));
    }

    [Fact]
    public void SpecialTokenTextMapsToItsIdUnlessTurnedOff()
    {
        Assert.Equal([2, Id("hello"), 3, Id("world"), 3], Tokenizer.Encode("hello[SEP]world"));
        WordPieceTokenizer plain = Create(new WordPieceOptions { SplitOnSpecialTokens = false });
        Assert.DoesNotContain(3, plain.Encode("hello[SEP]world").AsSpan(1, plain.Encode("hello[SEP]world").Length - 2).ToArray());
    }

    [Fact]
    public void TruncationKeepsTheSpecialTokens()
    {
        int[] ids = Tokenizer.Encode("hello world hello world hello world", maxLength: 5);
        Assert.Equal([2, Id("hello"), Id("world"), Id("hello"), 3], ids);
        Assert.Equal([2, 3], Tokenizer.Encode(""));
        Assert.Equal([2, 3], Tokenizer.Encode("   \t\n"));
        Span<int> exact = stackalloc int[2];
        Assert.Equal(2, Tokenizer.Encode("hello", exact));
        Assert.Throws<ArgumentException>(() => Tokenizer.Encode("hello", new int[1]));
    }

    [Fact]
    public void BatchesArePaddedWithAMask()
    {
        TokenizedBatch batch = Tokenizer.EncodeBatch(["hello", "hello world !", ""]);
        Assert.Equal(3, batch.Count);
        Assert.Equal(5, batch.Length);
        Assert.Equal([3, 5, 2], batch.Lengths);
        Assert.Equal([2, Id("hello"), 3, 0, 0], batch.InputIds.AsSpan(0, 5).ToArray());
        Assert.Equal([1, 1, 1, 0, 0], batch.AttentionMask.AsSpan(0, 5).ToArray());
        Assert.Equal([2, 3, 0, 0, 0], batch.InputIds.AsSpan(10, 5).ToArray());
        Assert.Equal(0, Tokenizer.EncodeBatch([]).Length);
    }

    [Fact]
    public void AnyStringEncodesWithinBoundsAndNeverThrows()
    {
        var random = new Random(20260101);
        int[] pool = [0x41, 0x61, 0x20, 0xE9, 0x301, 0x130, 0x4E2D, 0x3042, 0xAC00, 0x200B, 0x1F600, 0x2028, 0xA0, 0x7F, 0, 0xFFFD, 0x10FFFF, 0xD800, 0xDC00, 0x5B, 0x53, 0x45, 0x50, 0x5D];
        Span<int> buffer = stackalloc int[64];
        for (int i = 0; i < 20_000; i++)
        {
            var sb = new StringBuilder();
            int length = random.Next(0, 40);
            for (int k = 0; k < length; k++)
            {
                int cp = pool[random.Next(pool.Length)];
                if (cp is >= 0xD800 and <= 0xDFFF)
                {
                    sb.Append((char)cp);
                }
                else
                {
                    sb.Append(char.ConvertFromUtf32(cp));
                }
            }

            int n = Tokenizer.Encode(sb.ToString(), buffer);
            Assert.InRange(n, 2, 64);
            foreach (int id in buffer[..n])
            {
                Assert.InRange(id, 0, Tokens.Length - 1);
            }
        }
    }

    [Fact]
    public void VocabularyRejectsBadInput()
    {
        Assert.Throws<FormatException>(() => WordPieceVocabulary.Parse(""u8));
        Assert.Throws<FormatException>(() => WordPieceVocabulary.Parse("a\nb\na\n"u8));
        Assert.Throws<FormatException>(() => WordPieceVocabulary.Parse([0xFF, 0xFE, 0x0A]));
        Assert.Throws<FormatException>(() => WordPieceVocabulary.Parse("a\nb\nc\n"u8, maxTokens: 2));
        Assert.Equal(2, WordPieceVocabulary.Parse("a\r\nb\r\n"u8).Count);
        Assert.Throws<ArgumentException>(() => new WordPieceTokenizer(WordPieceVocabulary.Parse("a\nb\n"u8)));
    }
}
