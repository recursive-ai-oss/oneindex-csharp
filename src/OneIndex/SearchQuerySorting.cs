using System.Linq.Expressions;
using OneIndex.Aggregations;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex;

public sealed partial class SearchQuery<T>
    where T : class
{
    /// <summary>
    /// Orders the hits by a property instead of by relevance, replacing any earlier ordering. Documents without a value go last (or first with
    /// <paramref name="missing"/>), whichever the direction. Ties fall to the next <c>ThenBy</c> key and finally to the document identifier in
    /// byte order, so the order is total. A sorted search visits every match, so <see cref="SearchResults{T}.TotalHits"/> is exact and
    /// scores are not computed unless <see cref="OrderByRelevance"/> or <see cref="ThenByRelevance"/> is used.
    /// </summary>
    /// <typeparam name="TKey">The property type; keyword string, bool, numeric or date.</typeparam>
    /// <param name="member">The property, like <c>x =&gt; x.Modified</c>; it must keep a column (<c>[Sortable]</c>, <c>[Filterable]</c> or <c>[Facet]</c>).</param>
    /// <param name="missing">Where documents without a value go. Default last.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The expression is not a plain property, or the property cannot be sorted by.</exception>
    public SearchQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> member, MissingValuePlacement missing = MissingValuePlacement.Last)
        => SortBy(new FieldSort(FieldFor(member, nameof(OrderBy))) { Missing = missing }, replace: true);

    /// <summary>Like <see cref="OrderBy{TKey}"/>, largest first.</summary>
    /// <typeparam name="TKey">The property type.</typeparam>
    /// <param name="member">The property.</param>
    /// <param name="missing">Where documents without a value go. Default last.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The expression is not a plain property, or the property cannot be sorted by.</exception>
    public SearchQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> member, MissingValuePlacement missing = MissingValuePlacement.Last)
        => SortBy(new FieldSort(FieldFor(member, nameof(OrderByDescending))) { Direction = SortDirection.Descending, Missing = missing }, replace: true);

    /// <summary>Adds a key that separates hits the earlier keys tie.</summary>
    /// <typeparam name="TKey">The property type.</typeparam>
    /// <param name="member">The property.</param>
    /// <param name="missing">Where documents without a value go. Default last.</param>
    /// <returns>The search.</returns>
    /// <exception cref="InvalidOperationException">There is no <c>OrderBy</c> before it.</exception>
    /// <exception cref="ArgumentException">The expression is not a plain property, or the property cannot be sorted by.</exception>
    public SearchQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> member, MissingValuePlacement missing = MissingValuePlacement.Last)
        => SortBy(new FieldSort(FieldFor(member, nameof(ThenBy))) { Missing = missing }, replace: false);

    /// <summary>Adds a key, largest first, that separates hits the earlier keys tie.</summary>
    /// <typeparam name="TKey">The property type.</typeparam>
    /// <param name="member">The property.</param>
    /// <param name="missing">Where documents without a value go. Default last.</param>
    /// <returns>The search.</returns>
    /// <exception cref="InvalidOperationException">There is no <c>OrderBy</c> before it.</exception>
    /// <exception cref="ArgumentException">The expression is not a plain property, or the property cannot be sorted by.</exception>
    public SearchQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> member, MissingValuePlacement missing = MissingValuePlacement.Last)
        => SortBy(new FieldSort(FieldFor(member, nameof(ThenByDescending))) { Direction = SortDirection.Descending, Missing = missing }, replace: false);

    /// <summary>Orders by relevance score, best first, replacing any earlier ordering. Usually it follows a coarser key through <see cref="ThenByRelevance"/>.</summary>
    /// <returns>The search.</returns>
    public SearchQuery<T> OrderByRelevance() => SortBy(new ScoreSort(), replace: true);

    /// <summary>Breaks ties of the earlier keys by relevance score, best first.</summary>
    /// <returns>The search.</returns>
    /// <exception cref="InvalidOperationException">There is no <c>OrderBy</c> before it.</exception>
    public SearchQuery<T> ThenByRelevance() => SortBy(new ScoreSort(), replace: false);

    /// <summary>
    /// Orders by distance from a point, replacing any earlier ordering (Meilisearch's <c>_geoPoint</c> sort). The distance is the great-circle
    /// distance in metres to the document's nearest point; <see cref="SearchHit{T}.GeoDistance"/> returns it.
    /// </summary>
    /// <typeparam name="TPoint">The property type, <see cref="GeoPoint"/> or <c>GeoPoint?</c>.</typeparam>
    /// <param name="member">A <see cref="GeoAttribute"/> property.</param>
    /// <param name="latitude">The point's latitude in degrees.</param>
    /// <param name="longitude">The point's longitude in degrees.</param>
    /// <param name="direction">Nearest first (default) or farthest first.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The property is not a geo property.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The coordinates are out of range.</exception>
    public SearchQuery<T> OrderByDistance<TPoint>(Expression<Func<T, TPoint>> member, double latitude, double longitude, SortDirection direction = SortDirection.Ascending)
        => SortBy(DistanceKey(member, latitude, longitude, direction, nameof(OrderByDistance)), replace: true);

    /// <summary>Breaks ties of the earlier keys by distance from a point.</summary>
    /// <typeparam name="TPoint">The property type.</typeparam>
    /// <param name="member">A <see cref="GeoAttribute"/> property.</param>
    /// <param name="latitude">The point's latitude in degrees.</param>
    /// <param name="longitude">The point's longitude in degrees.</param>
    /// <param name="direction">Nearest first (default) or farthest first.</param>
    /// <returns>The search.</returns>
    /// <exception cref="InvalidOperationException">There is no <c>OrderBy</c> before it.</exception>
    /// <exception cref="ArgumentException">The property is not a geo property.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The coordinates are out of range.</exception>
    public SearchQuery<T> ThenByDistance<TPoint>(Expression<Func<T, TPoint>> member, double latitude, double longitude, SortDirection direction = SortDirection.Ascending)
        => SortBy(DistanceKey(member, latitude, longitude, direction, nameof(ThenByDistance)), replace: false);

    /// <summary>
    /// Continues a sorted search after a cursor (Elasticsearch's <c>search_after</c>): the hits that sort strictly after the hit the cursor came from.
    /// Unlike <c>Skip</c> it costs the same on every page and does not repeat or skip documents when others change between pages. Needs an ordering,
    /// and cannot be combined with <c>Skip</c>.
    /// </summary>
    /// <param name="cursor">The <see cref="SearchHit{T}.Cursor"/> of the last hit of the previous page, or <see cref="SearchResults{T}.NextCursor"/>.</param>
    /// <returns>The search.</returns>
    /// <exception cref="InvalidOperationException">The search has no ordering yet.</exception>
    /// <exception cref="ArgumentException">The cursor was made by a different ordering.</exception>
    public SearchQuery<T> SearchAfter(SortCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (ActiveRules() is not null)
        {
            // With ranking rules the cursor belongs to the rules' keys, which exist only once the search runs; the collector checks it then.
            QueryStages ruled = _stages ?? QueryStages.None;
            return With(ruled with { Sort = (ruled.Sort ?? new SortPlan([], null)) with { After = cursor } });
        }

        SortPlan plan = _stages?.Sort ?? throw new InvalidOperationException("SearchAfter needs an ordering: call OrderBy first.");
        if (cursor.Fingerprint != SortSpec.Fingerprint(plan.Keys) || cursor.Values.Count != plan.Keys.Length)
        {
            throw new ArgumentException("The cursor was made by a different ordering.", nameof(cursor));
        }

        return With((_stages ?? QueryStages.None) with { Sort = plan with { After = cursor } });
    }

    /// <summary>Continues a sorted search after a cursor written with <see cref="SortCursor.ToToken"/>.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The token is malformed or was made by a different ordering.</exception>
    /// <exception cref="InvalidOperationException">The search has no ordering yet.</exception>
    public SearchQuery<T> SearchAfter(string token)
        => SortCursor.TryParse(token, out SortCursor? cursor) ? SearchAfter(cursor!) : throw new ArgumentException("The token is not a valid search-after cursor.", nameof(token));

    internal SearchQuery<T> WithKeys(SortCriterion[] keys) => With((_stages ?? QueryStages.None) with { Sort = new SortPlan(keys, null) });

    private string FieldFor<TKey>(Expression<Func<T, TKey>> member, string what)
    {
        MappedMember m = MemberNames.Resolve(_index.Mapping, member, what);
        if (!m.Column || m.Type is FieldType.Text or FieldType.GeoPoint or FieldType.Bytes or FieldType.Vector or FieldType.Json)
        {
            throw new ArgumentException(
                m.Type == FieldType.GeoPoint
                    ? $"{what}: '{m.Name}' is a geo point; use OrderByDistance."
                    : $"{what}: '{m.Name}' cannot be sorted by; mark a keyword, bool, numeric or date property [Sortable].",
                nameof(member));
        }

        return m.Name;
    }

    private GeoDistanceSort DistanceKey<TPoint>(Expression<Func<T, TPoint>> member, double latitude, double longitude, SortDirection direction, string what)
    {
        MappedMember m = MemberNames.Resolve(_index.Mapping, member, what);
        if (m.Type != FieldType.GeoPoint || !m.Column)
        {
            throw new ArgumentException($"{what}: '{m.Name}' is not a geo property; mark a GeoPoint property [Geo].", nameof(member));
        }

        if (!(latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), $"{what}: ({latitude}, {longitude}) is not a valid latitude and longitude.");
        }

        return new GeoDistanceSort(m.Name, latitude, longitude) { Direction = direction };
    }

    private SearchQuery<T> SortBy(SortCriterion key, bool replace)
    {
        QueryStages stages = _stages ?? QueryStages.None;
        if (replace)
        {
            return With(stages with { Sort = new SortPlan([key], null) });
        }

        SortPlan plan = stages.Sort ?? throw new InvalidOperationException("ThenBy needs an ordering: call OrderBy first.");
        if (plan.Keys.Length >= 16)
        {
            throw new InvalidOperationException("A sort has at most 16 keys.");
        }

        return With(stages with { Sort = new SortPlan([.. plan.Keys, key], null) });
    }
}
