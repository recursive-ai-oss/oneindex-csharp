using System.Globalization;
using OneIndex.Suggest;

namespace OneIndex.Queries;

/// <summary>
/// Text or JSON that is not a valid query. The strict query syntax and the filter string report where the problem is (offset, line and
/// column, what was expected); query JSON reports the JSON path of the offending value. Each parser also has a <c>TryParse</c> that
/// returns this exception's data without throwing.
/// </summary>
public sealed class QueryParseException : FormatException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="detail">What is wrong, without the position.</param>
    /// <param name="offset">Zero-based UTF-16 offset of the problem in the input; 0 for JSON.</param>
    /// <param name="length">How many characters the problem covers; 0 when it is a place (for example the end of the input).</param>
    /// <param name="line">One-based line; 1 for JSON without a position.</param>
    /// <param name="column">One-based column in UTF-16 units.</param>
    /// <param name="expected">What the parser would have accepted here, or <see langword="null"/>.</param>
    /// <param name="path">The JSON path (<c>$.query.must[1].field</c>) for query JSON, otherwise <see langword="null"/>.</param>
    /// <param name="suggestion">A close known name when the problem is a misspelled field or property, otherwise <see langword="null"/>.</param>
    public QueryParseException(string detail, int offset, int length, int line, int column, string? expected = null, string? path = null, string? suggestion = null)
        : base(Compose(detail, offset, line, column, path))
    {
        Detail = detail;
        Offset = offset;
        Length = length;
        Line = line;
        Column = column;
        Expected = expected;
        Path = path;
        Suggestion = suggestion;
    }

    /// <summary>What is wrong, without the position.</summary>
    public string Detail { get; }

    /// <summary>Zero-based UTF-16 offset of the problem in the input. 0 for query JSON errors that have only a path.</summary>
    public int Offset { get; }

    /// <summary>How many characters the problem covers; 0 when it is a place such as the end of the input.</summary>
    public int Length { get; }

    /// <summary>One-based line of the problem. Lines end at <c>\n</c>, <c>\r\n</c> or <c>\r</c>.</summary>
    public int Line { get; }

    /// <summary>One-based column of the problem, counted in UTF-16 code units from the start of its line.</summary>
    public int Column { get; }

    /// <summary>What the parser would have accepted at this position, or <see langword="null"/> when the problem is not a syntax error.</summary>
    public string? Expected { get; }

    /// <summary>The JSON path of the offending value (query JSON only), such as <c>$.query.must[1].field</c>; otherwise <see langword="null"/>.</summary>
    public string? Path { get; }

    /// <summary>A known field or property name that is close to a misspelled one, or <see langword="null"/>.</summary>
    public string? Suggestion { get; }

    private static string Compose(string detail, int offset, int line, int column, string? path)
        => path is not null
            ? string.Create(CultureInfo.InvariantCulture, $"{detail} (at {path})")
            : string.Create(CultureInfo.InvariantCulture, $"{detail} (at offset {offset}, line {line}, column {column})");

    internal static QueryParseException At(string input, int offset, int length, string detail, string? expected = null, string? suggestion = null)
    {
        offset = Math.Clamp(offset, 0, input.Length);
        (int line, int column) = QuerySyntax.LineColumn(input, offset);
        return new QueryParseException(detail, offset, Math.Max(0, Math.Min(length, input.Length - offset)), line, column, expected, null, suggestion);
    }

    internal static QueryParseException AtPath(string path, string detail, string? expected = null, string? suggestion = null)
        => new(detail, 0, 0, 1, 1, expected, path, suggestion);
}

/// <summary>Helpers shared by the query text parsers.</summary>
internal static class QuerySyntax
{
    /// <summary>One-based line and column of an offset. A <c>\r\n</c> pair counts as one line break.</summary>
    internal static (int Line, int Column) LineColumn(string input, int offset)
    {
        int line = 1;
        int lineStart = 0;
        int end = Math.Min(offset, input.Length);
        for (int i = 0; i < end; i++)
        {
            char c = input[i];
            if (c == '\n' || (c == '\r' && !(i + 1 < input.Length && input[i + 1] == '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart + 1);
    }

    /// <summary>
    /// The candidate nearest to an unknown name, or null when none is close. Closeness is the optimal string alignment distance (the same
    /// measure "did you mean" uses), at most a third of the name's length and at most 3; a name that differs only in case always counts.
    /// </summary>
    internal static string? Nearest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        int limit = Math.Clamp(name.Length / 3, 1, 3);
        foreach (string candidate in candidates)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            int d = EditDistance.OptimalStringAlignment(name.ToLowerInvariant(), candidate.ToLowerInvariant(), limit);
            if (d <= limit && d < bestDistance)
            {
                best = candidate;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>Quotes a name for a message.</summary>
    internal static string Q(string s) => s.Length > 40 ? "'" + s[..40] + "...'" : "'" + s + "'";
}
