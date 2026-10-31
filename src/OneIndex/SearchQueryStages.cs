using System.Linq.Expressions;
using OneIndex.Aggregations;
using OneIndex.Highlighting;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex;

/// <summary>What a search computes beyond its page of hits.</summary>
internal sealed record QueryStages(FacetRequest[] Facets, string? Distinct, Aggregation[] Aggregations)
{
    public static readonly QueryStages None = new([], null, []);
}

public sealed partial class SearchQuery<T>
    where T : class
{
    /// <summary>
    /// Counts the values of a property over <em>all</em> matching documents, not only the page: <see cref="SearchResults{T}.Facets"/>
    /// then holds them under the property's name. The property must keep a column (<c>[Facet]</c>). Facets need every match, so
    /// the search visits them all and <see cref="SearchResults{T}.TotalHits"/> becomes exact. Calling it again for another property adds a facet.
    /// </summary>
    /// <typeparam name="TField">The property type; keyword, bool, numeric or date, or a collection of them.</typeparam>
    /// <param name="member">The property, like <c>x =&gt; x.Tags</c>.</param>
    /// <param name="maxValues">The most values returned for this facet, at least 1. Default 100.</param>
    /// <param name="sortBy">Order by value (default) or by count.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The expression is not a plain property, the property keeps no column or cannot be faceted, or it was already added.</exception>
    public SearchQuery<T> Facets<TField>(Expression<Func<T, TField>> member, int maxValues = 100, FacetSort sortBy = FacetSort.Alpha)
    {
        MappedMember m = MemberNames.Resolve(_index.Mapping, member, "Facets");
        var request = new FacetRequest(m.Name) { MaxValues = maxValues, SortBy = sortBy };
        QueryStages stages = _stages ?? QueryStages.None;
        if (Array.Exists(stages.Facets, f => f.Field == m.Name))
        {
            throw new ArgumentException($"Facets: '{m.Name}' was already added.", nameof(member));
        }

        _ = new FacetCollector(_index.Mapping.Schema, [request]); // validates the field and the limits now
        return With(stages with { Facets = [.. stages.Facets, request] });
    }

    /// <summary>
    /// Keeps only the best hit for each value of a property (Meilisearch's distinct attribute): hits are collapsed, groups are ranked by
    /// their best document and paged with <c>Skip</c> and <c>Take</c>. A document with several values is grouped by the first; a
    /// document without one is its own group. <see cref="SearchResults{T}.TotalHits"/> then counts groups, exactly. Memory grows with the number of groups.
    /// </summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="member">The property, like <c>x =&gt; x.Category</c>; it must keep a column.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The expression is not a plain property, or the property keeps no column.</exception>
    public SearchQuery<T> Distinct<TField>(Expression<Func<T, TField>> member)
    {
        MappedMember m = MemberNames.Resolve(_index.Mapping, member, "Distinct");
        if (!m.Column || m.Type is FieldType.Text or FieldType.GeoPoint or FieldType.Bytes or FieldType.Vector or FieldType.Json)
        {
            throw new ArgumentException($"Distinct: '{m.Name}' cannot be collapsed by; mark a keyword, bool, numeric or date property [Filterable] or [Facet].", nameof(member));
        }

        return With((_stages ?? QueryStages.None) with { Distinct = m.Name });
    }

    /// <summary>
    /// Computes aggregations (terms, ranges, histograms, statistics, cardinality, percentiles) over <em>all</em> matching documents:
    /// <see cref="SearchResults{T}.Aggregations"/> holds them by name. Like facets they make the search visit every match.
    /// </summary>
    /// <param name="configure">Adds requests to the builder.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">A request is invalid: unknown or non-column property, wrong type, duplicate name, setting out of range.</exception>
    public SearchQuery<T> Aggregate(Action<AggregationBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AggregationBuilder<T>(_index.Mapping);
        configure(builder);
        QueryStages stages = _stages ?? QueryStages.None;
        Aggregation[] all = [.. stages.Aggregations, .. builder.Build()];
        _ = new AggregationCollector(_index.Mapping.Schema, all); // validates now
        return With(stages with { Aggregations = all });
    }

    private SearchQuery<T> With(QueryStages stages) => new(_index, _text, _query, _filters, _take, _skip, _exact, _highlight, stages);

    private SearchResults<T> ExecuteStages(Search.Query query, Search.Query textQuery, SearchIndex<T>.Generation? generation, CancellationToken cancellationToken)
    {
        QueryStages stages = _stages!;
        Schema schema = _index.Mapping.Schema;
        var collectors = new List<Collector>();
        TopScoreCollector? top = null;
        CountCollector? counter = null;
        DistinctCollector? distinct = null;
        if (stages.Distinct is { } field)
        {
            distinct = new DistinctCollector(field, Math.Max(1, _skip + _take));
            collectors.Add(distinct);
        }
        else if (_take > 0)
        {
            // Facets and aggregations visit every match anyway, so the count is exact at no extra cost.
            top = new TopScoreCollector(_skip + _take, countExactly: true);
            collectors.Add(top);
        }
        else
        {
            counter = new CountCollector();
            collectors.Add(counter);
        }

        FacetCollector? facets = stages.Facets.Length > 0 ? new FacetCollector(schema, stages.Facets) : null;
        if (facets is not null)
        {
            collectors.Add(facets);
        }

        AggregationCollector? aggregations = stages.Aggregations.Length > 0 ? new AggregationCollector(schema, stages.Aggregations) : null;
        if (aggregations is not null)
        {
            collectors.Add(aggregations);
        }

        if (generation is not null)
        {
            generation.Searcher.Search(query, new MultiCollector(collectors), cancellationToken);
        }

        long total;
        IReadOnlyList<ScoreDoc> docs;
        if (distinct is not null)
        {
            DistinctDocs d = distinct.GetTopDocs();
            total = d.TotalGroups;
            docs = d.Hits;
        }
        else if (top is not null)
        {
            TopDocs t = top.GetTopDocs();
            total = t.TotalHits;
            docs = t.Hits;
        }
        else
        {
            total = counter!.Count;
            docs = [];
        }

        var hits = new List<SearchHit<T>>(Math.Max(0, Math.Min(_take, docs.Count - _skip)));
        if (_take > 0 && docs.Count > _skip)
        {
            HighlightTerms terms = HighlightTerms.From(textQuery);
            for (int i = _skip; i < docs.Count && hits.Count < _take; i++)
            {
                ScoreDoc sd = docs[i];
                LeafContext leaf = LeafOf(generation!.Context, sd.DocId);
                Document stored = _index.Source(generation, leaf, sd.DocId - leaf.DocBase, out string id)
                    ?? throw new InvalidOperationException($"The stored source of document '{id}' is not available.");
                hits.Add(new SearchHit<T>(id, sd.Score, _index.Mapping.FromDocument(stored), stored, schema, terms, _highlight));
            }
        }

        var facetResults = new Dictionary<string, FacetResult>(StringComparer.Ordinal);
        if (facets is not null)
        {
            foreach (FacetResult r in facets.GetResults())
            {
                facetResults[r.Field] = r;
            }
        }

        return new SearchResults<T>(hits, total, true)
        {
            Facets = facetResults,
            Aggregations = aggregations?.GetResults() ?? AggregationResultSet.Empty,
        };
    }
}
