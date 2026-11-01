using System.Globalization;
using System.Text.Json;
using Xunit;

namespace OneIndex.Tests;

[TypoTolerance(Enabled = false)]
[PrefixSearch(false)]
public class BeirPassage
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Boost = 3)]
    public string Title { get; set; } = string.Empty;

    [FullText]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Information report (skipped unless <c>ONEINDEX_BEIR_DIR</c> names a directory with the BEIR sets): nDCG@10 of the <c>words</c> rule alone
/// (any query word matches, more words first, ties by the BM25F score) and of the Meilisearch default ranking-rules order on the same documents
/// and queries. The plain happy-path search requires every word, which is the wrong arm for long natural-language queries.
/// <c>ONEINDEX_BEIR_SETS</c> lists the sets (default scifact), <c>ONEINDEX_BEIR_QUERIES</c> caps the queries per set.
/// </summary>
public sealed class RankingRulesRelevanceReport
{
    private static readonly string[] Rules = ["words", "typo", "proximity", "attribute", "exactness"];

    [Fact]
    public void ReportNdcgOfTheDefaultAndOfTheMeilisearchRuleOrder()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEINDEX_BEIR_DIR");
        Assert.SkipUnless(dir is not null, "set ONEINDEX_BEIR_DIR to the directory holding the BEIR sets");
        int cap = int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_BEIR_QUERIES"), CultureInfo.InvariantCulture, out int c) ? c : int.MaxValue;
        foreach (string set in (Environment.GetEnvironmentVariable("ONEINDEX_BEIR_SETS") ?? "scifact").Split(','))
        {
            string root = Path.Combine(dir!, set);
            using SearchIndex<BeirPassage> index = SearchIndex.InMemory<BeirPassage>();
            foreach (string line in File.ReadLines(Path.Combine(root, "corpus.jsonl")))
            {
                using JsonDocument d = JsonDocument.Parse(line);
                JsonElement r = d.RootElement;
                index.Upsert(new BeirPassage { Id = r.GetProperty("_id").GetString()!, Title = r.GetProperty("title").GetString() ?? string.Empty, Text = r.GetProperty("text").GetString() ?? string.Empty });
            }

            index.Commit();
            var qrels = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(Path.Combine(root, "qrels", "test.tsv")).Skip(1))
            {
                string[] f = line.Split('\t');
                if (!qrels.TryGetValue(f[0], out Dictionary<string, int>? m))
                {
                    qrels[f[0]] = m = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                m[f[1]] = int.Parse(f[2], CultureInfo.InvariantCulture);
            }

            double sumWords = 0;
            double sumRules = 0;
            int n = 0;
            foreach (string line in File.ReadLines(Path.Combine(root, "queries.jsonl")))
            {
                using JsonDocument d = JsonDocument.Parse(line);
                string qid = d.RootElement.GetProperty("_id").GetString()!;
                if (!qrels.TryGetValue(qid, out Dictionary<string, int>? rel) || n >= cap)
                {
                    continue;
                }

                string text = d.RootElement.GetProperty("text").GetString()!;
                sumWords += Ndcg10(index.Search(text).RankingRules("words").Take(11).Select(h => h.Document.Id), qid, rel);
                sumRules += Ndcg10(index.Search(text).RankingRules(Rules).Take(11).Select(h => h.Document.Id), qid, rel);
                n++;
            }

            Console.WriteLine($"ranking-rules report {set}: queries {n}, rules [words] (any word, ties by relevance) {sumWords / n:F4}, rules [{string.Join(' ', Rules)}] {sumRules / n:F4}");
        }
    }

    // BEIR convention: a hit whose id equals the query id is dropped; ten results are scored.
    private static double Ndcg10(IEnumerable<string> ranked, string qid, Dictionary<string, int> rel)
    {
        double dcg = 0;
        int rank = 0;
        foreach (string id in ranked)
        {
            if (id == qid)
            {
                continue;
            }

            if (rank == 10)
            {
                break;
            }

            if (rel.TryGetValue(id, out int g) && g > 0)
            {
                dcg += g / Math.Log2(rank + 2);
            }

            rank++;
        }

        double ideal = 0;
        int i = 0;
        foreach (int g in rel.Values.Where(v => v > 0).OrderByDescending(v => v).Take(10))
        {
            ideal += g / Math.Log2(i + 2);
            i++;
        }

        return ideal == 0 ? 0 : dcg / ideal;
    }
}
