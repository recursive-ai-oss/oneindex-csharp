using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>
/// A description of what to find, as data. Queries do nothing by themselves: the query engine turns one into a
/// <see cref="Weight"/> through a <see cref="QueryVisitor{TResult}"/>, which keeps the kinds here stable while execution evolves
/// elsewhere. Every query kind has a <see cref="Boost"/> that multiplies its score contribution.
/// </summary>
public abstract record Query
{
    /// <summary>Multiplies the query's score. Default 1; finite and not negative.</summary>
    public float Boost { get; init; } = 1f;

    /// <summary>Dispatches to the visitor method for this kind of query.</summary>
    /// <typeparam name="TResult">What the visitor produces.</typeparam>
    /// <param name="visitor">The visitor.</param>
    /// <returns>The visitor's result.</returns>
    public abstract TResult Accept<TResult>(QueryVisitor<TResult> visitor);
}

/// <summary>Walks a query. Override the kinds you handle; the rest reach <see cref="VisitUnknown"/>, which throws.</summary>
/// <typeparam name="TResult">What the visitor produces.</typeparam>
public abstract class QueryVisitor<TResult>
{
    /// <summary>Visits a <see cref="TermQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitTerm(TermQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="BooleanQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitBoolean(BooleanQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="PhraseQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitPhrase(PhraseQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="PrefixQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitPrefix(PrefixQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="FuzzyQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitFuzzy(FuzzyQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="RangeQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitRange(RangeQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="MatchAllQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitMatchAll(MatchAllQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="MultiFieldTermQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitMultiFieldTerm(MultiFieldTermQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="RegexQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitRegex(RegexQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="GeoRadiusQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitGeoRadius(GeoRadiusQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="GeoBoundingBoxQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitGeoBoundingBox(GeoBoundingBoxQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="FunctionScoreQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitFunctionScore(FunctionScoreQuery query) => VisitUnknown(query);

    /// <summary>Visits a typo-tolerant term query.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitTypoTerm(TypoTermQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="KnnQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitKnn(KnnQuery query) => VisitUnknown(query);

    /// <summary>Visits a <see cref="HybridQuery"/>.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The result.</returns>
    public virtual TResult VisitHybrid(HybridQuery query) => VisitUnknown(query);

    /// <summary>Called for a kind the visitor does not handle.</summary>
    /// <param name="query">The query.</param>
    /// <returns>Never returns normally.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    protected virtual TResult VisitUnknown(Query query) => throw new NotSupportedException($"{GetType().Name} does not handle {query.GetType().Name}.");
}

/// <summary>Documents whose field contains the exact term. The term is already analyzed: it is compared as given.</summary>
/// <param name="Field">The field name.</param>
/// <param name="Term">The term text.</param>
public sealed record TermQuery(string Field, string Term) : Query
{
    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitTerm(this);
}

/// <summary>How a clause takes part in a <see cref="BooleanQuery"/>.</summary>
public enum Occur : byte
{
    /// <summary>The document must match, and the clause scores.</summary>
    Must,

    /// <summary>The document may match; matching raises its score. With no Must clause at least one Should must match (or <see cref="BooleanQuery.MinimumShouldMatch"/>).</summary>
    Should,

    /// <summary>The document must not match.</summary>
    MustNot,

    /// <summary>The document must match, but the clause does not score.</summary>
    Filter,
}

/// <summary>One clause of a boolean query.</summary>
/// <param name="Query">The nested query.</param>
/// <param name="Occur">How it takes part.</param>
public readonly record struct BooleanClause(Query Query, Occur Occur);

/// <summary>Combines queries: all Must and Filter clauses match, no MustNot clause matches, and enough Should clauses match.</summary>
/// <param name="Clauses">The clauses, in order.</param>
public sealed record BooleanQuery(IReadOnlyList<BooleanClause> Clauses) : Query
{
    /// <summary>How many Should clauses must match. 0 (default) means one when the query has no Must or Filter clause, none otherwise.</summary>
    public int MinimumShouldMatch { get; init; }

    /// <summary>Creates a boolean query from clauses.</summary>
    /// <param name="clauses">The clauses.</param>
    /// <returns>The query.</returns>
    public static BooleanQuery Of(params BooleanClause[] clauses) => new(clauses);

    /// <summary>Compares clauses one by one (the generated record equality would compare the list by reference).</summary>
    /// <param name="other">The other query.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(BooleanQuery? other)
        => other is not null && Boost.Equals(other.Boost) && MinimumShouldMatch == other.MinimumShouldMatch && Clauses.SequenceEqual(other.Clauses);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Boost);
        hash.Add(MinimumShouldMatch);
        foreach (BooleanClause c in Clauses)
        {
            hash.Add(c);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitBoolean(this);
}

/// <summary>Documents where the terms occur in order, each at most <see cref="Slop"/> position moves from an exact phrase.</summary>
/// <param name="Field">The field name.</param>
/// <param name="Terms">The analyzed terms in phrase order.</param>
public sealed record PhraseQuery(string Field, IReadOnlyList<string> Terms) : Query
{
    /// <summary>The number of position moves allowed to line the terms up. 0 (default) is an exact phrase.</summary>
    public int Slop { get; init; }

    /// <summary>Compares terms one by one (the generated record equality would compare the list by reference).</summary>
    /// <param name="other">The other query.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(PhraseQuery? other)
        => other is not null && Field == other.Field && Slop == other.Slop && Boost.Equals(other.Boost) && Terms.SequenceEqual(other.Terms);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Field);
        hash.Add(Slop);
        hash.Add(Boost);
        foreach (string t in Terms)
        {
            hash.Add(t);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitPhrase(this);
}

/// <summary>
/// Documents whose field contains a term that starts with the prefix. By default every match scores the boost and nothing else (a constant
/// score, which is also what makes a short prefix cheap: no term is weighed and there is no limit on the terms it matches). With
/// <see cref="ScoreTerms"/> the matches are scored as a disjunction of the expanded terms' BM25F scores.
/// </summary>
/// <param name="Field">The field name.</param>
/// <param name="Prefix">The prefix, already analyzed.</param>
public sealed record PrefixQuery(string Field, string Prefix) : Query
{
    /// <summary>
    /// Whether each matching term is scored with BM25F (default <see langword="false"/>: constant score). Scored prefixes expand to at
    /// most <see cref="QueryEngineOptions.MaxExpandedTerms"/> terms, with the overflow behaviour of <see cref="QueryEngineOptions.Overflow"/>.
    /// </summary>
    public bool ScoreTerms { get; init; }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitPrefix(this);
}

/// <summary>
/// Documents whose field contains a term within <see cref="MaxEdits"/> edits of the given term. Only the <see cref="MaxExpansions"/>
/// closest terms are kept (fewest edits first, then the most documents, then term bytes), so a loose query never fails and never weighs
/// thousands of terms; each kept term is scored with BM25F.
/// </summary>
/// <param name="Field">The field name.</param>
/// <param name="Term">The term, already analyzed.</param>
public sealed record FuzzyQuery(string Field, string Term) : Query
{
    /// <summary>The largest edit distance, 0 to 2. Default 2.</summary>
    public int MaxEdits { get; init; } = 2;

    /// <summary>How many leading characters must match exactly. Default 0.</summary>
    public int PrefixLength { get; init; }

    /// <summary>The most terms to keep, 1 or more. Default 50. Distances are counted as optimal string alignment (a swap of adjacent characters is one edit).</summary>
    public int MaxExpansions { get; init; } = 50;

    /// <summary>Whether swapping two adjacent characters counts as one edit. Default true.</summary>
    public bool Transpositions { get; init; } = true;

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitFuzzy(this);
}

/// <summary>Documents whose field value lies between two bounds. A missing bound is open.</summary>
/// <param name="Field">The field name; a column or point field of a numeric, date or keyword type.</param>
/// <param name="Lower">The lower bound, or <see langword="null"/> for none.</param>
/// <param name="Upper">The upper bound, or <see langword="null"/> for none.</param>
public sealed record RangeQuery(string Field, FieldValue? Lower, FieldValue? Upper) : Query
{
    /// <summary>Whether a value equal to the lower bound matches. Default true.</summary>
    public bool IncludeLower { get; init; } = true;

    /// <summary>Whether a value equal to the upper bound matches. Default true.</summary>
    public bool IncludeUpper { get; init; } = true;

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitRange(this);
}

/// <summary>Every document.</summary>
public sealed record MatchAllQuery : Query
{
    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitMatchAll(this);
}

/// <summary>
/// Documents that contain the exact term in any of several fields, scored as one BM25F term: the fields' weights and length
/// normalizations are combined before saturation (Robertson and Zaragoza 2009, section 3.6, equations 3.19 to 3.21), not summed after it.
/// </summary>
/// <param name="Fields">The field names; each uses its schema boost as its weight.</param>
/// <param name="Term">The term text, already analyzed.</param>
public sealed record MultiFieldTermQuery(IReadOnlyList<string> Fields, string Term) : Query
{
    /// <summary>Compares fields one by one (the generated record equality would compare the list by reference).</summary>
    /// <param name="other">The other query.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(MultiFieldTermQuery? other)
        => other is not null && Term == other.Term && Boost.Equals(other.Boost) && Fields.SequenceEqual(other.Fields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Term);
        hash.Add(Boost);
        foreach (string f in Fields)
        {
            hash.Add(f);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitMultiFieldTerm(this);
}

/// <summary>Documents whose field contains a term that a regular expression matches in full. The syntax is the one in the term dictionary notes.</summary>
/// <param name="Field">The field name.</param>
/// <param name="Pattern">The regular expression; it must match the whole term.</param>
public sealed record RegexQuery(string Field, string Pattern) : Query
{
    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitRegex(this);
}

/// <summary>
/// Documents with a geo point within a distance of a centre, measured along the great circle on a spherical Earth
/// (<see cref="GeoMath.HaversineMeters"/>). A document with several points matches when any is inside. Matches score as the boost.
/// </summary>
/// <param name="Field">The geo point field; it must be indexed and keep a column.</param>
/// <param name="Latitude">The centre latitude in degrees, -90 to 90.</param>
/// <param name="Longitude">The centre longitude in degrees, -180 to 180.</param>
/// <param name="RadiusMeters">The radius in metres, 0 or more and finite. A point exactly this far away is inside.</param>
public sealed record GeoRadiusQuery(string Field, double Latitude, double Longitude, double RadiusMeters) : Query
{
    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitGeoRadius(this);
}

/// <summary>
/// Documents with a geo point inside a latitude and longitude rectangle, edges included. When <paramref name="West"/> is greater than
/// <paramref name="East"/> the rectangle runs east from <paramref name="West"/> across the antimeridian to <paramref name="East"/>.
/// A rectangle from -180 to 180 spans every longitude. Matches score as the boost.
/// </summary>
/// <param name="Field">The geo point field; it must be indexed and keep a column.</param>
/// <param name="North">The northern edge, -90 to 90 and not below <paramref name="South"/>.</param>
/// <param name="South">The southern edge, -90 to 90.</param>
/// <param name="West">The western edge, -180 to 180.</param>
/// <param name="East">The eastern edge, -180 to 180.</param>
public sealed record GeoBoundingBoxQuery(string Field, double North, double South, double West, double East) : Query
{
    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitGeoBoundingBox(this);
}

/// <summary>
/// One analyzed query word, matched in one or more fields as itself, with typos, and optionally as the beginning of longer words
/// (as-you-type). Every indexed word that matches is scored like a term query and multiplied by its typo and exactness factors
/// (<see cref="TypoMatch"/>); with no typos allowed and no prefix this is the same as <see cref="MultiFieldTermQuery"/>.
/// </summary>
/// <param name="Fields">The fields to search; they are scored together as one BM25F term. Must be text fields and not repeat.</param>
/// <param name="Term">The analyzed query word.</param>
public sealed record TypoTermQuery(IReadOnlyList<string> Fields, string Term) : Query
{
    /// <summary>The most typos a matching word may have, 0 to 2. Default 2.</summary>
    public int MaxTypos { get; init; } = 2;

    /// <summary>Whether words that begin like the query word (with typos allowed in that beginning) match too. Default <see langword="false"/>.</summary>
    public bool Prefix { get; init; }

    /// <summary>Fields of <see cref="Fields"/> in which no typos are allowed (the word and, with <see cref="Prefix"/>, its extensions still match). Default none.</summary>
    public IReadOnlyList<string> ExactFields { get; init; } = [];

    /// <inheritdoc/>
    public bool Equals(TypoTermQuery? other)
        => other is not null && Term == other.Term && MaxTypos == other.MaxTypos && Prefix == other.Prefix && Boost.Equals(other.Boost)
            && Fields.SequenceEqual(other.Fields) && ExactFields.SequenceEqual(other.ExactFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Term);
        hash.Add(MaxTypos);
        hash.Add(Prefix);
        hash.Add(Boost);
        foreach (string f in Fields)
        {
            hash.Add(f);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitTypoTerm(this);
}
