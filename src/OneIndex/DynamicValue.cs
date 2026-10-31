using System.Text;
using System.Text.Json;
using OneIndex.Queries;

namespace OneIndex;

/// <summary>
/// Reaches a path inside a <c>[Dynamic]</c> JSON property in <c>Where</c> expressions: <c>x.Meta.Path("customer.age") &gt;= 18</c>.
/// </summary>
public static class JsonPathExtensions
{
    /// <summary>Selects the values at a path of a JSON value (see docs/dynamic.md for the path syntax).</summary>
    /// <param name="json">The JSON value, in a <c>Where</c> expression the <c>[Dynamic]</c> property of the document.</param>
    /// <param name="path">Object keys joined by dots, such as <c>customer.address.city</c>. A dot inside a key is written <c>\.</c> and a backslash <c>\\</c>. Arrays take no part in the path: their elements count as several values at the path.</param>
    /// <returns>The values at the path, to compare with a string, number, bool or date.</returns>
    public static DynamicValue Path(this JsonElement json, string path) => new(json, path);
}

/// <summary>
/// The values found at a path of a JSON value. Comparing them with a string, a number, a boolean or a date is true when <i>any</i>
/// value at the path of that kind satisfies the comparison (arrays hold several values). In a <c>Where</c> expression the
/// comparison becomes a filter on the indexed path; evaluated directly, it gives the same answer, so a predicate can be checked
/// against plain objects. A value of another kind never matches, so <c>!=</c> is simply the opposite of <c>==</c> and also holds for
/// documents without the path.
/// </summary>
public readonly struct DynamicValue : IEquatable<DynamicValue>
{
    private readonly JsonElement _root;

    /// <summary>Selects the values at a path.</summary>
    /// <param name="root">The JSON value.</param>
    /// <param name="path">The path (see <see cref="JsonPathExtensions.Path"/>).</param>
    public DynamicValue(JsonElement root, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _root = root;
        PathText = path;
    }

    /// <summary>The path text.</summary>
    public string PathText { get; }

    /// <summary>Whether the path holds at least one string, number or boolean (a null, an empty array or an object is not a value).</summary>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool Exists()
    {
        foreach (JsonElement leaf in Leaves())
        {
            if (leaf.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any string value at the path equals the text exactly.</summary>
    /// <param name="value">The value.</param>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when one does.</returns>
    public static bool operator ==(DynamicValue value, string? text) => text is not null && value.AnyText(s => string.Equals(s, text, StringComparison.Ordinal));

    /// <summary>Whether no string value at the path equals the text.</summary>
    /// <param name="value">The value.</param>
    /// <param name="text">The text.</param>
    /// <returns>The opposite of <c>==</c>.</returns>
    public static bool operator !=(DynamicValue value, string? text) => !(value == text);

    /// <summary>Whether any string value at the path is after the text in UTF-8 byte order.</summary>
    /// <param name="value">The value.</param>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator >(DynamicValue value, string text) => value.AnyText(s => Compare(s, text) > 0);

    /// <summary>Whether any string value at the path is before the text in UTF-8 byte order.</summary>
    /// <param name="value">The value.</param>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator <(DynamicValue value, string text) => value.AnyText(s => Compare(s, text) < 0);

    /// <summary>Whether any string value at the path is the text or after it.</summary>
    /// <param name="value">The value.</param>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator >=(DynamicValue value, string text) => value.AnyText(s => Compare(s, text) >= 0);

    /// <summary>Whether any string value at the path is the text or before it.</summary>
    /// <param name="value">The value.</param>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator <=(DynamicValue value, string text) => value.AnyText(s => Compare(s, text) <= 0);

    /// <summary>Whether any number at the path equals the number.</summary>
    /// <param name="value">The value.</param>
    /// <param name="number">The number.</param>
    /// <returns><see langword="true"/> when one does.</returns>
    public static bool operator ==(DynamicValue value, double number) => value.AnyNumber(n => n == number);

    /// <summary>Whether no number at the path equals the number.</summary>
    /// <param name="value">The value.</param>
    /// <param name="number">The number.</param>
    /// <returns>The opposite of <c>==</c>.</returns>
    public static bool operator !=(DynamicValue value, double number) => !(value == number);

    /// <summary>Whether any number at the path is above the number.</summary>
    /// <param name="value">The value.</param>
    /// <param name="number">The number.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator >(DynamicValue value, double number) => value.AnyNumber(n => n > number);

    /// <summary>Whether any number at the path is below the number.</summary>
    /// <param name="value">The value.</param>
    /// <param name="number">The number.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator <(DynamicValue value, double number) => value.AnyNumber(n => n < number);

    /// <summary>Whether any number at the path is at least the number.</summary>
    /// <param name="value">The value.</param>
    /// <param name="number">The number.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator >=(DynamicValue value, double number) => value.AnyNumber(n => n >= number);

    /// <summary>Whether any number at the path is at most the number.</summary>
    /// <param name="value">The value.</param>
    /// <param name="number">The number.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator <=(DynamicValue value, double number) => value.AnyNumber(n => n <= number);

    /// <summary>Whether any boolean at the path equals the flag.</summary>
    /// <param name="value">The value.</param>
    /// <param name="flag">The flag.</param>
    /// <returns><see langword="true"/> when one does.</returns>
    public static bool operator ==(DynamicValue value, bool flag) => value.Any(e => e.ValueKind == (flag ? JsonValueKind.True : JsonValueKind.False));

    /// <summary>Whether no boolean at the path equals the flag.</summary>
    /// <param name="value">The value.</param>
    /// <param name="flag">The flag.</param>
    /// <returns>The opposite of <c>==</c>.</returns>
    public static bool operator !=(DynamicValue value, bool flag) => !(value == flag);

    /// <summary>Whether any string at the path that reads as a date is that instant (see docs/dynamic.md for the formats).</summary>
    /// <param name="value">The value.</param>
    /// <param name="when">The instant.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator ==(DynamicValue value, DateTimeOffset when) => value.AnyDate(d => d == when);

    /// <summary>Whether no date at the path is that instant.</summary>
    /// <param name="value">The value.</param>
    /// <param name="when">The instant.</param>
    /// <returns>The opposite of <c>==</c>.</returns>
    public static bool operator !=(DynamicValue value, DateTimeOffset when) => !(value == when);

    /// <summary>Whether any date at the path is after the instant.</summary>
    /// <param name="value">The value.</param>
    /// <param name="when">The instant.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator >(DynamicValue value, DateTimeOffset when) => value.AnyDate(d => d > when);

    /// <summary>Whether any date at the path is before the instant.</summary>
    /// <param name="value">The value.</param>
    /// <param name="when">The instant.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator <(DynamicValue value, DateTimeOffset when) => value.AnyDate(d => d < when);

    /// <summary>Whether any date at the path is at or after the instant.</summary>
    /// <param name="value">The value.</param>
    /// <param name="when">The instant.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator >=(DynamicValue value, DateTimeOffset when) => value.AnyDate(d => d >= when);

    /// <summary>Whether any date at the path is at or before the instant.</summary>
    /// <param name="value">The value.</param>
    /// <param name="when">The instant.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public static bool operator <=(DynamicValue value, DateTimeOffset when) => value.AnyDate(d => d <= when);

    /// <inheritdoc/>
    public bool Equals(DynamicValue other) => PathText == other.PathText && _root.Equals(other._root);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DynamicValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(PathText, _root); // in-process only

    private static int Compare(string a, string b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    private bool Any(Func<JsonElement, bool> test)
    {
        foreach (JsonElement leaf in Leaves())
        {
            if (test(leaf))
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyText(Func<string, bool> test) => Any(e => e.ValueKind == JsonValueKind.String && e.GetString() is { } s && test(s));

    private bool AnyNumber(Func<double, bool> test) => Any(e => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double d) && double.IsFinite(d) && test(d));

    private bool AnyDate(Func<DateTimeOffset, bool> test)
        => Any(e => e.ValueKind == JsonValueKind.String && e.GetString() is { } s && ValueBinder.TryParseDate(s, out DateTimeOffset when) && test(when));

    private List<JsonElement> Leaves()
    {
        var sink = new List<JsonElement>();
        Descend(_root, SplitPath(PathText), 0, sink);
        return sink;
    }

    // Arrays are transparent: the rest of the path applies to each element, as when the index flattens them.
    private static void Descend(JsonElement e, List<string> segments, int at, List<JsonElement> sink)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement item in e.EnumerateArray())
                {
                    Descend(item, segments, at, sink);
                }

                return;
            case JsonValueKind.Object:
                if (at == segments.Count)
                {
                    return;
                }

                foreach (JsonProperty p in e.EnumerateObject())
                {
                    if (segments[at].Length > 0 && string.Equals(p.Name, segments[at], StringComparison.Ordinal))
                    {
                        Descend(p.Value, segments, at + 1, sink);
                    }
                }

                return;
            case JsonValueKind.Undefined:
                return;
            default:
                if (at == segments.Count)
                {
                    sink.Add(e);
                }

                return;
        }
    }

    internal static List<string> SplitPath(string path)
    {
        var segments = new List<string>();
        if (path.Length == 0)
        {
            return segments;
        }

        var current = new StringBuilder();
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c == '\\' && i + 1 < path.Length && path[i + 1] is '.' or '\\')
            {
                current.Append(path[++i]);
            }
            else if (c == '.')
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        segments.Add(current.ToString());
        return segments;
    }
}
