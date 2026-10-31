namespace OneIndex.Search;

/// <summary>A query cannot be run against the index: an unknown field, a field of the wrong kind, a bound of the wrong type, a bad pattern or edit distance. The message names the field.</summary>
public class QueryException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong with the query.</param>
    public QueryException(string message)
        : base(message)
    {
    }
}

/// <summary>A prefix, fuzzy or regular-expression query matched more distinct terms than <see cref="QueryEngineOptions.MaxExpandedTerms"/> allows.</summary>
public sealed class TooManyTermsException : QueryException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="field">The field the query ran against.</param>
    /// <param name="limit">The limit that was exceeded.</param>
    public TooManyTermsException(string field, int limit)
        : base($"The query matches more than {limit} distinct terms of field '{field}'. Make it more specific, or choose ExpansionOverflow.KeepMostFrequent to expand only the most frequent terms.")
    {
        Field = field;
        Limit = limit;
    }

    /// <summary>The field the query ran against.</summary>
    public string Field { get; }

    /// <summary>The limit that was exceeded.</summary>
    public int Limit { get; }
}
