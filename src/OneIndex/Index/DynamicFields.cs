using System.Globalization;
using System.Text;
using System.Text.Json;
using OneIndex.Queries;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>The kinds of leaf a JSON field is indexed as. The value is the suffix letter of the derived field's name (docs/dynamic.md).</summary>
internal enum DynamicKind : byte
{
    /// <summary>A string, matched exactly.</summary>
    Keyword = (byte)'k',

    /// <summary>Any JSON number, held as a 64-bit float.</summary>
    Number = (byte)'n',

    /// <summary>true or false.</summary>
    Bool = (byte)'b',

    /// <summary>A string in one of the date formats of the filter grammar, also indexed as a keyword.</summary>
    Date = (byte)'d',
}

/// <summary>
/// Indexing of JSON fields by path. A JSON value is flattened into leaves; each leaf becomes a value of a derived field named
/// <c>field.path#kind</c> (arrays add no path segment, so their elements are several values of one field). The derived fields are
/// appended to the schema as paths appear, so a segment never needs to know a path before it is written, and a segment written
/// before a path appeared simply has no values for it.
/// </summary>
internal static class DynamicFields
{
    public static readonly DynamicKind[] Kinds = [DynamicKind.Keyword, DynamicKind.Number, DynamicKind.Bool, DynamicKind.Date];

    public static string PhysicalName(string logical, DynamicKind kind) => logical + "#" + (char)kind;

    public static FieldType TypeOf(DynamicKind kind) => kind switch
    {
        DynamicKind.Keyword => FieldType.Keyword,
        DynamicKind.Number => FieldType.Fractional,
        DynamicKind.Bool => FieldType.Bool,
        _ => FieldType.Date,
    };

    /// <summary>The derived kind a value of the given kind is queried as, or null when JSON cannot hold it.</summary>
    public static DynamicKind? KindOf(FieldValueKind kind) => kind switch
    {
        FieldValueKind.Text => DynamicKind.Keyword,
        FieldValueKind.Integral or FieldValueKind.Fractional => DynamicKind.Number,
        FieldValueKind.Bool => DynamicKind.Bool,
        FieldValueKind.Date => DynamicKind.Date,
        _ => null,
    };

    /// <summary>Escapes a JSON object key for use as a path segment: a dot or backslash is preceded by a backslash.</summary>
    public static string EscapeKey(string key)
        => key.AsSpan().IndexOfAny('.', '\\') < 0 ? key : key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(".", "\\.", StringComparison.Ordinal);

    public static FieldDefinition Define(string physical, DynamicKind kind, string parent)
        => new(physical, TypeOf(kind)) { Indexed = true, Stored = false, Column = true, Multi = true, DynamicParent = parent };

    /// <summary>One flattened leaf.</summary>
    internal readonly record struct Leaf(string Physical, DynamicKind Kind, string Parent, FieldValue Value);

    /// <summary>
    /// Checks that every string and property name of a JSON value decodes. A document parsed from bytes that are not valid UTF-8 can
    /// reach here with strings that throw when read; refusing it at the write keeps the failure out of the later flush.
    /// </summary>
    /// <param name="root">The value.</param>
    /// <param name="field">The field name, for the message.</param>
    /// <exception cref="ArgumentException">Some string or name is not valid text.</exception>
    public static void EnsureReadable(JsonElement root, string field)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                JsonElement e = pending.Pop();
                switch (e.ValueKind)
                {
                    case JsonValueKind.String:
                        _ = e.GetString();
                        break;
                    case JsonValueKind.Object:
                        foreach (JsonProperty p in e.EnumerateObject())
                        {
                            _ = p.Name;
                            pending.Push(p.Value);
                        }

                        break;
                    case JsonValueKind.Array:
                        foreach (JsonElement item in e.EnumerateArray())
                        {
                            pending.Push(item);
                        }

                        break;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException($"Field '{field}' holds JSON text that is not valid UTF-8.", nameof(root), ex);
        }
    }

    /// <summary>Flattens a JSON value into leaves, within the limits. Leaves over a limit are skipped (they stay in the stored source).</summary>
    public static void Flatten(string field, JsonElement root, IndexLimits limits, List<Leaf> sink)
    {
        int budget = limits.MaxDynamicValues;
        var seen = new HashSet<(string, string)>();
        Walk(root, field, new StringBuilder(), 0, limits, sink, seen, ref budget);
    }

    private static void Walk(JsonElement e, string field, StringBuilder path, int depth, IndexLimits limits, List<Leaf> sink, HashSet<(string, string)> seen, ref int budget)
    {
        if (budget <= 0)
        {
            return;
        }

        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= limits.MaxDynamicDepth)
                {
                    return;
                }

                foreach (JsonProperty p in e.EnumerateObject())
                {
                    if (p.Name.Length == 0)
                    {
                        continue; // an empty key cannot be told apart from no key in a path
                    }

                    int mark = path.Length;
                    if (mark > 0)
                    {
                        path.Append('.');
                    }

                    path.Append(EscapeKey(p.Name));
                    Walk(p.Value, field, path, depth + 1, limits, sink, seen, ref budget);
                    path.Length = mark;
                    if (budget <= 0)
                    {
                        return;
                    }
                }

                return;
            case JsonValueKind.Array:
                if (depth >= limits.MaxDynamicDepth)
                {
                    return;
                }

                foreach (JsonElement item in e.EnumerateArray())
                {
                    Walk(item, field, path, depth + 1, limits, sink, seen, ref budget);
                    if (budget <= 0)
                    {
                        return;
                    }
                }

                return;
            case JsonValueKind.String:
                {
                    string? s = e.GetString();
                    if (string.IsNullOrEmpty(s) || !Indexable(path, limits) || Encoding.UTF8.GetByteCount(s) > limits.MaxDynamicKeywordBytes)
                    {
                        return;
                    }

                    string logical = field + "." + path;
                    Add(sink, seen, logical, DynamicKind.Keyword, field, FieldValue.FromText(s), s, ref budget);
                    if (ValueBinder.TryParseDate(s, out DateTimeOffset when))
                    {
                        Add(sink, seen, logical, DynamicKind.Date, field, FieldValue.FromDate(when), s, ref budget);
                    }

                    return;
                }

            case JsonValueKind.Number:
                if (Indexable(path, limits) && e.TryGetDouble(out double d) && double.IsFinite(d))
                {
                    Add(sink, seen, field + "." + path, DynamicKind.Number, field, FieldValue.FromFloat64(d), BitConverter.DoubleToInt64Bits(d + 0.0).ToString(CultureInfo.InvariantCulture), ref budget);
                }

                return;
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (Indexable(path, limits))
                {
                    bool b = e.ValueKind == JsonValueKind.True;
                    Add(sink, seen, field + "." + path, DynamicKind.Bool, field, FieldValue.FromBool(b), b ? "1" : "0", ref budget);
                }

                return;
            default:
                return; // null (and undefined): a missing value
        }
    }

    private static bool Indexable(StringBuilder path, IndexLimits limits)
    {
        if (path.Length == 0 || path.Length > limits.MaxDynamicPathBytes)
        {
            return false; // a scalar (or array of scalars) at the root has no path to be found by
        }

        for (int i = 0; i < path.Length; i++)
        {
            if (char.IsControl(path[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void Add(List<Leaf> sink, HashSet<(string, string)> seen, string logical, DynamicKind kind, string parent, FieldValue value, string identity, ref int budget)
    {
        string physical = PhysicalName(logical, kind);
        if (seen.Add((physical, identity)))
        {
            sink.Add(new Leaf(physical, kind, parent, value));
            budget--;
        }
    }

    /// <summary>
    /// Prepares documents for a flush: adds each document's JSON leaves as values of derived fields, and appends to the schema the
    /// derived fields not yet in it (within the per-field path limit and the field limit; further paths are not indexed). Documents
    /// without an indexed JSON field are returned as they are.
    /// </summary>
    /// <param name="schema">The schema in force.</param>
    /// <param name="documents">The documents.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="extended">The schema to flush with: <paramref name="schema"/>, or that with derived fields appended.</param>
    /// <returns>The documents to flush.</returns>
    public static IReadOnlyList<Document> Expand(Schema schema, IReadOnlyList<Document> documents, IndexLimits limits, out Schema extended)
    {
        extended = schema;
        var jsonFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldDefinition f in schema.Fields)
        {
            if (f.Type == FieldType.Json && f.Indexed && f.DynamicParent is null)
            {
                jsonFields.Add(f.Name);
            }
        }

        if (jsonFields.Count == 0)
        {
            return documents;
        }

        var leavesOf = new List<Leaf>?[documents.Count];
        var newLeaves = new SortedDictionary<string, Leaf>(StringComparer.Ordinal);
        for (int d = 0; d < documents.Count; d++)
        {
            foreach (DocumentField f in documents[d].Fields)
            {
                if (f.Value.Kind == FieldValueKind.Json && jsonFields.Contains(f.Name))
                {
                    leavesOf[d] ??= [];
                    Flatten(f.Name, f.Value.AsJson(), limits, leavesOf[d]!);
                }
            }

            if (leavesOf[d] is { } leaves)
            {
                foreach (Leaf leaf in leaves)
                {
                    if (schema.GetFieldId(leaf.Physical) < 0)
                    {
                        newLeaves.TryAdd(leaf.Physical, leaf);
                    }
                }
            }
        }

        if (newLeaves.Count > 0)
        {
            var perParent = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (FieldDefinition f in schema.Fields)
            {
                if (f.DynamicParent is { } p)
                {
                    perParent[p] = perParent.GetValueOrDefault(p) + 1;
                }
            }

            int total = schema.Fields.Count;
            var added = new List<FieldDefinition>();
            foreach ((string physical, Leaf leaf) in newLeaves)
            {
                if (total >= limits.MaxFields || perParent.GetValueOrDefault(leaf.Parent) >= limits.MaxDynamicPaths)
                {
                    continue;
                }

                perParent[leaf.Parent] = perParent.GetValueOrDefault(leaf.Parent) + 1;
                total++;
                added.Add(Define(physical, leaf.Kind, leaf.Parent));
            }

            if (added.Count > 0)
            {
                extended = schema.WithAppended(added, limits);
            }
        }

        var result = new List<Document>(documents.Count);
        for (int d = 0; d < documents.Count; d++)
        {
            if (leavesOf[d] is not { Count: > 0 } leaves)
            {
                result.Add(documents[d]);
                continue;
            }

            var copy = new Document();
            foreach (DocumentField f in documents[d].Fields)
            {
                copy.Add(f.Name, f.Value);
            }

            foreach (Leaf leaf in leaves)
            {
                if (extended.GetFieldId(leaf.Physical) >= 0)
                {
                    copy.Add(leaf.Physical, leaf.Value);
                }
            }

            result.Add(copy);
        }

        return result;
    }
}
