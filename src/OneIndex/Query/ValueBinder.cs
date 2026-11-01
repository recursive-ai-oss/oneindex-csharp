using System.Globalization;
using OneIndex.Index;

namespace OneIndex.Queries;

/// <summary>How a literal was written in the query: quoted text, a bare word, a JSON number, a JSON boolean.</summary>
internal enum LiteralKind : byte
{
    Text,
    Bare,
    Number,
    Bool,
}

/// <summary>What a value is used for in a comparison, which decides how a fractional number against an integer field is rounded.</summary>
internal enum BoundRole : byte
{
    Equal,
    Lower,
    Upper,
}

/// <summary>
/// Turns the literal in a filter, a range or a JSON query into the <see cref="FieldValue"/> a field of a given type holds. The rules are
/// the ones the LINQ translator uses, so the same comparison means the same thing whichever surface wrote it: a fractional bound against
/// an integer field is rounded inward (<c>&gt; 2.5</c> means <c>&gt;= 3</c>) and equality with a fractional number matches nothing.
/// </summary>
internal static class ValueBinder
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    /// <summary>Parses a date the way date comparisons do (UTC unless the text carries an offset).</summary>
    internal static bool TryParseDate(string text, out long unixMilliseconds)
    {
        bool ok = DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset when);
        unixMilliseconds = ok ? when.ToUnixTimeMilliseconds() : 0;
        return ok;
    }

    /// <summary>The kinds of value a comparison can use, for messages.</summary>
    internal static string Describe(FieldType type) => type switch
    {
        FieldType.Keyword => "text",
        FieldType.Integral => "an integer",
        FieldType.Fractional => "a number",
        FieldType.Date => "a date such as 2024-01-31 or 2024-01-31T10:00:00Z",
        FieldType.Bool => "true or false",
        _ => type.ToString(),
    };

    /// <summary>The date format written by the JSON and filter printers: UTC, millisecond precision.</summary>
    internal static string FormatDate(FieldValue value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.AsInt64()).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static string FormatNumber(FieldValue value)
        => value.Kind == FieldValueKind.Fractional
            ? value.AsFloat64().ToString("R", CultureInfo.InvariantCulture)
            : value.AsInt64().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Converts a literal. Returns <see langword="null"/> and sets <paramref name="value"/> on success, or a message saying what the field
    /// expects. <paramref name="nothing"/> is set when the comparison can match no document at all (equality of an integer field with a
    /// fractional number).
    /// </summary>
    internal static string? Bind(
        FieldDefinition field,
        LiteralKind kind,
        string text,
        BoundRole role,
        bool lenient,
        out FieldValue value,
        out bool roundedInclusive,
        out bool nothing)
    {
        value = default;
        roundedInclusive = false;
        nothing = false;
        string wrong = $"Field '{field.Name}' holds {Describe(field.Type)}";
        switch (field.Type)
        {
            case FieldType.Keyword:
                if (kind is LiteralKind.Number or LiteralKind.Bool && !lenient)
                {
                    return wrong + "; write the value as a string.";
                }

                value = FieldValue.FromText(text);
                return null;

            case FieldType.Bool:
                if (kind == LiteralKind.Text && !lenient || kind == LiteralKind.Number)
                {
                    return wrong + ".";
                }

                if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    value = FieldValue.FromBool(true);
                    return null;
                }

                if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    value = FieldValue.FromBool(false);
                    return null;
                }

                return wrong + ", not " + QuerySyntax.Q(text) + ".";

            case FieldType.Date:
                if (kind is LiteralKind.Number or LiteralKind.Bool)
                {
                    return wrong + "; write the date as a string.";
                }

                if (DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset when))
                {
                    value = FieldValue.FromDate(when);
                    return null;
                }

                return wrong + ", not " + QuerySyntax.Q(text) + ".";

            case FieldType.Integral:
            case FieldType.Fractional:
                if (kind == LiteralKind.Bool || (kind == LiteralKind.Text && !lenient))
                {
                    return wrong + (kind == LiteralKind.Text ? "; write the value as a number, not a string." : ".");
                }

                if (!IsNumber(text))
                {
                    return wrong + ", not " + QuerySyntax.Q(text) + ".";
                }

                return field.Type == FieldType.Integral
                    ? BindIntegral(field, text, role, out value, out roundedInclusive, out nothing)
                    : BindFractional(field, text, out value);

            default:
                return $"Field '{field.Name}' is {field.Type} and cannot be compared with a value.";
        }
    }

    private static string? BindIntegral(FieldDefinition field, string text, BoundRole role, out FieldValue value, out bool roundedInclusive, out bool nothing)
    {
        value = default;
        roundedInclusive = false;
        nothing = false;
        if (text.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
            {
                value = FieldValue.FromInt64(l);
                return null;
            }

            return $"The number {QuerySyntax.Q(text)} is outside the range of integer field '{field.Name}'.";
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d) || Math.Abs(d) >= 9.2e18)
        {
            return $"The number {QuerySyntax.Q(text)} is outside the range of integer field '{field.Name}'.";
        }

        if (d == Math.Floor(d))
        {
            value = FieldValue.FromInt64((long)d);
            return null;
        }

        switch (role)
        {
            case BoundRole.Equal:
                nothing = true;
                value = FieldValue.FromInt64(0);
                return null;
            case BoundRole.Lower:
                value = FieldValue.FromInt64((long)Math.Ceiling(d));
                break;
            default:
                value = FieldValue.FromInt64((long)Math.Floor(d));
                break;
        }

        roundedInclusive = true;
        return null;
    }

    private static string? BindFractional(FieldDefinition field, string text, out FieldValue value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
        {
            value = FieldValue.FromFloat64(d);
            return null;
        }

        value = default;
        return $"The number {QuerySyntax.Q(text)} is outside the range of field '{field.Name}'.";
    }

    /// <summary>Whether the text is a number in the JSON number syntax (an optional minus, digits, an optional fraction and exponent).</summary>
    internal static bool IsNumber(string s)
    {
        int i = 0;
        int n = s.Length;
        if (i < n && s[i] == '-')
        {
            i++;
        }

        int digits = 0;
        while (i < n && s[i] is >= '0' and <= '9')
        {
            i++;
            digits++;
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < n && s[i] == '.')
        {
            i++;
            int frac = 0;
            while (i < n && s[i] is >= '0' and <= '9')
            {
                i++;
                frac++;
            }

            if (frac == 0)
            {
                return false;
            }
        }

        if (i < n && s[i] is 'e' or 'E')
        {
            i++;
            if (i < n && s[i] is '+' or '-')
            {
                i++;
            }

            int exp = 0;
            while (i < n && s[i] is >= '0' and <= '9')
            {
                i++;
                exp++;
            }

            if (exp == 0)
            {
                return false;
            }
        }

        return i == n;
    }
}
