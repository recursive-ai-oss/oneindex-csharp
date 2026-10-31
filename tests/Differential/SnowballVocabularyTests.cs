using System.IO.Compression;
using System.Text;
using OneIndex.Analysis.Snowball;

namespace OneIndex.Differential;

/// <summary>
/// Every stemmer against the vocabulary and expected output the Snowball project publishes for its algorithms
/// (snowball-data: voc.txt and output.txt per language). The data is fetched into a cache and never committed; set
/// ONEINDEX_SNOWBALL_DATA to the directory that holds one folder per language.
/// </summary>
public class SnowballVocabularyTests
{
    [Fact]
    public void AllLanguagesMatchPublishedOutput()
    {
        string? root = Environment.GetEnvironmentVariable("ONEINDEX_SNOWBALL_DATA");
        if (root is null || !Directory.Exists(root))
        {
            Assert.Skip("set ONEINDEX_SNOWBALL_DATA to the snowball-data directory");
        }

        var report = new StringBuilder();
        int totalWords = 0;
        int totalMatched = 0;
        var firstDifferences = new List<string>();
        foreach (string language in Stemmers.Languages)
        {
            string dir = Path.Combine(root, language);
            string[]? voc = ReadLines(Path.Combine(dir, "voc.txt"));
            string[]? expected = ReadLines(Path.Combine(dir, "output.txt"));
            if (voc is null || expected is null)
            {
                report.AppendLine($"{language}: no vocabulary published");
                continue;
            }

            Assert.Equal(voc.Length, expected.Length);
            Assert.True(Stemmers.TryCreate(language, out Stemmer? stemmer));
            int matched = 0;
            var differences = new List<string>();
            for (int i = 0; i < voc.Length; i++)
            {
                string actual = new(stemmer.Stem(voc[i]));
                if (actual == expected[i])
                {
                    matched++;
                }
                else if (differences.Count < 5)
                {
                    differences.Add($"{voc[i]} -> {actual} (expected {expected[i]})");
                }
            }

            totalWords += voc.Length;
            totalMatched += matched;
            report.AppendLine($"{language}: {matched}/{voc.Length}");
            foreach (string d in differences)
            {
                report.AppendLine("    " + d);
            }

            if (matched != voc.Length)
            {
                firstDifferences.Add($"{language} {matched}/{voc.Length}");
            }
        }

        string tmp = Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath();
        File.WriteAllText(Path.Combine(tmp, "snowball-report.txt"), report.ToString());
        Assert.True(firstDifferences.Count == 0, $"{totalMatched}/{totalWords}; differing: {string.Join(", ", firstDifferences)}");
    }

    private static string[]? ReadLines(string path)
    {
        if (File.Exists(path))
        {
            return File.ReadAllLines(path, Encoding.UTF8);
        }

        if (File.Exists(path + ".gz"))
        {
            using var gz = new GZipStream(File.OpenRead(path + ".gz"), CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return reader.ReadToEnd().TrimEnd('\n').Split('\n');
        }

        return null;
    }
}
