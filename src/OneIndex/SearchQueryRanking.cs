using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex;

public sealed partial class SearchQuery<T>
    where T : class
{
    /// <summary>
    /// Ranks the results by bucket sort over ranking rules (Meilisearch's ranking rules, docs/ranking-rules.md) instead of by the BM25F
    /// score: documents are ordered by the first rule, those that tie by the second, and so on, and the relevance score breaks the ties that
    /// remain. A rule is <c>words</c>, <c>typo</c>, <c>proximity</c>, <c>attribute</c>, <c>sort</c> (this search's <c>OrderBy</c>
    /// keys go here), <c>exactness</c>, or <c>field:asc</c> / <c>field:desc</c> for a sortable property. With the <c>words</c> rule a
    /// document matches when it has any of the search words and ranks by how many; without it every word is still required.
    /// The search visits every match, so <see cref="SearchResults{T}.TotalHits"/> is exact. Ranking rules do not apply to hybrid searches.
    /// An empty list turns off the rules the type's settings declare.
    /// </summary>
    /// <param name="rules">The rules in order; <see cref="RankingRule.MeilisearchDefault"/> is Meilisearch's default order.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">A rule is not valid or is listed twice.</exception>
    public SearchQuery<T> RankingRules(params string[] rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        IReadOnlyList<RankingRule> parsed = RankingRule.ParseList(rules);
        return With((_extras ?? QueryExtras.None) with { Rules = parsed });
    }

    /// <summary>Ranks by BM25F with the term-proximity bonus, ignoring the ranking rules the type declares.</summary>
    /// <returns>The search.</returns>
    public SearchQuery<T> RankByRelevance() => With((_extras ?? QueryExtras.None) with { Rules = [] });

    private IReadOnlyList<RankingRule>? ActiveRules()
    {
        if (_extras?.Rules is { } own)
        {
            return own.Count == 0 ? null : own;
        }

        return _index.Mapping.Schema.Settings.RankingRules is { Count: > 0 } declared ? RankingRule.ParseList(declared) : null;
    }

    private SortCriterion[] RuleKeys(IReadOnlyList<RankingRule> rules, IndexSearcher searcher, IReadOnlyList<Search.Query> groups, SortCriterion[] userKeys, out RankingEvaluator evaluator)
    {
        evaluator = new RankingEvaluator(searcher, groups);
        Schema schema = _index.Mapping.Schema;
        var keys = new List<SortCriterion>();
        foreach (RankingRule rule in rules)
        {
            switch (rule.Kind)
            {
                case RankingRuleKind.Sort:
                    keys.AddRange(userKeys);
                    break;
                case RankingRuleKind.Field:
                    if (!schema.TryGetField(rule.Field!, out FieldDefinition? def) || !def.Column || def.Type is FieldType.Text or FieldType.GeoPoint or FieldType.Bytes or FieldType.Vector or FieldType.Json)
                    {
                        throw new ArgumentException($"Ranking rule '{rule}': '{rule.Field}' is not a sortable property; mark a keyword, bool, numeric or date property [Sortable].", nameof(rules));
                    }

                    keys.Add(new FieldSort(rule.Field!) { Direction = rule.Direction });
                    break;
                default:
                    keys.Add(new RuleSort(rule.Kind, evaluator));
                    break;
            }
        }

        // Documents that no rule separates keep the order of their relevance.
        keys.Add(new ScoreSort());
        if (keys.Count > 16)
        {
            throw new InvalidOperationException("Ranking rules and the ordering together have at most 16 keys.");
        }

        return [.. keys];
    }

    private static Explanation RuleExplanation(SortCriterion[] keys, FieldDoc doc, int groups, Explanation text)
    {
        var details = new List<Explanation>(keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            SortValue v = doc.Values[i];
            switch (keys[i])
            {
                case RuleSort r:
                    details.Add(new Explanation(v.Number, RankingEvaluator.Describe(r.Kind, v.Number, groups)));
                    break;
                case FieldSort f:
                    details.Add(new Explanation(v.IsMissing || v.IsText ? 0f : v.Number, $"{f.Field}:{(f.Direction == SortDirection.Ascending ? "asc" : "desc")} = {(v.IsMissing ? "missing" : v.IsText ? System.Text.Encoding.UTF8.GetString(v.Text) : v.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))}"));
                    break;
                case ScoreSort:
                    details.Add(text);
                    break;
                default:
                    details.Add(new Explanation(0f, "sort key " + keys[i].GetType().Name));
                    break;
            }
        }

        return new Explanation(doc.Score, "ranking rules in order (smaller buckets first), then the text score; the root value is the text score", [.. details]);
    }
}
