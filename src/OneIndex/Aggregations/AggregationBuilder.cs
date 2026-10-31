using System.Linq.Expressions;
using OneIndex.Index;

namespace OneIndex.Aggregations;

internal static class MemberNames
{
    /// <summary>The mapped member a lambda such as <c>x =&gt; x.Tags</c> reads.</summary>
    public static MappedMember Resolve<T, TField>(SearchMapping<T> mapping, Expression<Func<T, TField>> member, string what)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(member);
        Expression body = member.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            body = u.Operand;
        }

        if (body is MemberExpression { Expression: ParameterExpression p } m && p == member.Parameters[0] && mapping.TryGetMember(m.Member.Name, out MappedMember? mapped))
        {
            return mapped;
        }

        throw new ArgumentException($"{what}: the expression '{member}' must read one attributed property of {typeof(T).Name} directly, like x => x.Tags.", nameof(member));
    }
}

/// <summary>
/// Composes aggregation requests with lambdas over the document type, for <see cref="SearchQuery{T}.Aggregate(Action{AggregationBuilder{T}})"/>.
/// Each method adds one request and returns the builder. A property needs a column: mark it <c>[Facet]</c>, <c>[Filterable]</c> or
/// <c>[Sortable]</c>. The low-level request classes (<see cref="Aggregation"/>) offer the full set of options.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed class AggregationBuilder<T>
    where T : class
{
    private readonly SearchMapping<T> _mapping;
    private readonly List<Aggregation> _items = [];

    internal AggregationBuilder(SearchMapping<T> mapping) => _mapping = mapping;

    internal IReadOnlyList<Aggregation> Build() => _items;

    private string Field<TField>(Expression<Func<T, TField>> member, string what) => MemberNames.Resolve(_mapping, member, what).Name;

    private List<Aggregation> Nested(Action<AggregationBuilder<T>>? sub)
    {
        if (sub is null)
        {
            return [];
        }

        var inner = new AggregationBuilder<T>(_mapping);
        sub(inner);
        return inner._items;
    }

    /// <summary>Adds a <see cref="TermsAggregation"/>.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The property.</param>
    /// <param name="size">How many buckets to return. Default 10.</param>
    /// <param name="order">The bucket order. Default most documents first.</param>
    /// <param name="sub">Adds sub-aggregations computed inside each bucket.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> Terms<TField>(string name, Expression<Func<T, TField>> member, int size = 10, TermsOrder order = TermsOrder.CountDescending, Action<AggregationBuilder<T>>? sub = null)
    {
        _items.Add(new TermsAggregation(name, Field(member, "Terms")) { Size = size, Order = order, SubAggregations = Nested(sub) });
        return this;
    }

    /// <summary>Adds a <see cref="RangeAggregation"/>.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The numeric or date property.</param>
    /// <param name="ranges">The ranges; from inclusive, to exclusive.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> Range<TField>(string name, Expression<Func<T, TField>> member, params AggregationRange[] ranges)
        => Range(name, member, null, ranges);

    /// <summary>Adds a <see cref="RangeAggregation"/> with sub-aggregations.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The numeric or date property.</param>
    /// <param name="sub">Adds sub-aggregations computed inside each bucket.</param>
    /// <param name="ranges">The ranges; from inclusive, to exclusive.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> Range<TField>(string name, Expression<Func<T, TField>> member, Action<AggregationBuilder<T>>? sub, params AggregationRange[] ranges)
    {
        _items.Add(new RangeAggregation(name, Field(member, "Range"), ranges) { SubAggregations = Nested(sub) });
        return this;
    }

    /// <summary>Adds a <see cref="HistogramAggregation"/>.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The numeric property.</param>
    /// <param name="interval">The bucket width.</param>
    /// <param name="sub">Adds sub-aggregations computed inside each bucket.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> Histogram<TField>(string name, Expression<Func<T, TField>> member, double interval, Action<AggregationBuilder<T>>? sub = null)
    {
        _items.Add(new HistogramAggregation(name, Field(member, "Histogram"), interval) { SubAggregations = Nested(sub) });
        return this;
    }

    /// <summary>Adds a <see cref="DateHistogramAggregation"/> with a calendar unit.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The date property.</param>
    /// <param name="interval">The calendar unit, in UTC.</param>
    /// <param name="sub">Adds sub-aggregations computed inside each bucket.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> DateHistogram<TField>(string name, Expression<Func<T, TField>> member, DateInterval interval, Action<AggregationBuilder<T>>? sub = null)
    {
        _items.Add(new DateHistogramAggregation(name, Field(member, "DateHistogram")) { CalendarInterval = interval, SubAggregations = Nested(sub) });
        return this;
    }

    /// <summary>Adds a <see cref="DateHistogramAggregation"/> with a fixed length.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The date property.</param>
    /// <param name="interval">The length, a whole number of milliseconds.</param>
    /// <param name="sub">Adds sub-aggregations computed inside each bucket.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> DateHistogram<TField>(string name, Expression<Func<T, TField>> member, TimeSpan interval, Action<AggregationBuilder<T>>? sub = null)
    {
        _items.Add(new DateHistogramAggregation(name, Field(member, "DateHistogram")) { FixedInterval = interval, SubAggregations = Nested(sub) });
        return this;
    }

    /// <summary>Adds a <see cref="StatsAggregation"/>.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The numeric or date property.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> Stats<TField>(string name, Expression<Func<T, TField>> member)
    {
        _items.Add(new StatsAggregation(name, Field(member, "Stats")));
        return this;
    }

    /// <summary>Adds a <see cref="CardinalityAggregation"/>.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The property.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> Cardinality<TField>(string name, Expression<Func<T, TField>> member)
    {
        _items.Add(new CardinalityAggregation(name, Field(member, "Cardinality")));
        return this;
    }

    /// <summary>Adds a <see cref="PercentilesAggregation"/>.</summary>
    /// <typeparam name="TField">The property type.</typeparam>
    /// <param name="name">The result name.</param>
    /// <param name="member">The numeric or date property.</param>
    /// <param name="percents">The percentiles from 0 to 100; the defaults (1, 5, 25, 50, 75, 95, 99) when none are given.</param>
    /// <returns>The builder.</returns>
    public AggregationBuilder<T> Percentiles<TField>(string name, Expression<Func<T, TField>> member, params double[] percents)
    {
        _items.Add(percents.Length == 0
            ? new PercentilesAggregation(name, Field(member, "Percentiles"))
            : new PercentilesAggregation(name, Field(member, "Percentiles")) { Percents = percents });
        return this;
    }
}
