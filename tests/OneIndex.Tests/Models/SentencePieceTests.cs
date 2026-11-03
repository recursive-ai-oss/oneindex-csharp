using System.Text;
using System.Text.Json;
using OneIndex.Models;
using OneIndex.Models.Tokenization;

namespace OneIndex.Tests.Models;

/// <summary>The SentencePiece reader and tokenizer: malformed models, and ids against the released sentencepiece package.</summary>
public class SentencePieceTests
{
    // Reference files come from tools/models/spm_sample.py: ONEINDEX_SPM_MODEL is the model file, ONEINDEX_SPM_SAMPLE the output prefix.
    private static (string Model, string Prefix)? Reference()
    {
        string? m = Environment.GetEnvironmentVariable("ONEINDEX_SPM_MODEL"), p = Environment.GetEnvironmentVariable("ONEINDEX_SPM_SAMPLE");
        return m is not null && p is not null && File.Exists(m) && File.Exists(p + ".ids.jsonl") ? (m, p) : null;
    }

    [Fact]
    public void IdsMatchTheReferenceOnTheMultilingualSample()
    {
        (string Model, string Prefix)? r = Reference();
        if (r is null)
        {
            Assert.Skip("set ONEINDEX_SPM_MODEL and ONEINDEX_SPM_SAMPLE (tools/models/spm_sample.py)");
            return;
        }

        var tokenizer = SentencePieceTokenizer.Create(SentencePieceModel.Load(File.ReadAllBytes(r.Value.Model)));
        string[] texts = File.ReadAllLines(r.Value.Prefix + ".texts.jsonl");
        string[] expected = File.ReadAllLines(r.Value.Prefix + ".ids.jsonl");
        Assert.Equal(texts.Length, expected.Length);
        string[] normalized = File.ReadAllLines(r.Value.Prefix + ".norm.jsonl");
        int normalizationDiffs = 0;
        for (int i = 0; i < texts.Length; i++)
        {
            string text = JsonSerializer.Deserialize<string>(texts[i])!;
            if (tokenizer.Normalize(text) != JsonSerializer.Deserialize<string>(normalized[i])!)
            {
                normalizationDiffs++;
            }
        }

        Assert.True(normalizationDiffs == 0, $"{normalizationDiffs} normalized texts differ");
        int mismatches = 0;
        var report = new StringBuilder();
        for (int i = 0; i < texts.Length; i++)
        {
            string text = JsonSerializer.Deserialize<string>(texts[i])!;
            int[] want = JsonSerializer.Deserialize<int[]>(expected[i])!;
            var got = new List<int>();
            tokenizer.Encode(text, got);
            if (!got.SequenceEqual(want))
            {
                mismatches++;
                if (mismatches <= 12)
                {
                    string shown = text.Length > 60 ? text[..60] : text;
                    report.AppendLine($"#{i} {JsonSerializer.Serialize(shown)} want [{string.Join(' ', want.Take(20))}] got [{string.Join(' ', got.Take(20))}]");
                }
            }
        }

        Assert.True(mismatches == 0, $"{mismatches} of {texts.Length} differ\n{report}");
    }

    [Fact]
    public void MalformedModelsAreRefusedWithTypedErrors()
    {
        Assert.Throws<SentencePieceFormatException>(() => SentencePieceModel.Load([]));
        Assert.Throws<SentencePieceFormatException>(() => SentencePieceModel.Load([0x0A, 0xFF, 0xFF, 0xFF, 0x7F]));
        Assert.Throws<SentencePieceFormatException>(() => SentencePieceModel.Load([0x0A, 0x05, 0x0A]));
        var rng = new Random(11);
        for (int i = 0; i < 2000; i++)
        {
            byte[] junk = new byte[rng.Next(0, 80)];
            rng.NextBytes(junk);
            try
            {
                _ = SentencePieceModel.Load(junk);
            }
            catch (SentencePieceFormatException)
            {
            }
        }
    }

    [Fact]
    public void TruncatedRealModelNeverThrowsAnythingElse()
    {
        (string Model, string Prefix)? r = Reference();
        if (r is null)
        {
            Assert.Skip("no reference model");
            return;
        }

        byte[] bytes = File.ReadAllBytes(r.Value.Model);
        var rng = new Random(5);
        for (int i = 0; i < 300; i++)
        {
            byte[] copy = bytes.AsSpan(0, rng.Next(0, bytes.Length)).ToArray();
            if (copy.Length > 0 && rng.Next(2) == 0)
            {
                copy[rng.Next(copy.Length)] ^= (byte)rng.Next(1, 256);
            }

            try
            {
                SentencePieceModel m = SentencePieceModel.Load(copy);
                _ = SentencePieceTokenizer.Create(m).Encode("hello world", []);
            }
            catch (SentencePieceFormatException)
            {
            }
        }
    }
}
