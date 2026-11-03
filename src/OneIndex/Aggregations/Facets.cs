using OneIndex.Analysis.Unicode;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Suggest;

namespace OneIndex.Aggregations;

/// <summary>How the values of a facet are ordered before the first <see cref="FacetRequest.MaxValues"/> are kept.</summary>
public enum FacetSort : byte
{
    /// <summary>By value: keywords in ordinal (UTF-16 code unit) order, numbers by magnitude, dates by time, false before true.</summary>
    Alpha,

    /// <summary>Most documents first; equal counts by value.</summary>
    Count,
}

/// <summary>One facet to compute: the counts of a field's values over the matching documents.</summary>
/// <param name="Field">The schema field (keyword, bool, integral, fractional or date) with a column; mark the property <c>[Facet]</c>.</param>
public sealed record FacetRequest(string Field)
{
    /// <summary>The most values returned. Default 100. At least 1.</summary>
    public int MaxValues { get; init; } = 100;

    /// <summary>The order. Default <see cref="FacetSort.Alpha"/>.</summary>
    public FacetSort SortBy { get; init; } = FacetSort.Alpha;
}

/// <summary>A value of a facet and how many matching documents have it.</summary>
/// <param name="Key">The value.</param>
/// <param name="Count">The number of matching documents with the value (a document counts once however often the value is listed).</param>
public readonly record struct FacetValue(BucketKey Key, long Count)
{
    /// <summary>The value as text: the keyword, <c>true</c>/<c>false</c>, an invariant number or an ISO 8601 UTC time.</summary>
    public string Value => Key.Text;
}

/// <summary>The smallest and largest value of a numeric facet over the matching documents.</summary>
/// <param name="Min">The minimum.</param>
/// <param name="Max">The maximum.</param>
public readonly record struct FacetStats(double Min, double Max);

/// <summary>The outcome for one facet.</summary>
public sealed class FacetResult
{
    internal FacetResult(string field, IReadOnlyList<FacetValue> values, long distinctValues, FacetStats? stats)
    {
        Field = field;
        Values = values;
        DistinctValues = distinctValues;
        Stats = stats;
    }

    /// <summary>The field.</summary>
    public string Field { get; }

    /// <summary>The kept values in the requested order.</summary>
    public IReadOnlyList<FacetValue> Values { get; }

    /// <summary>How many distinct values the matching documents have, before <see cref="FacetRequest.MaxValues"/> cut the list.</summary>
    public long DistinctValues { get; }

    /// <summary>Minimum and maximum, for integral and fractional fields with at least one value; otherwise <see langword="null"/>.</summary>
    public FacetStats? Stats { get; }
}

/// <summary>
/// Counts field values over all matching documents (facet distribution) with the shape of Meilisearch's: values with counts, a cap per
/// facet, ordering by value or by count, and minimum and maximum for numeric fields. It is built on <see cref="TermsAggregation"/> and
/// <see cref="StatsAggregation"/> and, like them, needs every match.
/// </summary>
public sealed class FacetCollector : Collector
{
    private readonly FacetRequest[] _requests;
    private readonly FieldType[] _types;
    private readonly AggregationCollector _inner;

    /// <summary>Prepares the collector.</summary>
    /// <param name="schema">The schema of the searched index.</param>
    /// <param name="requests">The facets; fields must be distinct.</param>
    /// <exception cref="ArgumentException">A field is missing, keeps no column or is of a type that cannot be faceted; a field is repeated; <see cref="FacetRequest.MaxValues"/> is below 1.</exception>
    public FacetCollector(Schema schema, IEnumerable<FacetRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(requests);
        _requests = [.. requests];
        _types = new FieldType[_requests.Length];
        var aggregations = new List<Aggregation>();
        for (int i = 0; i < _requests.Length; i++)
        {
            FacetRequest r = _requests[i] ?? throw new ArgumentException("A facet request is null.", nameof(requests));
            if (r.MaxValues < 1)
            {
                throw new ArgumentException($"Facet '{r.Field}': MaxValues must be at least 1.", nameof(requests));
            }

            if (!schema.TryGetField(r.Field, out FieldDefinition? f))
            {
                throw new ArgumentException($"Facet '{r.Field}': the schema has no such field.", nameof(requests));
            }

            _types[i] = f.Type;
            aggregations.Add(new TermsAggregation("v" + i, r.Field) { Size = int.MaxValue, Order = TermsOrder.KeyAscending });
            if (f.Type is FieldType.Integral or FieldType.Fractional)
            {
                aggregations.Add(new StatsAggregation("s" + i, r.Field));
            }
        }

        _inner = new AggregationCollector(schema, aggregations);
    }

    /// <inheritdoc/>
    public override ScoreMode ScoreMode => ScoreMode.CompleteNoScores;

    /// <inheritdoc/>
    public override LeafCollector GetLeafCollector(LeafContext leaf) => _inner.GetLeafCollector(leaf);

    /// <summary>The facets in request order.</summary>
    /// <returns>One result per request.</returns>
    public IReadOnlyList<FacetResult> GetResults()
    {
        AggregationResultSet all = _inner.GetResults();
        var results = new List<FacetResult>(_requests.Length);
        for (int i = 0; i < _requests.Length; i++)
        {
            FacetRequest r = _requests[i];
            IReadOnlyList<Bucket> buckets = all.Get<BucketsResult>("v" + i).Buckets; // key ascending
            var values = new List<FacetValue>(Math.Min(buckets.Count, r.MaxValues));
            if (r.SortBy == FacetSort.Count)
            {
                // OrderByDescending is stable, so equal counts stay in ascending value order.
                buckets = [.. buckets.OrderByDescending(static b => b.DocCount)];
            }

            for (int k = 0; k < buckets.Count && k < r.MaxValues; k++)
            {
                values.Add(new FacetValue(buckets[k].Key, buckets[k].DocCount));
            }

            FacetStats? stats = null;
            if (all.TryGet("s" + i, out AggregationResult? s) && s is StatsResult { Min: { } min, Max: { } max })
            {
                stats = new FacetStats(min, max);
            }

            results.Add(new FacetResult(r.Field, values, buckets.Count, stats));
        }

        return results;
    }
}

/// <summary>
/// Search within the values of one facet (Meilisearch's facet search): which values of a field, among the documents a query matches,
/// match what the user typed, with the number of matching documents for each. Typing is forgiving: values and input are
/// normalized (NFKC, full case folding, accents removed) and split into words; the input matches a value when each input word is a
/// prefix of one of the value's words, or within the typo budget of such a prefix (none up to 4 letters, 1 for 5 to 8, 2 from 9; a
/// swap of two neighbours counts as one), or when the whole input is a prefix of the whole value. An empty input matches every value.
/// </summary>
public static class FacetValueSearch
{
    /// <summary>The default number of values returned.</summary>
    public const int DefaultLimit = 100;

    /// <summary>Counts the facet's values over the documents of <paramref name="query"/> and keeps those that match <paramref name="text"/>.</summary>
    /// <param name="searcher">The searcher.</param>
    /// <param name="query">Selects the documents whose values are counted.</param>
    /// <param name="field">The facet field.</param>
    /// <param name="text">What the user typed; may be null or empty.</param>
    /// <param name="limit">The most values returned, at least 1. Default <see cref="DefaultLimit"/>.</param>
    /// <param name="sortBy">The order of the answer; by value (default, as Meilisearch's facet search) or by count.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Matching values in the requested order (<see cref="FacetSort"/>).</returns>
    public static IReadOnlyList<FacetValue> Search(IndexSearcher searcher, Query query, string field, string? text, int limit = DefaultLimit, FacetSort sortBy = FacetSort.Alpha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searcher);
        var collector = new FacetCollector(searcher.Context.Schema, [new FacetRequest(field) { MaxValues = int.MaxValue }]);
        searcher.Search(query, collector, cancellationToken);
        return Match(collector.GetResults()[0].Values, text, limit, sortBy);
    }

    /// <summary>Counts the facet's values and keeps those that match the text (see <see cref="Search(IndexSearcher, Query, string, string?, int, FacetSort, CancellationToken)"/>).</summary>
    /// <param name="searcher">The searcher.</param>
    /// <param name="query">Selects the documents whose values are counted.</param>
    /// <param name="field">The facet field.</param>
    /// <param name="text">What the user typed; may be null or empty.</param>
    /// <param name="limit">The most values returned, at least 1. Default <see cref="DefaultLimit"/>.</param>
    /// <param name="sortBy">The order of the answer.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Matching values in the requested order.</returns>
    public static ValueTask<IReadOnlyList<FacetValue>> SearchAsync(IndexSearcher searcher, Query query, string field, string? text, int limit = DefaultLimit, FacetSort sortBy = FacetSort.Alpha, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Search(searcher, query, field, text, limit, sortBy, cancellationToken));

    /// <summary>Filters counted values by what the user typed (the matching rules of this class).</summary>
    /// <param name="values">The values with their counts.</param>
    /// <param name="text">What the user typed; may be null or empty.</param>
    /// <param name="limit">The most values returned, at least 1.</param>
    /// <param name="sortBy">By value (ordinal, the default) or by count (most documents first, ties by value).</param>
    /// <returns>Matching values in the requested order.</returns>
    public static IReadOnlyList<FacetValue> Match(IReadOnlyList<FacetValue> values, string? text, int limit = DefaultLimit, FacetSort sortBy = FacetSort.Alpha)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        string normalized = Normalize(text ?? string.Empty);
        List<string> words = Words(normalized);
        var hits = new List<FacetValue>();
        foreach (FacetValue v in values)
        {
            if (words.Count == 0 || Matches(v.Value, normalized, words))
            {
                hits.Add(v);
            }
        }

        if (sortBy == FacetSort.Count)
        {
            hits.Sort(static (a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count) : string.CompareOrdinal(a.Value, b.Value));
        }
        else
        {
            hits.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        }
        if (hits.Count > limit)
        {
            hits.RemoveRange(limit, hits.Count - limit);
        }

        return hits;
    }

    /// <summary>The text the matching compares: NFKC, full case folding, accents removed. Exposed so the rule can be tested and reused.</summary>
    /// <param name="text">Any text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var normalizer = new Normalizer();
        char[] a = new char[(text.Length * 18) + 16];
        if (!normalizer.TryNormalize(text, UnicodeForm.Nfkc, a, out int n))
        {
            return text;
        }

        char[] b = new char[(n * CaseFolder.MaxExpansion) + 16];
        int f = CaseFolder.Fold(a.AsSpan(0, n), b);
        char[] c = new char[(f * 18) + 16];
        return normalizer.TryFoldAccents(b.AsSpan(0, f), c, out int m) ? new string(c, 0, m) : new string(b, 0, f);
    }

    private static List<string> Words(string normalized)
    {
        var words = new List<string>();
        int start = -1;
        for (int i = 0; i <= normalized.Length; i++)
        {
            bool inWord = i < normalized.Length && (char.IsLetterOrDigit(normalized[i]) || char.GetUnicodeCategory(normalized[i]) == System.Globalization.UnicodeCategory.NonSpacingMark);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                words.Add(normalized[start..i]);
                start = -1;
            }
        }

        return words;
    }

    private static bool Matches(string value, string normalizedQuery, List<string> queryWords)
    {
        string v = Normalize(value);
        if (queryWords.Count == 0)
        {
            return true;
        }

        if (v.StartsWith(normalizedQuery.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        List<string> valueWords = Words(v);
        foreach (string q in queryWords)
        {
            bool found = false;
            foreach (string w in valueWords)
            {
                if (WordMatches(q, w))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static int Budget(int length) => length >= 9 ? 2 : length >= 5 ? 1 : 0;

    private static bool WordMatches(string q, string w)
    {
        if (w.StartsWith(q, StringComparison.Ordinal))
        {
            return true;
        }

        int budget = Budget(EditDistance.CodePointCount(q));
        if (budget == 0)
        {
            return false;
        }

        // The query is a prefix being typed: compare it with prefixes of the word that are within the budget in length.
        for (int len = Math.Max(1, q.Length - budget); len <= Math.Min(w.Length, q.Length + budget); len++)
        {
            if (EditDistance.OptimalStringAlignment(q, w.AsSpan(0, len), budget) <= budget)
            {
                return true;
            }
        }

        return false;
    }
}
