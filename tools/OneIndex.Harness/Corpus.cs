using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneIndex.Harness;

/// <summary>The benchmark rows.</summary>
public enum RowKind
{
    /// <summary>BM25 top-k, one term.</summary>
    Terms1,

    /// <summary>BM25 top-k, two terms (OR).</summary>
    Terms2,

    /// <summary>BM25 top-k, five terms (OR).</summary>
    Terms5,

    /// <summary>Two-term exact phrase.</summary>
    Phrase,

    /// <summary>A three-letter prefix.</summary>
    Prefix,

    /// <summary>Full terms plus a partial last word.</summary>
    AsYouType,

    /// <summary>One edit.</summary>
    Fuzzy1,

    /// <summary>Two edits.</summary>
    Fuzzy2,

    /// <summary>Terms with a numeric range and category filter.</summary>
    Filtered,

    /// <summary>Terms with category counts.</summary>
    Facets,

    /// <summary>Terms with numeric statistics.</summary>
    Stats,

    /// <summary>Terms ordered by the numeric column.</summary>
    Sort,

    /// <summary>Documents within a radius of a point (synthesized coordinates).</summary>
    Geo,

    /// <summary>Two terms, score times a decay of the numeric column.</summary>
    FunctionScore,
}

/// <summary>Reads the Wikipedia abstract corpus written by <c>tools/wikipedia_abstracts.py</c>.</summary>
public static class WikiCorpus
{
    /// <summary>Reads up to <paramref name="limit"/> documents from the JSON-lines file.</summary>
    public static List<Doc> Read(string path, int limit)
    {
        var docs = new List<Doc>(Math.Min(limit, 1 << 20));
        foreach (var line in File.ReadLines(path))
        {
            if (docs.Count >= limit)
            {
                break;
            }

            using var j = JsonDocument.Parse(line);
            var r = j.RootElement;
            docs.Add(new Doc(
                r.GetProperty("id").GetString()!,
                r.GetProperty("title").GetString() ?? "",
                r.GetProperty("text").GetString() ?? "",
                r.GetProperty("len").GetInt64(),
                r.GetProperty("cat").GetString() ?? ""));
        }

        return docs;
    }
}

/// <summary>
/// Builds the seeded query sets of the benchmark rows from the corpus itself, so every query matches something and the same
/// seed gives the same queries for every engine. Terms are lower-case ASCII words, which every compared analysis keeps whole.
/// </summary>
public static partial class QueryGenerator
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}'’.\-])[a-z]{3,}(?![\p{L}\p{N}'’.\-])")]
    private static partial Regex Word();

    /// <summary>Generates <paramref name="perRow"/> queries for every row.</summary>
    public static Dictionary<RowKind, HarnessQuery[]> Generate(IReadOnlyList<Doc> docs, int perRow, int seed)
    {
        var rng = new Random(seed);
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        var tokens = new List<(string Word, int Start, int End)>[docs.Count];
        for (var i = 0; i < docs.Count; i++)
        {
            tokens[i] = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Word().Matches(docs[i].Text.ToLowerInvariant()))
            {
                tokens[i].Add((m.Value, m.Index, m.Index + m.Length));
                if (seen.Add(m.Value))
                {
                    df[m.Value] = df.GetValueOrDefault(m.Value) + 1;
                }
            }
        }

        var minDf = 5;
        var maxDf = Math.Max(minDf + 1, docs.Count / 5);
        bool Good(string w) => df.TryGetValue(w, out var d) && d >= minDf && d <= maxDf;

        string[] PickTerms(int count)
        {
            for (var attempt = 0; attempt < 10_000; attempt++)
            {
                var d = rng.Next(docs.Count);
                var good = tokens[d].Select(t => t.Word).Where(Good).Distinct().ToList();
                if (good.Count >= count)
                {
                    return good.OrderBy(_ => rng.Next()).Take(count).ToArray();
                }
            }

            throw new InvalidOperationException("The corpus is too small to generate queries.");
        }

        string[] Phrase()
        {
            for (var attempt = 0; attempt < 100_000; attempt++)
            {
                var d = rng.Next(docs.Count);
                var t = tokens[d];
                if (t.Count < 2)
                {
                    continue;
                }

                var i = rng.Next(t.Count - 1);
                if (Good(t[i].Word) && Good(t[i + 1].Word) && t[i + 1].Start == t[i].End + 1 && docs[d].Text[t[i].End] == ' ')
                {
                    return [t[i].Word, t[i + 1].Word];
                }
            }

            throw new InvalidOperationException("No adjacent term pairs found.");
        }

        string LongTerm(int minLength)
        {
            for (var attempt = 0; attempt < 100_000; attempt++)
            {
                var w = PickTerms(1)[0];
                if (w.Length >= minLength)
                {
                    return w;
                }
            }

            throw new InvalidOperationException("No long terms found.");
        }

        char Other(char c)
        {
            char r;
            do
            {
                r = (char)('a' + rng.Next(26));
            }
            while (r == c);
            return r;
        }

        var lengths = docs.Select(d => d.Length).OrderBy(x => x).ToArray();
        var median = lengths[lengths.Length / 2];
        var topCategories = docs.GroupBy(d => d.Category).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(10).Select(g => g.Key).ToArray();

        HarnessQuery[] Make(Func<HarnessQuery> f) => Enumerable.Range(0, perRow).Select(_ => f()).ToArray();
        var result = new Dictionary<RowKind, HarnessQuery[]>
        {
            [RowKind.Terms1] = Make(() => new TermsQuery(PickTerms(1))),
            [RowKind.Terms2] = Make(() => new TermsQuery(PickTerms(2))),
            [RowKind.Terms5] = Make(() => new TermsQuery(PickTerms(5))),
            [RowKind.Phrase] = Make(() => new PhraseQuery(Phrase())),
            [RowKind.Prefix] = Make(() => new PrefixQuery([], LongTerm(5)[..3])),
            [RowKind.AsYouType] = Make(() =>
            {
                var req = PickTerms(1);
                var partial = LongTerm(6);
                return new PrefixQuery(req, partial[..Math.Min(4, partial.Length - 2)]);
            }),
            [RowKind.Fuzzy1] = Make(() =>
            {
                var w = LongTerm(5).ToCharArray();
                var p = rng.Next(w.Length);
                w[p] = Other(w[p]);
                return new FuzzyQuery(new string(w), 1);
            }),
            [RowKind.Fuzzy2] = Make(() =>
            {
                var w = LongTerm(6).ToCharArray();
                var p = rng.Next(w.Length);
                w[p] = Other(w[p]);
                var s = new string(w);
                var q = rng.Next(s.Length);
                return new FuzzyQuery(s.Remove(q, 1), 2);
            }),
            [RowKind.Filtered] = Make(() => new FilteredQuery(PickTerms(2), median / 2, median * 2, topCategories.OrderBy(_ => rng.Next()).Take(3).ToArray())),
            [RowKind.Facets] = Make(() => new FacetQuery(PickTerms(2))),
            [RowKind.Stats] = Make(() => new StatsQuery(PickTerms(2))),
            [RowKind.Sort] = Make(() => new SortedQuery(PickTerms(2))),
            [RowKind.FunctionScore] = Make(() => new DecayedQuery(PickTerms(2), median, Math.Max(1, median))),
            // A centre of the synthetic clusters (or a random place) and a radius from 1 km to 1,000 km, log-uniform.
            [RowKind.Geo] = Make(() =>
            {
                var (la, lo) = rng.Next(4) == 0 ? (Math.Asin((rng.NextDouble() * 2) - 1) * 180 / Math.PI, (rng.NextDouble() * 360) - 180) : GeoSynth.Centres[rng.Next(GeoSynth.Centres.Length)];
                return new GeoQuery(la, lo, 1000 * Math.Pow(1000, rng.NextDouble()));
            }),
        };
        return result;
    }

    /// <summary>Writes the query sets as JSON for the native runners: per row, the query objects with the fields they need.</summary>
    public static void WriteJson(Dictionary<RowKind, HarnessQuery[]> queries, string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        foreach (var (row, list) in queries)
        {
            w.WriteStartArray(row.ToString());
            foreach (var q in list)
            {
                w.WriteStartObject();
                switch (q)
                {
                    case TermsQuery t: Strings(w, "terms", t.Terms); break;
                    case PhraseQuery p: Strings(w, "terms", p.Terms); break;
                    case PrefixQuery px: Strings(w, "required", px.Required); w.WriteString("prefix", px.Prefix); break;
                    case FuzzyQuery f: w.WriteString("term", f.Term); w.WriteNumber("edits", f.MaxEdits); break;
                    case FilteredQuery fq: Strings(w, "terms", fq.Terms); w.WriteNumber("min", fq.MinLength); w.WriteNumber("max", fq.MaxLength); Strings(w, "categories", fq.Categories); break;
                    case FacetQuery fc: Strings(w, "terms", fc.Terms); break;
                    case StatsQuery sq: Strings(w, "terms", sq.Terms); break;
                    case SortedQuery so: Strings(w, "terms", so.Terms); break;
                    case DecayedQuery dq: Strings(w, "terms", dq.Terms); w.WriteNumber("origin", dq.Origin); w.WriteNumber("scale", dq.Scale); break;
                    case GeoQuery g: w.WriteNumber("lat", g.Latitude); w.WriteNumber("lng", g.Longitude); w.WriteNumber("meters", g.RadiusMeters); break;
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void Strings(Utf8JsonWriter w, string name, string[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }
}
