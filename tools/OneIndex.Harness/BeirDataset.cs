using System.Globalization;
using System.Text.Json;

namespace OneIndex.Harness;

/// <summary>A BEIR dataset on disk: corpus.jsonl, queries.jsonl and qrels/test.tsv.</summary>
public sealed class BeirDataset
{
    /// <summary>Dataset name (the directory name).</summary>
    public required string Name { get; init; }

    /// <summary>Directory holding the three files.</summary>
    public required string Directory { get; init; }

    /// <summary>Test-split relevance judgements: query id → document id → grade.</summary>
    public required Dictionary<string, Dictionary<string, int>> Qrels { get; init; }

    /// <summary>The queries that have test judgements, in file order.</summary>
    public required List<(string Id, string Text)> Queries { get; init; }

    /// <summary>Opens a dataset directory, reading queries and qrels (the corpus is streamed by <see cref="ReadCorpus"/>).</summary>
    public static BeirDataset Open(string directory)
    {
        var qrels = ReadQrels(Path.Combine(directory, "qrels", "test.tsv"));

        var queries = new List<(string, string)>();
        foreach (var line in File.ReadLines(Path.Combine(directory, "queries.jsonl")))
        {
            using var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("_id").GetString()!;
            if (qrels.ContainsKey(id))
            {
                queries.Add((id, doc.RootElement.GetProperty("text").GetString() ?? ""));
            }
        }

        return new BeirDataset { Name = Path.GetFileName(directory.TrimEnd('/')), Directory = directory, Qrels = qrels, Queries = queries };
    }

    /// <summary>Reads a BEIR qrels TSV (with its header line): query id → document id → grade.</summary>
    public static Dictionary<string, Dictionary<string, int>> ReadQrels(string path)
    {
        var qrels = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var first = true;
        foreach (var line in File.ReadLines(path))
        {
            if (first)
            {
                first = false;
                if (line.StartsWith("query-id", StringComparison.Ordinal))
                {
                    continue; // header
                }
            }

            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            if (!qrels.TryGetValue(parts[0], out var perQuery))
            {
                qrels[parts[0]] = perQuery = new Dictionary<string, int>(StringComparer.Ordinal);
            }

            perQuery[parts[1]] = int.Parse(parts[2], CultureInfo.InvariantCulture);
        }

        return qrels;
    }

    /// <summary>Streams the corpus in file order.</summary>
    public IEnumerable<Doc> ReadCorpus()
    {
        foreach (var line in File.ReadLines(Path.Combine(Directory, "corpus.jsonl")))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            yield return new Doc(
                root.GetProperty("_id").GetString()!,
                root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "");
        }
    }
}
