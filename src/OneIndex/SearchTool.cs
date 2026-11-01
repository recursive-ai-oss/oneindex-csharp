using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex;

/// <summary>
/// Describes a search over documents of a type as a tool an LLM can call: a name, a description and a JSON Schema (draft 2020-12) for the
/// arguments, which are a query JSON document. The schema lists the type's own fields, so a model cannot name a field that does not
/// exist, and it types each value by its field. <see cref="SearchIndex{T}.Parse(string)"/> accepts exactly what the schema describes,
/// and also checks what a schema cannot: that a number is in range, that a date is a date, and that text and regular expressions are
/// well formed. See <c>docs/search-tool.md</c>.
/// </summary>
public static class SearchTool
{
    private static readonly string[] ClauseNames = ["must", "filter", "should", "mustNot"];

    private static readonly string[] BoundNames = ["gt", "gte", "lt", "lte"];

    private static readonly (string, string)[] BoundPairs = [("gt", "gte"), ("lt", "lte")];

    private const string DatePattern = @"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})?)?$";

    /// <summary>Builds the tool definition for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <returns>
    /// A JSON object <c>{"name","description","input_schema"}</c>, the shape tool-calling APIs take (rename <c>input_schema</c> to
    /// <c>parameters</c> where an API wants that). The name is <c>search_</c> plus the type's name.
    /// </returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has no OneIndex mapping.</exception>
    public static string For<T>()
        where T : class
        => For(SearchMappings.Require<T>().Schema, typeof(T).Name);

    /// <summary>Builds only the JSON Schema of the arguments for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <returns>The schema document.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has no OneIndex mapping.</exception>
    public static string SchemaFor<T>()
        where T : class
        => SchemaFor(SearchMappings.Require<T>().Schema);

    /// <summary>Builds the tool definition for a runtime schema.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="name">What the documents are called; the tool is named <c>search_</c> plus this, with characters tool names cannot hold replaced by <c>_</c>, at most 64 characters in all.</param>
    /// <returns>The definition, as for <see cref="For{T}"/>.</returns>
    public static string For(Schema schema, string name)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(name);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("name", ToolName(name));
            w.WriteString("description", Describe(schema, name));
            w.WritePropertyName("input_schema");
            WriteSchema(w, schema);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Builds only the JSON Schema of the arguments for a runtime schema.</summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The schema document.</returns>
    public static string SchemaFor(Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            WriteSchema(w, schema);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string ToolName(string typeName)
    {
        var sb = new StringBuilder("search_");
        foreach (char c in typeName)
        {
            sb.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_');
        }

        return sb.Length > 64 ? sb.ToString(0, 64) : sb.ToString();
    }

    private static string Describe(Schema schema, string name)
    {
        var sb = new StringBuilder("Searches ").Append(name).Append(" documents and returns the best matches. ");
        sb.Append("Give free text in q, exact conditions in filter, or a structured query. Fields: ");
        bool first = true;
        foreach (FieldDefinition f in schema.Fields)
        {
            if (!f.Indexed)
            {
                continue;
            }

            sb.Append(first ? string.Empty : ", ").Append(f.Name).Append(" (").Append(Label(f)).Append(')');
            first = false;
        }

        return sb.Append('.').ToString();
    }

    private static string Label(FieldDefinition f) => f.Type switch
    {
        FieldType.Text => "full text",
        FieldType.Keyword => "keyword",
        FieldType.Integral => "integer",
        FieldType.Fractional => "number",
        FieldType.Date => "date",
        FieldType.Bool => "boolean",
        _ => f.Type.ToString(),
    };

    private static List<string> Names(Schema schema, params FieldType[] types)
    {
        var list = new List<string>();
        foreach (FieldDefinition f in schema.Fields)
        {
            if (f.Indexed && Array.IndexOf(types, f.Type) >= 0)
            {
                list.Add(f.Name);
            }
        }

        return list;
    }

    // Fields inside a JSON property are reached by path in a filter string; the node types with field enums cannot name them.
    private static string JsonHint(Schema schema)
    {
        string? json = schema.Fields.FirstOrDefault(static f => f.Type == FieldType.Json && f.Indexed && f.DynamicParent is null)?.Name;
        return json is null ? string.Empty : " Values inside the JSON property " + json + " are reached by path, e.g. " + json + ".customer.age >= 18 or " + json + ".tags = \"red\".";
    }

    private static void WriteSchema(Utf8JsonWriter w, Schema schema)
    {
        List<string> keyword = Names(schema, FieldType.Keyword);
        List<string> integral = Names(schema, FieldType.Integral);
        List<string> fractional = Names(schema, FieldType.Fractional);
        List<string> numeric = Names(schema, FieldType.Integral, FieldType.Fractional);
        List<string> date = Names(schema, FieldType.Date);
        List<string> boolean = Names(schema, FieldType.Bool);
        List<string> textOrKeyword = Names(schema, FieldType.Text, FieldType.Keyword);
        List<string> text = Names(schema, FieldType.Text);
        List<string> notText = Names(schema, FieldType.Keyword, FieldType.Integral, FieldType.Fractional, FieldType.Date, FieldType.Bool);

        // The node definitions that apply to this schema; each is a $defs entry the "node" alternatives refer to.
        var nodes = new List<string>();
        var defs = new List<(string Name, Action Write)>();

        void Def(string name, Action write)
        {
            defs.Add((name, write));
            nodes.Add(name);
        }

        Def("matchAll", () => Node(w, "matchAll", null));
        Def("text", () => Node(w, "text", () => Str(w, "text", "Free text, searched in every full-text field. Words must all match; \"quoted phrases\"; -excluded words; OR."), required: ["text"]));
        Def("strict", () => Node(w, "strict", () => Str(w, "text", "A query in the strict syntax: field:value, AND, OR, NOT, \"phrase\"~2, prefix*, fuzzy~1, /regex/, field:[a TO b], ^boost."), required: ["text"]));
        Def("filter", () => Node(w, "filter", () => Str(w, "filter", "A filter string such as: Paid = false AND Total >= 100. Operators: = != > >= < <= TO IN EXISTS IS NULL IS EMPTY AND OR NOT, parentheses." + JsonHint(schema)), required: ["filter"]));

        if (textOrKeyword.Count > 0)
        {
            Def("termKeyword", () => Node(w, "term", () => { Field(w, textOrKeyword); Typed(w, "value", "string", "The exact value of a keyword, or one analyzed term of a full-text field."); }, required: ["field", "value"]));
        }

        if (numeric.Count > 0)
        {
            Def("termNumber", () => Node(w, "term", () => { Field(w, numeric); Typed(w, "value", "number", "The exact number."); }, required: ["field", "value"]));
        }

        if (date.Count > 0)
        {
            Def("termDate", () => Node(w, "term", () => { Field(w, date); DateProp(w, "value"); }, required: ["field", "value"]));
        }

        if (boolean.Count > 0)
        {
            Def("termBool", () => Node(w, "term", () => { Field(w, boolean); Typed(w, "value", "boolean", "true or false."); }, required: ["field", "value"]));
        }

        if (textOrKeyword.Count > 0)
        {
            Def("phrase", () => Node(w, "phrase", () =>
            {
                Field(w, textOrKeyword);
                w.WriteStartObject("terms");
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("type", "string");
                w.WriteNumber("maxLength", QueryJson.MaxStringLength);
                w.WriteEndObject();
                w.WriteNumber("minItems", 1);
                w.WriteNumber("maxItems", 256);
                w.WriteString("description", "The terms in order, already in analyzed form (lower case for most fields).");
                w.WriteEndObject();
                Int(w, "slop", 0, StrictQuery.MaxSlop, "How many position moves are allowed. Default 0.");
            }, required: ["field", "terms"]));
            Def("prefix", () => Node(w, "prefix", () => { Field(w, textOrKeyword); Str(w, "prefix", "The start of a term."); }, required: ["field", "prefix"]));
            Def("fuzzy", () => Node(w, "fuzzy", () =>
            {
                Field(w, textOrKeyword);
                Str(w, "term", "The term to match approximately.");
                Int(w, "maxEdits", 0, 2, "The most single-character edits. Default 2.");
                Int(w, "prefixLength", 0, 1024, "How many leading characters must match exactly. Default 0.");
                Typed(w, "transpositions", "boolean", "Whether swapping two neighbours is one edit. Default true.");
            }, required: ["field", "term"]));
            Def("regex", () => Node(w, "regex", () => { Field(w, textOrKeyword); Str(w, "pattern", "A regular expression that must match a whole term."); }, required: ["field", "pattern"]));
        }

        if (text.Count > 0 || keyword.Count > 0)
        {
            Def("multiFieldTerm", () => Node(w, "multiFieldTerm", () =>
            {
                w.WriteStartObject("fields");
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                EnumOf(w, textOrKeyword);
                w.WriteEndObject();
                w.WriteNumber("minItems", 1);
                w.WriteNumber("maxItems", 64);
                w.WriteEndObject();
                Str(w, "term", "The analyzed term, matched in any of the fields.");
            }, required: ["fields", "term"]));
        }

        RangeDef("rangeNumber", numeric, "number");
        RangeDef("rangeDate", date, "date");
        RangeDef("rangeKeyword", keyword, "string");

        if (notText.Count > 0)
        {
            Def("exists", () => Node(w, "exists", () => Field(w, notText), required: ["field"]));
        }

        List<string> geo = [.. schema.Fields.Where(static f => f.Type == FieldType.GeoPoint && f.Indexed && f.Column).Select(static f => f.Name)];
        if (geo.Count > 0)
        {
            Def("geoRadius", () => Node(w, "geoRadius", () =>
            {
                Field(w, geo);
                Num(w, "lat", -90, 90, "The centre latitude in degrees.");
                Num(w, "lng", -180, 180, "The centre longitude in degrees.");
                Num(w, "meters", 0, null, "The radius in metres.");
            }, required: ["field", "lat", "lng", "meters"]));
            Def("geoBoundingBox", () => Node(w, "geoBoundingBox", () =>
            {
                Field(w, geo);
                Num(w, "north", -90, 90, "The northern edge in degrees.");
                Num(w, "south", -90, 90, "The southern edge in degrees, not above north.");
                Num(w, "west", -180, 180, "The western edge in degrees. A west edge greater than the east edge crosses the antimeridian.");
                Num(w, "east", -180, 180, "The eastern edge in degrees.");
            }, required: ["field", "north", "south", "west", "east"]));
        }

        int knnIndex = 0;
        foreach (FieldDefinition vf in schema.Fields.Where(static f => f.Type == FieldType.Vector && f.Indexed))
        {
            FieldDefinition vector = vf;
            Def("knn" + (knnIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture), () => Node(w, "knn", () =>
            {
                Field(w, [vector.Name]);
                w.WriteStartObject("vector");
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("type", "number");
                w.WriteEndObject();
                w.WriteNumber("minItems", vector.Dimensions);
                w.WriteNumber("maxItems", vector.Dimensions);
                w.WriteString("description", "The query embedding: exactly " + vector.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture) + " numbers.");
                w.WriteEndObject();
                Int(w, "k", 1, KnnQuery.MaxK, "How many nearest documents to find. Default 10.");
                Int(w, "ef", 1, KnnQuery.MaxK, "Search breadth, at least k. Larger is more accurate and slower. Default 100.");
                Typed(w, "exact", "boolean", "Compare with every vector instead of the approximate graph search. Default false.");
                w.WriteStartObject("filter");
                w.WriteString("$ref", "#/$defs/node");
                w.WriteString("description", "Only documents matching this query may be neighbours.");
                w.WriteEndObject();
            }, required: ["field", "vector"]));
        }

        int sparseIndex = 0;
        foreach (FieldDefinition sf in schema.Fields.Where(static f => f.Type == FieldType.Sparse && f.Indexed))
        {
            FieldDefinition sparse = sf;
            Def("sparse" + (sparseIndex++).ToString(System.Globalization.CultureInfo.InvariantCulture), () => Node(w, "sparse", () =>
            {
                Field(w, [sparse.Name]);
                w.WriteStartObject("weights");
                w.WriteString("type", "object");
                w.WriteNumber("maxProperties", SparseVector.MaxEntries);
                w.WriteStartObject("additionalProperties");
                w.WriteString("type", "number");
                w.WriteNumber("minimum", 0);
                w.WriteEndObject();
                w.WriteString("description", "Weights by token: the documents sharing tokens score the dot product of the weights.");
                w.WriteEndObject();
            }, required: ["field", "weights"]));
        }

        Def("bool", () => Node(w, "bool", () =>
        {
            foreach (string clause in ClauseNames)
            {
                w.WriteStartObject(clause);
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("$ref", "#/$defs/node");
                w.WriteEndObject();
                w.WriteEndObject();
            }

            Int(w, "minimumShouldMatch", 0, 1024, "How many should clauses must match. Default: one when there is no must or filter clause.");
        }, required: []));

        Def("functionScore", () => Node(w, "functionScore", () =>
        {
            w.WriteStartObject("query");
            w.WriteString("$ref", "#/$defs/node");
            w.WriteEndObject();
            w.WriteStartObject("functions");
            w.WriteString("type", "array");
            w.WriteNumber("minItems", 1);
            w.WriteNumber("maxItems", FunctionScoreQuery.MaxFunctions);
            w.WriteString("description", "Scoring functions of column values; the values combine by scoreMode and then with the score of query by boostMode.");
            w.WriteStartObject("items");
            w.WriteStartArray("oneOf");
            ScoreFunctionDefs(w, schema);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("scoreMode");
            EnumOf(w, [.. FunctionNames.ScoreModes]);
            w.WriteString("description", "How function values combine. Default multiply.");
            w.WriteEndObject();
            w.WriteStartObject("boostMode");
            EnumOf(w, [.. FunctionNames.BoostModes]);
            w.WriteString("description", "How the function score and the query score combine. Default multiply.");
            w.WriteEndObject();
            Num(w, "maxBoost", 0, null, "The ceiling of the combined function score. Default: none.");
            Num(w, "minScore", 0, null, "Documents scoring below this are dropped.");
        }, required: ["functions"]));

        void RangeDef(string name, List<string> fields, string kind)
        {
            if (fields.Count == 0)
            {
                return;
            }

            Def(name, () =>
            {
                w.WriteStartObject();
                w.WriteString("type", "object");
                w.WriteStartObject("properties");
                TypeConst(w, "range");
                Field(w, fields);
                foreach (string bound in BoundNames)
                {
                    if (kind == "date")
                    {
                        DateProp(w, bound);
                    }
                    else
                    {
                        Typed(w, bound, kind, bound.StartsWith('g') ? "Lower bound." : "Upper bound.");
                    }
                }

                Boost(w);
                w.WriteEndObject();
                w.WriteStartArray("required");
                w.WriteStringValue("type");
                w.WriteStringValue("field");
                w.WriteEndArray();
                w.WriteBoolean("additionalProperties", false);

                // At least one bound, and not both forms of the same side.
                w.WriteStartArray("anyOf");
                foreach (string bound in BoundNames)
                {
                    w.WriteStartObject();
                    w.WriteStartArray("required");
                    w.WriteStringValue(bound);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                foreach ((string a, string b) in BoundPairs)
                {
                    w.WriteStartObject("not");
                    w.WriteStartArray("required");
                    w.WriteStringValue(a);
                    w.WriteStringValue(b);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            });
        }

        w.WriteStartObject();
        w.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
        w.WriteString("type", "object");
        w.WriteString("description", "A OneIndex query document, version 1.");
        w.WriteStartObject("properties");
        w.WriteStartObject("v");
        w.WriteNumber("const", QueryJson.Version);
        w.WriteString("description", "The format version. Always 1.");
        w.WriteEndObject();
        Str(w, "q", "Free text, searched in every full-text field. Words must all match; \"quoted phrases\"; -excluded words; OR.");
        w.WriteStartObject("query");
        w.WriteString("$ref", "#/$defs/node");
        w.WriteString("description", "A structured query that scores.");
        w.WriteEndObject();
        w.WriteStartObject("filter");
        w.WriteString("description", "Selects documents without scoring: a filter string, or a structured query.");
        w.WriteStartArray("oneOf");
        w.WriteStartObject();
        w.WriteString("type", "string");
        w.WriteNumber("maxLength", FilterString.MaxLength);
        w.WriteEndObject();
        w.WriteStartObject();
        w.WriteString("$ref", "#/$defs/node");
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        Int(w, "limit", 0, QueryJson.MaxWindow, "The most hits to return. Default 20; limit + offset is at most 10000.");
        Int(w, "offset", 0, QueryJson.MaxWindow, "How many of the best hits to skip. Default 0.");
        Typed(w, "countExactly", "boolean", "Count every match instead of giving a lower bound. Slower. Default false.");
        List<string> vectorFields = [.. schema.Fields.Where(static f => f.Type == FieldType.Vector && f.Indexed).Select(static f => f.Name)];
        bool hasSparse = schema.Fields.Any(static f => f.Type == FieldType.Sparse && f.Indexed);
        if (vectorFields.Count > 0 || hasSparse)
        {
            w.WriteStartObject("hybrid");
            w.WriteString("type", "object");
            w.WriteString("description", "Combine the text search of q with a vector search over the meaning of q, when the index can embed text. Without it, results are text matches only.");
            w.WriteBoolean("additionalProperties", false);
            w.WriteStartObject("properties");
            if (vectorFields.Count > 0)
            {
                w.WriteStartObject("field");
                EnumOf(w, vectorFields);
                w.WriteString("description", "The vector field to search. Default: the only automatically embedded one.");
                w.WriteEndObject();
            }

            if (hasSparse)
            {
                Typed(w, "sparse", "boolean", "Add a learned sparse list (the text of q encoded by the index's sparse encoder). Default false.");
                Num(w, "sparseWeight", 0, 1000, "The sparse list's weight against the others. Default 1 for rrf, 0.5 for linear.");
            }

            Num(w, "semanticRatio", 0, 1, "How much meaning counts against words: 0 words only, 1 meaning only. Default 0.75.");
            w.WriteStartObject("fusion");
            EnumOf(w, ["rrf", "linear"]);
            w.WriteString("description", "How the two result lists combine. Default rrf (reciprocal rank fusion).");
            w.WriteEndObject();
            Int(w, "rankConstant", 1, 1000, "The constant of reciprocal rank fusion. Default 10.");
            Int(w, "window", 1, HybridQuery.MaxWindow, "How many results each list contributes. Default 100.");
            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteStartObject("rerank");
        w.WriteString("type", "object");
        w.WriteString("description", "Rescore the best results with a cross-encoder that reads q and each result's text together, and return them in that order. Needs q and an index with a reranker; slower. Not with sort.");
        w.WriteBoolean("additionalProperties", false);
        w.WriteStartObject("properties");
        Int(w, "window", 1, QueryJson.MaxWindow, "How many of the best results to rescore. Default 100.");
        w.WriteEndObject();
        w.WriteEndObject();

        List<string> sortable = [.. schema.Fields.Where(static f => f.Column && f.Type is FieldType.Keyword or FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool).Select(static f => f.Name)];
        w.WriteStartObject("sort");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "string");
        w.WriteNumber("maxLength", QueryJson.MaxStringLength);
        w.WriteEndObject();
        w.WriteNumber("minItems", 1);
        w.WriteNumber("maxItems", 16);
        w.WriteString("description", "Orders the hits instead of by relevance. Each key is a field name with :asc or :desc (and :first to put documents without a value first), applied in turn; ties go to the next key. Fields: " + string.Join(", ", sortable) + ". _score:desc sorts by relevance." + (geo.Count > 0 ? " _geoPoint(lat, lng):asc sorts by distance from a point." : string.Empty));
        w.WriteEndObject();
        Str(w, "searchAfter", "The cursor of the last hit of the previous page of a sorted search; returns the hits after it. Needs sort; do not combine with offset.");
        w.WriteStartObject("highlight");
        w.WriteString("description", "Snippet settings: true for the defaults, or an object.");
        w.WriteStartArray("oneOf");
        w.WriteStartObject();
        w.WriteBoolean("const", true);
        w.WriteEndObject();
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        Int(w, "fragmentLength", 1, 10_000, "Target length of a snippet in characters. Default 160.");
        Int(w, "maxFragments", 1, 100, "Most snippets per field. Default 3.");
        w.WriteEndObject();
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("v");
        w.WriteEndArray();
        w.WriteBoolean("additionalProperties", false);

        w.WriteStartObject("$defs");
        w.WriteStartObject("node");
        w.WriteStartArray("oneOf");
        foreach (string n in nodes)
        {
            w.WriteStartObject();
            w.WriteString("$ref", "#/$defs/" + n);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
        foreach ((string name, Action write) in defs)
        {
            w.WritePropertyName(name);
            write();
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    // The function objects a functionScore node accepts, one oneOf alternative each; fields are the schema's columns.
    private static void ScoreFunctionDefs(Utf8JsonWriter w, Schema schema)
    {
        List<string> factorFields = [.. schema.Fields.Where(static f => f.Column && f.Type is FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool).Select(static f => f.Name)];
        List<string> decayFields = [.. schema.Fields.Where(static f => f.Column && f.Type is FieldType.Integral or FieldType.Fractional or FieldType.Date).Select(static f => f.Name)];
        List<string> geoFields = [.. schema.Fields.Where(static f => f.Column && f.Type == FieldType.GeoPoint).Select(static f => f.Name)];

        void Common()
        {
            Num(w, "weight", 0, null, "Multiplies the function's value. Default 1.");
            w.WriteStartObject("filter");
            w.WriteString("description", "Applies the function only to documents that match: a filter string or a query node.");
            w.WriteStartArray("oneOf");
            w.WriteStartObject();
            w.WriteString("type", "string");
            w.WriteNumber("maxLength", QueryJson.MaxStringLength);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("$ref", "#/$defs/node");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        void Fn(Action type, Action properties, string[] required)
        {
            w.WriteStartObject();
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            w.WriteStartObject("type");
            type();
            w.WriteEndObject();
            properties();
            Common();
            w.WriteEndObject();
            w.WriteStartArray("required");
            w.WriteStringValue("type");
            foreach (string r in required)
            {
                w.WriteStringValue(r);
            }

            w.WriteEndArray();
            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
        }

        void Const(string v) => w.WriteString("const", v);

        Fn(() => Const("weight"), static () => { }, []);
        if (factorFields.Count > 0)
        {
            Fn(() => Const("fieldValueFactor"), () =>
            {
                Field(w, factorFields);
                Num(w, "factor", -double.MaxValue, null, "Multiplies the value before the modifier. Default 1.");
                w.WriteStartObject("modifier");
                EnumOf(w, [.. FunctionNames.Modifiers]);
                w.WriteString("description", "Applied to factor times the value. Default none.");
                w.WriteEndObject();
                Num(w, "missing", -double.MaxValue, null, "The value for a document that has none. Default: the function does not apply to it.");
            }, ["field"]);
        }

        if (decayFields.Count > 0)
        {
            Fn(() => { w.WriteStartArray("enum"); foreach (string n in FunctionNames.Shapes) { w.WriteStringValue(n); } w.WriteEndArray(); }, () =>
            {
                Field(w, decayFields);
                w.WriteStartObject("origin");
                w.WriteString("description", "The centre: a number, or for a date field a date such as 2024-01-31 or a number of milliseconds since 1970.");
                w.WriteStartArray("oneOf");
                w.WriteStartObject();
                w.WriteString("type", "number");
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteString("type", "string");
                w.WriteString("pattern", DatePattern);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
                Num(w, "scale", double.Epsilon, null, "The distance beyond the offset at which the value is decay (milliseconds for dates).");
                Num(w, "offset", 0, null, "The distance within which nothing decays. Default 0.");
                OpenUnit(w, "decay", "The value at the scale distance, above 0 and below 1. Default 0.5.");
            }, ["field", "origin", "scale"]);
        }

        if (geoFields.Count > 0)
        {
            Fn(() => { w.WriteStartArray("enum"); foreach (string n in FunctionNames.Shapes) { w.WriteStringValue(n); } w.WriteEndArray(); }, () =>
            {
                Field(w, geoFields);
                w.WriteStartObject("origin");
                w.WriteString("type", "object");
                w.WriteStartObject("properties");
                Num(w, "lat", -90, 90, "The latitude in degrees.");
                Num(w, "lng", -180, 180, "The longitude in degrees.");
                w.WriteEndObject();
                w.WriteStartArray("required");
                w.WriteStringValue("lat");
                w.WriteStringValue("lng");
                w.WriteEndArray();
                w.WriteBoolean("additionalProperties", false);
                w.WriteEndObject();
                Num(w, "scale", double.Epsilon, null, "The distance in metres beyond the offset at which the value is decay.");
                Num(w, "offset", 0, null, "The distance in metres within which nothing decays. Default 0.");
                OpenUnit(w, "decay", "The value at the scale distance, above 0 and below 1. Default 0.5.");
            }, ["field", "origin", "scale"]);
        }
    }

    // One node definition: an object with a fixed "type", the given properties, a boost, and nothing else.
    private static void Node(Utf8JsonWriter w, string type, Action? properties, string[]? required = null)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        TypeConst(w, type);
        properties?.Invoke();
        Boost(w);
        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("type");
        foreach (string r in required ?? [])
        {
            w.WriteStringValue(r);
        }

        w.WriteEndArray();
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();
    }

    private static void TypeConst(Utf8JsonWriter w, string type)
    {
        w.WriteStartObject("type");
        w.WriteString("const", type);
        w.WriteEndObject();
    }

    private static void Boost(Utf8JsonWriter w)
    {
        w.WriteStartObject("boost");
        w.WriteString("type", "number");
        w.WriteNumber("minimum", 0);
        w.WriteString("description", "Multiplies the score. Default 1.");
        w.WriteEndObject();
    }

    private static void EnumOf(Utf8JsonWriter w, List<string> names)
    {
        w.WriteString("type", "string");
        w.WriteStartArray("enum");
        foreach (string n in names)
        {
            w.WriteStringValue(n);
        }

        w.WriteEndArray();
    }

    private static void Field(Utf8JsonWriter w, List<string> names)
    {
        w.WriteStartObject("field");
        EnumOf(w, names);
        w.WriteEndObject();
    }

    private static void Str(Utf8JsonWriter w, string name, string description)
    {
        w.WriteStartObject(name);
        w.WriteString("type", "string");
        w.WriteNumber("maxLength", QueryJson.MaxStringLength);
        w.WriteString("description", description);
        w.WriteEndObject();
    }

    private static void Typed(Utf8JsonWriter w, string name, string jsonType, string description)
    {
        w.WriteStartObject(name);
        w.WriteString("type", jsonType);
        if (jsonType == "string")
        {
            w.WriteNumber("maxLength", QueryJson.MaxStringLength);
        }

        w.WriteString("description", description);
        w.WriteEndObject();
    }

    private static void DateProp(Utf8JsonWriter w, string name)
    {
        w.WriteStartObject(name);
        w.WriteString("type", "string");
        w.WriteString("pattern", DatePattern);
        w.WriteString("description", "A UTC date or time such as 2024-01-31 or 2024-01-31T10:00:00Z.");
        w.WriteEndObject();
    }

    private static void Num(Utf8JsonWriter w, string name, double min, double? max, string description)
    {
        w.WriteStartObject(name);
        w.WriteString("type", "number");
        w.WriteNumber("minimum", min);
        if (max is { } m)
        {
            w.WriteNumber("maximum", m);
        }

        w.WriteString("description", description);
        w.WriteEndObject();
    }

    private static void OpenUnit(Utf8JsonWriter w, string name, string description)
    {
        w.WriteStartObject(name);
        w.WriteString("type", "number");
        w.WriteNumber("exclusiveMinimum", 0);
        w.WriteNumber("exclusiveMaximum", 1);
        w.WriteString("description", description);
        w.WriteEndObject();
    }

    private static void Int(Utf8JsonWriter w, string name, int min, int max, string description)
    {
        w.WriteStartObject(name);
        w.WriteString("type", "integer");
        w.WriteNumber("minimum", min);
        w.WriteNumber("maximum", max);
        w.WriteString("description", description);
        w.WriteEndObject();
    }
}
