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

/// <summary>Documents whose field contains a term that starts with the prefix.</summary>
/// <param name="Field">The field name.</param>
/// <param name="Prefix">The prefix, already analyzed.</param>
public sealed record PrefixQuery(string Field, string Prefix) : Query
{
    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitPrefix(this);
}

/// <summary>Documents whose field contains a term within <see cref="MaxEdits"/> edits of the given term.</summary>
/// <param name="Field">The field name.</param>
/// <param name="Term">The term, already analyzed.</param>
public sealed record FuzzyQuery(string Field, string Term) : Query
{
    /// <summary>The largest edit distance, 0 to 2. Default 2.</summary>
    public int MaxEdits { get; init; } = 2;

    /// <summary>How many leading characters must match exactly. Default 0.</summary>
    public int PrefixLength { get; init; }

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
/// normalizations are combined before saturation (Robertson and Zaragoza 2009, section 3.5), not summed after it.
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
