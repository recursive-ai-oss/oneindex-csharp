using System.Globalization;
using System.Text;
using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>The kinds of ranking rule, with the names and meanings of Meilisearch's documented ranking rules.</summary>
public enum RankingRuleKind : byte
{
    /// <summary>More of the query's words matched first.</summary>
    Words,

    /// <summary>Fewer typos in the matched words first.</summary>
    Typo,

    /// <summary>Matched words closer together first.</summary>
    Proximity,

    /// <summary>Words matched in earlier fields (the order of the type's text properties), and earlier in them, first.</summary>
    Attribute,

    /// <summary>The ordering of the search (<c>OrderBy</c>) applies here. Without one the rule changes nothing.</summary>
    Sort,

    /// <summary>Words matched whole and without typos first.</summary>
    Exactness,

    /// <summary>A column's ascending or descending order (<c>price:asc</c>).</summary>
    Field,
}

/// <summary>
/// One ranking rule of a bucket sort: documents are ordered by the first rule, documents that tie by the second, and so on; the
/// relevance score (BM25F) breaks the ties that remain. Rule semantics are in docs/ranking-rules.md.
/// </summary>
/// <param name="Kind">Which rule.</param>
/// <param name="Field">The column of a <see cref="RankingRuleKind.Field"/> rule; <see langword="null"/> for the others.</param>
/// <param name="Direction">The direction of a <see cref="RankingRuleKind.Field"/> rule.</param>
public sealed record RankingRule(RankingRuleKind Kind, string? Field = null, SortDirection Direction = SortDirection.Ascending)
{
    /// <summary>Meilisearch's default order: words, typo, proximity, attribute, sort, exactness.</summary>
    public static IReadOnlyList<string> MeilisearchDefault { get; } = ["words", "typo", "proximity", "attribute", "sort", "exactness"];

    /// <summary>The most rules one list may hold.</summary>
    public const int MaxRules = 12;

    /// <summary>Whether a property rule can order by this field: a column of a keyword, bool, numeric or date field.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="field">The field named by the rule.</param>
    /// <returns><see langword="true"/> when the field exists, has a column and is not text, geo, bytes, vector or JSON.</returns>
    internal static bool CanOrderBy(Schema schema, string field)
        => schema.TryGetField(field, out FieldDefinition? def) && def.Column && def.Type is not (FieldType.Text or FieldType.GeoPoint or FieldType.Bytes or FieldType.Vector or FieldType.Json);

    /// <summary>The rule as written: <c>words</c>, <c>typo</c>, <c>proximity</c>, <c>attribute</c>, <c>sort</c>, <c>exactness</c> or <c>field:asc</c> / <c>field:desc</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Kind switch
    {
        RankingRuleKind.Words => "words",
        RankingRuleKind.Typo => "typo",
        RankingRuleKind.Proximity => "proximity",
        RankingRuleKind.Attribute => "attribute",
        RankingRuleKind.Sort => "sort",
        RankingRuleKind.Exactness => "exactness",
        _ => Field + (Direction == SortDirection.Ascending ? ":asc" : ":desc"),
    };

    /// <summary>Reads one rule.</summary>
    /// <param name="text">The rule as <see cref="ToString"/> writes it.</param>
    /// <param name="rule">The rule when valid.</param>
    /// <param name="error">Why not, when invalid.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool TryParse(string? text, out RankingRule? rule, out string? error)
    {
        rule = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "A ranking rule is empty.";
            return false;
        }

        switch (text)
        {
            case "words": rule = new RankingRule(RankingRuleKind.Words); return true;
            case "typo": rule = new RankingRule(RankingRuleKind.Typo); return true;
            case "proximity": rule = new RankingRule(RankingRuleKind.Proximity); return true;
            case "attribute": rule = new RankingRule(RankingRuleKind.Attribute); return true;
            case "sort": rule = new RankingRule(RankingRuleKind.Sort); return true;
            case "exactness": rule = new RankingRule(RankingRuleKind.Exactness); return true;
        }

        int colon = text.LastIndexOf(':');
        if (colon > 0 && colon < text.Length - 1)
        {
            string dir = text[(colon + 1)..];
            if (dir is "asc" or "desc")
            {
                rule = new RankingRule(RankingRuleKind.Field, text[..colon], dir == "asc" ? SortDirection.Ascending : SortDirection.Descending);
                return true;
            }
        }

        error = $"'{text}' is not a ranking rule. Use words, typo, proximity, attribute, sort, exactness, or field:asc / field:desc.";
        return false;
    }

    /// <summary>Reads and checks a list of rules: no built-in rule twice, at most <see cref="MaxRules"/>.</summary>
    /// <param name="rules">The rules in order.</param>
    /// <returns>The parsed rules.</returns>
    /// <exception cref="ArgumentException">A rule is invalid or repeated.</exception>
    public static IReadOnlyList<RankingRule> ParseList(IEnumerable<string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var result = new List<RankingRule>();
        foreach (string text in rules)
        {
            if (!TryParse(text, out RankingRule? rule, out string? error))
            {
                throw new ArgumentException(error, nameof(rules));
            }

            if (result.Exists(r => r.Equals(rule)) || (rule!.Kind == RankingRuleKind.Field && result.Exists(r => r.Kind == RankingRuleKind.Field && r.Field == rule.Field)))
            {
                throw new ArgumentException($"The ranking rule '{text}' is listed twice.", nameof(rules));
            }

            result.Add(rule!);
        }

        if (result.Count > MaxRules)
        {
            throw new ArgumentException($"A ranking rule list has at most {MaxRules} rules, got {result.Count}.", nameof(rules));
        }

        return result;
    }
}

/// <summary>A key of the bucket sort that a text rule computes per document (smaller is better).</summary>
internal sealed record RuleSort(RankingRuleKind Kind, RankingEvaluator Evaluator) : SortCriterion
{
    internal override void Describe(StringBuilder into) => into.Append("rule:").Append((int)Kind).Append(';');
}

/// <summary>
/// The text rules of one search against one snapshot: the query's groups (one per user-text word or phrase, each possibly with
/// synonym readings) and the words they resolved to.
/// </summary>
internal sealed class RankingEvaluator
{
    private const int FarApart = 8;
    private const long AttributeRankUnit = 100_000_000;

    private readonly IndexSearcher _searcher;
    private readonly Weight[] _groups;
    private readonly int[] _wordOfGroup;
    private readonly QueryWord[] _words;

    public RankingEvaluator(IndexSearcher searcher, IReadOnlyList<Query> groups)
    {
        _searcher = searcher;
        Fields = new WordFields(searcher.Context.Schema);
        _groups = new Weight[groups.Count];
        _wordOfGroup = new int[groups.Count];
        var words = new List<QueryWord>();
        for (int g = 0; g < groups.Count; g++)
        {
            _groups[g] = searcher.CreateWeight(groups[g], ScoreMode.Complete);
            _wordOfGroup[g] = -1;
            if (WordResolver.PrimaryWord(groups[g]) is { } primary && WordResolver.Resolve(searcher.Context, Fields, primary) is { Terms.Count: > 0 } word)
            {
                _wordOfGroup[g] = words.Count;
                words.Add(word);
            }
        }

        _words = [.. words];
    }

    public WordFields Fields { get; }

    public int GroupCount => _groups.Length;

    public LeafRules ForLeaf(LeafContext leaf) => new(this, leaf);

    internal sealed class LeafRules
    {
        private readonly RankingEvaluator _owner;
        private readonly Scorer?[] _scorers;
        private readonly LeafWordMatcher _matcher;
        private readonly long[] _values = new long[7];
        private readonly bool[] _groupMatched;
        private int _doc = -1;

        public LeafRules(RankingEvaluator owner, LeafContext leaf)
        {
            _owner = owner;
            _scorers = new Scorer?[owner._groups.Length];
            for (int g = 0; g < _scorers.Length; g++)
            {
                _scorers[g] = owner._groups[g].GetScorer(leaf);
            }

            _groupMatched = new bool[_scorers.Length];
            _matcher = new LeafWordMatcher(leaf, owner._words, owner.Fields, positions: true);
        }

        public long Value(RankingRuleKind kind, int doc)
        {
            if (doc != _doc)
            {
                Compute(doc);
                _doc = doc;
            }

            return _values[(int)kind];
        }

        private void Compute(int doc)
        {
            int matchedGroups = 0;
            for (int g = 0; g < _scorers.Length; g++)
            {
                Scorer? s = _scorers[g];
                bool hit = false;
                if (s is not null)
                {
                    int at = s.DocId < doc ? s.Advance(doc) : s.DocId;
                    hit = at == doc;
                }

                _groupMatched[g] = hit;
                if (hit)
                {
                    matchedGroups++;
                }
            }

            _matcher.Load(doc);
            _matcher.EnsurePositions();
            long typos = 0;
            long inexact = 0;
            long attribute = 0;
            var order = new List<int>();
            for (int g = 0; g < _scorers.Length; g++)
            {
                int w = _owner._wordOfGroup[g];
                if (w < 0 || !_matcher.Matched(w))
                {
                    continue;
                }

                order.Add(w);
                int t = _matcher.BestTypos(w);
                typos += t;
                if (t > 0 || !_matcher.BestWhole(w))
                {
                    inexact++;
                }

                for (int f = 0; f < _matcher.FieldCount; f++)
                {
                    if (_matcher.Present(w, f))
                    {
                        ReadOnlySpan<int> p = _matcher.Positions(w, f);
                        attribute += ((long)f * AttributeRankUnit) + (p.Length > 0 ? Math.Min(p[0], 99_999) : 0);
                        break;
                    }
                }
            }

            long proximity = 0;
            for (int i = 1; i < order.Count; i++)
            {
                proximity += PairDistance(order[i - 1], order[i]);
            }

            _values[(int)RankingRuleKind.Words] = _scorers.Length - matchedGroups;
            _values[(int)RankingRuleKind.Typo] = typos;
            _values[(int)RankingRuleKind.Proximity] = proximity;
            _values[(int)RankingRuleKind.Attribute] = attribute;
            _values[(int)RankingRuleKind.Exactness] = inexact;
        }

        // The smallest distance between an occurrence of word a and one of word b in a field that holds both: b after a at a distance
        // d costs d (adjacent = 1), b before a costs d + 1, so query order wins a tie; capped at FarApart, which is also the cost of
        // words that share no field.
        private int PairDistance(int a, int b)
        {
            int best = FarApart;
            for (int f = 0; f < _matcher.FieldCount; f++)
            {
                ReadOnlySpan<int> pa = _matcher.Positions(a, f);
                ReadOnlySpan<int> pb = _matcher.Positions(b, f);
                if (pa.Length == 0 || pb.Length == 0)
                {
                    continue;
                }

                int j = 0;
                foreach (int x in pa)
                {
                    while (j < pb.Length && pb[j] <= x)
                    {
                        j++;
                    }

                    if (j < pb.Length)
                    {
                        best = Math.Min(best, Math.Max(1, pb[j] - x));
                    }

                    if (j > 0)
                    {
                        best = Math.Min(best, x - pb[j - 1] + 1);
                    }
                }
            }

            return Math.Max(1, best);
        }
    }

    /// <summary>The description of a bucket value for explanations.</summary>
    public static string Describe(RankingRuleKind kind, long value, int groups) => kind switch
    {
        RankingRuleKind.Words => $"words: {value.ToString(CultureInfo.InvariantCulture)} of {groups.ToString(CultureInfo.InvariantCulture)} query words missing",
        RankingRuleKind.Typo => $"typo: {value.ToString(CultureInfo.InvariantCulture)} typos in the matched words",
        RankingRuleKind.Proximity => $"proximity: {value.ToString(CultureInfo.InvariantCulture)} summed distance between matched words (adjacent = 1, at most {FarApart.ToString(CultureInfo.InvariantCulture)} each)",
        RankingRuleKind.Attribute => $"attribute: {(value / AttributeRankUnit).ToString(CultureInfo.InvariantCulture)} field rank sum, first positions {(value % AttributeRankUnit).ToString(CultureInfo.InvariantCulture)}",
        RankingRuleKind.Exactness => $"exactness: {value.ToString(CultureInfo.InvariantCulture)} matched words not whole or with typos",
        _ => kind.ToString(),
    };
}
