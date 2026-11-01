using System.Text;
using System.Text.Json;
using OneIndex.Models.Tokenization;

namespace OneIndex.Differential;

/// <summary>
/// Token ids of <see cref="WordPieceTokenizer"/> against reference ids written by tools/models/tokenizer_reference.py from the
/// Hugging Face tokenizers package (run as a black box). Set ONEINDEX_TOKENIZER_REF to the reference directory and
/// ONEINDEX_MODEL_DIR to the directory holding the model's vocab.txt; the data is fetched into a cache and never committed.
/// </summary>
public class WordPieceConformanceTests
{
    private static (WordPieceTokenizer Tokenizer, string Reference) Setup()
    {
        string? reference = Environment.GetEnvironmentVariable("ONEINDEX_TOKENIZER_REF");
        string? models = Environment.GetEnvironmentVariable("ONEINDEX_MODEL_DIR");
        if (reference is null || models is null || !Directory.Exists(reference))
        {
            Assert.Skip("set ONEINDEX_TOKENIZER_REF and ONEINDEX_MODEL_DIR (see tools/models)");
        }

        var vocabulary = WordPieceVocabulary.Load(Path.Combine(models, "vocab.txt"));
        return (new WordPieceTokenizer(vocabulary, null, OneIndex.Models.UnicodeDecomposer.Instance), reference);
    }

    // Ids that the Hugging Face tokenizer disagrees with only where its Unicode tables are older or newer than the runtime's
    // (characters assigned between the two versions); see docs/models.md. The list is checked in full: any other code point fails.
    [Fact]
    public void EveryUnicodeScalarMatchesTheReference()
    {
        (WordPieceTokenizer tokenizer, string reference) = Setup();
        int compared = 0;
        var mismatches = new List<string>();
        Span<int> buffer = stackalloc int[512];
        foreach (string line in File.ReadLines(Path.Combine(reference, "scalars.tsv")))
        {
            string[] parts = line.Split('\t');
            int cp = Convert.ToInt32(parts[0], 16);
            string scalar = char.ConvertFromUtf32(cp);
            foreach ((string text, string expected) in new[] { ("Ab" + scalar + "Cd", parts[1]), (scalar, parts[2]) })
            {
                int n = tokenizer.Encode(text, buffer);
                string actual = string.Join(' ', buffer[..n].ToArray());
                compared++;
                if (actual != expected)
                {
                    mismatches.Add($"U+{cp:X4} '{text}': ours [{actual}] reference [{expected}]");
                }
            }
        }

        Report("scalars", compared, mismatches);

        // The documented differences (docs/models.md) are exactly the scalars in the fixture: no more, no fewer.
        var allowed = File.ReadLines(Path.Combine(FindRepoRoot(), "testdata", "models", "wordpiece-reference-differences.txt"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => Convert.ToInt32(l, 16)).ToHashSet();
        var differing = mismatches.Select(m => Convert.ToInt32(m.Split(' ')[0][2..], 16)).ToHashSet();
        Assert.Equal(allowed.Order(), differing.Order());
    }

    [Fact]
    public void AdversarialTextAndBeirSamplesMatchTheReference()
    {
        (WordPieceTokenizer tokenizer, string reference) = Setup();
        int compared = 0;
        var mismatches = new List<string>();
        foreach (string line in File.ReadLines(Path.Combine(reference, "samples.jsonl")))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            string text = doc.RootElement.GetProperty("t").GetString()!;
            int[] expected = [.. doc.RootElement.GetProperty("i").EnumerateArray().Select(e => e.GetInt32())];
            int[] actual = tokenizer.Encode(text, 512);
            compared++;
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                string shown = text.Length > 80 ? text[..80] : text;
                mismatches.Add($"'{shown}': ours [{string.Join(' ', actual.Take(30))}] reference [{string.Join(' ', expected.Take(30))}]");
            }
        }

        Report("samples", compared, mismatches);
        Assert.Empty(mismatches);
    }

    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string? thisFile = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static void Report(string name, int compared, List<string> mismatches)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{name}: {compared - mismatches.Count}/{compared} identical");
        foreach (string m in mismatches.Take(Environment.GetEnvironmentVariable("ONEINDEX_CONFORMANCE_REPORT") is null ? 40 : 100000))
        {
            sb.AppendLine("  " + m);
        }

        string? output = Environment.GetEnvironmentVariable("ONEINDEX_CONFORMANCE_REPORT");
        if (output is not null)
        {
            File.AppendAllText(output, sb.ToString());
        }

        Console.WriteLine(sb.ToString());
    }
}
