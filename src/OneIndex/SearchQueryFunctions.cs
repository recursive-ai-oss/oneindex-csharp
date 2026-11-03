using System.Linq.Expressions;
using OneIndex.Aggregations;
using OneIndex.Index;
using OneIndex.Linq;
using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex;

/// <summary>What a search carries beyond its text, filters and paging: filter strings, a function score, explain.</summary>
internal sealed record QueryExtras(Search.Query[] FilterQueries, FunctionSpec? Function, bool Explain, HybridSpec? Hybrid = null, SparseSpec? Sparse = null, RerankSpec? Rerank = null, IReadOnlyList<RankingRule>? Rules = null, MaxSimSpec? MaxSim = null)
{
    public static readonly QueryExtras None = new([], null, false);
}

/// <summary>The settings of <see cref="SearchQuery{T}.ScoreBy"/>.</summary>
internal sealed record FunctionSpec(ScoreFunction[] Functions, FunctionScoreMode ScoreMode, FunctionBoostMode BoostMode, float MaxBoost, float? MinScore);

/// <summary>
/// Collects the scoring functions of <see cref="SearchQuery{T}.ScoreBy"/> with lambdas over the document type. Each method adds one function and
/// returns the builder. A property needs a column: mark it <c>[Filterable]</c>, <c>[Sortable]</c> or <c>[Facet]</c> (<c>[Geo]</c> for distances).
/// The low-level classes (<see cref="ScoreFunction"/>) offer the same functions with every option. Formulas: docs/function-scores.md.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed class FunctionScoreBuilder<T>
    where T : class
{
    private readonly SearchMapping<T> _mapping;
    private readonly List<ScoreFunction> _functions = [];

    internal FunctionScoreBuilder(SearchMapping<T> mapping) => _mapping = mapping;

    /// <summary>How the function values combine. Default <see cref="FunctionScoreMode.Multiply"/>.</summary>
    public FunctionScoreMode ScoreMode { get; set; }

    /// <summary>How the function score and the relevance score combine. Default <see cref="FunctionBoostMode.Multiply"/>.</summary>
    public FunctionBoostMode BoostMode { get; set; }

    /// <summary>The ceiling of the combined function score. Default <see cref="float.MaxValue"/>.</summary>
    public float MaxBoost { get; set; } = float.MaxValue;

    /// <summary>Hits that score below this are dropped. <see langword="null"/> (default) keeps all.</summary>
    public float? MinScore { get; set; }

    internal ScoreFunction[] Build() => [.. _functions];

    /// <summary>Adds a function that is only a weight, for the hits that match <paramref name="when"/> (all hits when <see langword="null"/>).</summary>
    /// <param name="weight">The value, finite and not negative.</param>
    /// <param name="when">Which hits it applies to, as a <c>Where</c>-style predicate.</param>
    /// <returns>The builder.</returns>
    public FunctionScoreBuilder<T> Weight(float weight, Expression<Func<T, bool>>? when = null)
        => Add(new WeightFunction { Weight = weight, Filter = Condition(when) });

    /// <summary>Adds <c>modifier(factor × value)</c> of a numeric, date (milliseconds) or bool property.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="member">The property, like <c>x =&gt; x.Rating</c>.</param>
    /// <param name="factor">Multiplies the value first. Default 1.</param>
    /// <param name="modifier">Applied next. Default none.</param>
    /// <param name="missing">The value for hits that have none; <see langword="null"/> (default) means the function does not apply to them.</param>
    /// <param name="weight">Multiplies the result. Default 1.</param>
    /// <param name="when">Which hits it applies to.</param>
    /// <returns>The builder.</returns>
    public FunctionScoreBuilder<T> FieldValue<TField>(Expression<Func<T, TField>> member, double factor = 1d, FieldValueModifier modifier = FieldValueModifier.None, double? missing = null, float weight = 1f, Expression<Func<T, bool>>? when = null)
        => Add(new FieldValueFactorFunction(Resolve(member, nameof(FieldValue)).Name) { Factor = factor, Modifier = modifier, Missing = missing, Weight = weight, Filter = Condition(when) });

    /// <summary>Adds a decay of a numeric property with its distance from <paramref name="origin"/>: 1 within <paramref name="offset"/>, <paramref name="decay"/> at <paramref name="scale"/> beyond it.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="member">The property.</param>
    /// <param name="shape">The curve.</param>
    /// <param name="origin">The centre.</param>
    /// <param name="scale">Above 0.</param>
    /// <param name="offset">Not negative. Default 0.</param>
    /// <param name="decay">Above 0 and below 1. Default 0.5.</param>
    /// <param name="weight">Multiplies the result. Default 1.</param>
    /// <param name="when">Which hits it applies to.</param>
    /// <returns>The builder.</returns>
    public FunctionScoreBuilder<T> Decay<TField>(Expression<Func<T, TField>> member, DecayShape shape, double origin, double scale, double offset = 0d, double decay = 0.5d, float weight = 1f, Expression<Func<T, bool>>? when = null)
        => Add(new DecayFunction(shape, Resolve(member, nameof(Decay)).Name, origin, scale) { Offset = offset, Decay = decay, Weight = weight, Filter = Condition(when) });

    /// <summary>Adds a decay of a date property with its distance in time from <paramref name="origin"/> (hits with no date score 1).</summary>
    /// <typeparam name="TField">The property type, <see cref="DateTime"/> or <see cref="DateTimeOffset"/> (or nullable).</typeparam>
    /// <param name="member">The property.</param>
    /// <param name="shape">The curve.</param>
    /// <param name="origin">The centre; a <see cref="DateTime"/> without a kind is read as UTC.</param>
    /// <param name="scale">Above zero.</param>
    /// <param name="offset">Not negative. Default none.</param>
    /// <param name="decay">Above 0 and below 1. Default 0.5.</param>
    /// <param name="weight">Multiplies the result. Default 1.</param>
    /// <param name="when">Which hits it applies to.</param>
    /// <returns>The builder.</returns>
    public FunctionScoreBuilder<T> Decay<TField>(Expression<Func<T, TField>> member, DecayShape shape, DateTimeOffset origin, TimeSpan scale, TimeSpan offset = default, double decay = 0.5d, float weight = 1f, Expression<Func<T, bool>>? when = null)
        => Add(new DecayFunction(shape, Resolve(member, nameof(Decay)).Name, origin.ToUnixTimeMilliseconds(), scale.TotalMilliseconds) { Offset = offset.TotalMilliseconds, Decay = decay, Weight = weight, Filter = Condition(when) });

    /// <summary>Adds a decay of a geo property with its great-circle distance in metres from a point.</summary>
    /// <typeparam name="TPoint">The property type, <see cref="GeoPoint"/> or <c>GeoPoint?</c>.</typeparam>
    /// <param name="member">A <see cref="GeoAttribute"/> property.</param>
    /// <param name="shape">The curve.</param>
    /// <param name="latitude">The point's latitude in degrees.</param>
    /// <param name="longitude">The point's longitude in degrees.</param>
    /// <param name="scaleMeters">Above 0.</param>
    /// <param name="offsetMeters">Not negative. Default 0.</param>
    /// <param name="decay">Above 0 and below 1. Default 0.5.</param>
    /// <param name="weight">Multiplies the result. Default 1.</param>
    /// <param name="when">Which hits it applies to.</param>
    /// <returns>The builder.</returns>
    public FunctionScoreBuilder<T> Distance<TPoint>(Expression<Func<T, TPoint>> member, DecayShape shape, double latitude, double longitude, double scaleMeters, double offsetMeters = 0d, double decay = 0.5d, float weight = 1f, Expression<Func<T, bool>>? when = null)
        => Add(new GeoDecayFunction(shape, Resolve(member, nameof(Distance)).Name, latitude, longitude, scaleMeters) { Offset = offsetMeters, Decay = decay, Weight = weight, Filter = Condition(when) });

    /// <summary>Adds any function, such as a <see cref="CustomFunction"/> that runs your code over the segment's columns.</summary>
    /// <param name="function">The function.</param>
    /// <returns>The builder.</returns>
    public FunctionScoreBuilder<T> Add(ScoreFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        _functions.Add(function);
        return this;
    }

    private MappedMember Resolve<TField>(Expression<Func<T, TField>> member, string what)
    {
        MappedMember m = MemberNames.Resolve(_mapping, member, what);
        if (!m.Column)
        {
            throw new ArgumentException($"{what}: '{m.Name}' keeps no column; mark it [Filterable], [Sortable] or [Facet].", nameof(member));
        }

        return m;
    }

    private Search.Query? Condition(Expression<Func<T, bool>>? when) => when is null ? null : new FilterTranslator<T>(_mapping).Translate(when);
}

public sealed partial class SearchQuery<T>
    where T : class
{
    /// <summary>
    /// Keeps only documents that match a filter string, such as <c>Rating &gt;= 4 AND Tags = invoice</c> (docs/filter-strings.md). It selects without
    /// scoring and combines with other <c>Where</c> calls by and. The text is checked against the schema now.
    /// </summary>
    /// <param name="filter">The filter string.</param>
    /// <returns>The narrowed search.</returns>
    /// <exception cref="QueryParseException">The filter is not valid or names an unknown or unfilterable field; the exception says where.</exception>
    public SearchQuery<T> Where(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Search.Query parsed = FilterString.Parse(filter, _index.Mapping.Schema);
        QueryExtras extras = _extras ?? QueryExtras.None;
        return With(extras with { FilterQueries = [.. extras.FilterQueries, parsed] });
    }

    /// <summary>
    /// Changes the relevance score with functions of property values: recency, popularity, distance (docs/function-scores.md). The functions
    /// you add combine into one value that meets the text score as the builder's <see cref="FunctionScoreBuilder{T}.BoostMode"/> says. It does not change
    /// which hits match (unless <see cref="FunctionScoreBuilder{T}.MinScore"/> is set),. It shapes the relevance score, so an <c>OrderBy</c> on a property ignores it (<c>ThenByRelevance</c> still uses it).
    /// A second call replaces the first.
    /// </summary>
    /// <param name="configure">Adds functions to the builder.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">A function is invalid: unknown property or one without a column, a value out of range; the message names it.</exception>
    public SearchQuery<T> ScoreBy(Action<FunctionScoreBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new FunctionScoreBuilder<T>(_index.Mapping);
        configure(builder);
        ScoreFunction[] functions = builder.Build();
        var spec = new FunctionSpec(functions, builder.ScoreMode, builder.BoostMode, builder.MaxBoost, builder.MinScore);
        try
        {
            // Validate now, against the schema, so a mistake is thrown at the call.
            FunctionScoreQuery probe = Wrap(new MatchAllQuery(), spec);
            _ = new QueryEngine().CreateWeight(probe, new SearchContext(_index.Mapping.Schema, []), ScoreMode.Complete);
        }
        catch (QueryException e)
        {
            throw new ArgumentException("ScoreBy: " + e.Message, nameof(configure), e);
        }

        return With((_extras ?? QueryExtras.None) with { Function = spec });
    }

    /// <summary>
    /// Attaches to every returned hit the breakdown of its score (<see cref="SearchHit{T}.Explanation"/>): the text score's parts and, with
    /// <see cref="ScoreBy"/>, each function's value and how they combined. The root value equals <see cref="SearchHit{T}.Score"/>. It costs one extra
    /// evaluation per returned hit.
    /// </summary>
    /// <returns>The search.</returns>
    public SearchQuery<T> Explain() => With((_extras ?? QueryExtras.None) with { Explain = true });

    private SearchQuery<T> With(QueryExtras extras) => new(_index, _text, _query, _filters, _take, _skip, _exact, _highlight, _stages, extras);

    private static FunctionScoreQuery Wrap(Search.Query scoring, FunctionSpec spec)
        => new FunctionScoreQuery(scoring, spec.Functions) { ScoreMode = spec.ScoreMode, BoostMode = spec.BoostMode, MaxBoost = spec.MaxBoost, MinScore = spec.MinScore };

    private Search.Query WithExtras(Search.Query textQuery, bool applyFunction = true)
    {
        Search.Query scoring = applyFunction && _extras?.Function is { } spec ? Wrap(textQuery, spec) : textQuery;
        if (_filters.Length == 0 && (_extras is null || _extras.FilterQueries.Length == 0))
        {
            return scoring;
        }

        var translator = new FilterTranslator<T>(_index.Mapping);
        if (textQuery is KnnQuery knn)
        {
            // Filters of a vector search select the candidates before the nearest are chosen (a full page of neighbours), not after.
            var selected = new List<BooleanClause>();
            if (knn.Filter is not null)
            {
                selected.Add(new BooleanClause(knn.Filter, Occur.Filter));
            }

            foreach (Expression<Func<T, bool>> f in _filters)
            {
                selected.Add(new BooleanClause(translator.Translate(f), Occur.Filter));
            }

            if (_extras is not null)
            {
                foreach (Search.Query f in _extras.FilterQueries)
                {
                    selected.Add(new BooleanClause(f, Occur.Filter));
                }
            }

            KnnQuery filtered = knn with { Filter = selected.Count == 1 ? selected[0].Query : new BooleanQuery(selected) };
            return applyFunction && _extras?.Function is { } fn ? Wrap(filtered, fn) : filtered;
        }

        var clauses = new List<BooleanClause>(_filters.Length + 1) { new(scoring, Occur.Must) };
        foreach (Expression<Func<T, bool>> f in _filters)
        {
            clauses.Add(new BooleanClause(translator.Translate(f), Occur.Filter));
        }

        if (_extras is not null)
        {
            foreach (Search.Query f in _extras.FilterQueries)
            {
                clauses.Add(new BooleanClause(f, Occur.Filter));
            }
        }

        return new BooleanQuery(clauses);
    }

    private Explanation? ExplainHit(SearchIndex<T>.Generation generation, Search.Query query, int docId)
        => _extras is { Explain: true } ? generation.Searcher.Explain(query, docId) : null;
}
