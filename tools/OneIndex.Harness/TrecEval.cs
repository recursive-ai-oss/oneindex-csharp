using System.Globalization;

namespace OneIndex.Harness;

/// <summary>TREC run files and the nDCG metric, following trec_eval's definitions so the numbers can be checked against pytrec_eval.</summary>
public static class TrecEval
{
    /// <summary>Writes one query's hits as "qid Q0 docid rank score tag" lines. Scores use the shortest round-trip form so a reader sees the exact values.</summary>
    public static void WriteRun(TextWriter writer, string queryId, IReadOnlyList<Hit> hits, string tag)
    {
        for (var i = 0; i < hits.Count; i++)
        {
            writer.Write(queryId);
            writer.Write(" Q0 ");
            writer.Write(hits[i].Id);
            writer.Write(' ');
            writer.Write((i + 1).ToString(CultureInfo.InvariantCulture));
            writer.Write(' ');
            writer.Write(hits[i].Score.ToString("R", CultureInfo.InvariantCulture));
            writer.Write(' ');
            writer.Write(tag);
            writer.Write('\n');
        }
    }

    /// <summary>Reads a run file: query id → (document id, score) pairs in file order.</summary>
    public static Dictionary<string, List<(string Doc, double Score)>> ReadRun(string path)
    {
        var run = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ');
            if (p.Length < 6)
            {
                continue;
            }

            if (!run.TryGetValue(p[0], out var list))
            {
                run[p[0]] = list = [];
            }

            list.Add((p[2], double.Parse(p[4], CultureInfo.InvariantCulture)));
        }

        return run;
    }

    /// <summary>
    /// Mean nDCG@<paramref name="k"/> over the queries present in both the run and the judgements (the definition BEIR and
    /// pytrec_eval use). Gain is the judged grade, the discount is 1/log2(rank+1), and ranking is by score descending with
    /// ties broken by document id descending, as trec_eval does. A query whose ideal DCG is zero scores 0.
    /// </summary>
    public static (double Mean, int Queries) NdcgAt(int k, Dictionary<string, Dictionary<string, int>> qrels, Dictionary<string, List<(string Doc, double Score)>> run)
    {
        double sum = 0;
        var count = 0;
        foreach (var (queryId, docs) in run)
        {
            if (!qrels.TryGetValue(queryId, out var judged))
            {
                continue;
            }

            var ranked = docs.OrderByDescending(d => d.Score).ThenByDescending(d => d.Doc, StringComparer.Ordinal).Take(k).ToList();
            double dcg = 0;
            for (var i = 0; i < ranked.Count; i++)
            {
                if (judged.TryGetValue(ranked[i].Doc, out var grade) && grade > 0)
                {
                    dcg += grade / Math.Log2(i + 2);
                }
            }

            double ideal = 0;
            var grades = judged.Values.Where(g => g > 0).OrderByDescending(g => g).Take(k).ToList();
            for (var i = 0; i < grades.Count; i++)
            {
                ideal += grades[i] / Math.Log2(i + 2);
            }

            sum += ideal > 0 ? dcg / ideal : 0;
            count++;
        }

        return (count == 0 ? 0 : sum / count, count);
    }
}
