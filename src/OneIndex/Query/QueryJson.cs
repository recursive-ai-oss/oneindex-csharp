using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OneIndex.Highlighting;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Queries;

/// <summary>
/// A search described as data: what to score, what to filter, and how to page. It is what a query JSON document means and what
/// <see cref="QueryJson"/> reads and writes. It holds query objects, so everything a JSON document can say is already analyzed and checked
/// against a schema.
/// </summary>
public sealed record QueryRequest
{
    /// <summary>What to score, or <see langword="null"/> for every document.</summary>
    public Query? Query { get; init; }

    /// <summary>What to select without scoring, or <see langword="null"/> for no restriction.</summary>
    public Query? Filter { get; init; }

    /// <summary>The most hits to return. Default 20; 0 to 10,000 together with <see cref="Offset"/>.</summary>
    public int Limit { get; init; } = 20;

    /// <summary>How many of the best hits to skip. Default 0.</summary>
    public int Offset { get; init; }

    /// <summary>Whether the total number of hits must be exact (it visits every match). Default <see langword="false"/>: a lower bound.</summary>
    public bool CountExactly { get; init; }

    /// <summary>Snippet settings, or <see langword="null"/> for the defaults.</summary>
    public HighlightOptions? Highlight { get; init; }

    /// <summary>The ordering of the hits, or <see langword="null"/> for relevance order.</summary>
    public QuerySort? Sort { get; init; }

    /// <summary>A <see cref="SortCursor.ToToken"/> token to continue after (search-after); needs <see cref="Sort"/>. <see langword="null"/> for the first page.</summary>
    public string? SearchAfter { get; init; }

    /// <summary>
    /// Fuses a text search of the request's <c>q</c> with a vector search over its embedding, or <see langword="null"/> for plain search. With it,
    /// <c>q</c> is held here and not in <see cref="Query"/>. It needs an index with an embedder: <c>SearchIndex.Parse</c> applies it
    /// (<see cref="ToQuery"/> does not, because embedding is not the data form's job, and leaves the text out).
    /// </summary>
    public HybridRequest? Hybrid { get; init; }

    /// <summary>The query the engine runs: <see cref="Query"/> (every document when absent) with <see cref="Filter"/> as a non-scoring restriction.</summary>
    /// <returns>The combined query.</returns>
    public Query ToQuery()
    {
        Query scored = Query ?? new MatchAllQuery();
        return Filter is null ? scored : new BooleanQuery([new BooleanClause(scored, Occur.Must), new BooleanClause(Filter, Occur.Filter)]);
    }
}

/// <summary>The <c>hybrid</c> setting of a query JSON request (docs/hybrid.md).</summary>
/// <param name="Text">The request's <c>q</c>: the text searched by words and, embedded, by meaning.</param>
/// <param name="SemanticRatio">How much the vector arm counts, 0 to 1. Default 0.75.</param>
/// <param name="Fusion">How the lists combine. Default reciprocal rank fusion.</param>
/// <param name="RankConstant">The reciprocal rank constant, 1 to 1,000. Default 10.</param>
/// <param name="Window">How many documents each arm contributes. Default 100.</param>
/// <param name="Field">The vector property to search, or <see langword="null"/> for the schema's only automatically embedded one.</param>
/// <param name="Sparse">Whether to add a learned sparse list (the text of q encoded by the index's sparse encoder). Default <see langword="false"/>.</param>
/// <param name="SparseWeight">The sparse list's weight, or <see langword="null"/> for the default of the fusion.</param>
public sealed record HybridRequest(string Text, double SemanticRatio = HybridQuery.DefaultSemanticRatio, FusionMethod Fusion = FusionMethod.ReciprocalRank, int RankConstant = HybridQuery.DefaultRankConstant, int Window = 100, string? Field = null, bool Sparse = false, double? SparseWeight = null);

/// <summary>The sort keys of a query JSON document. Equal when the keys are equal in order.</summary>
/// <param name="Keys">The keys, 1 to 16.</param>
public sealed record QuerySort(IReadOnlyList<SortCriterion> Keys)
{
    /// <summary>Compares the keys one by one.</summary>
    /// <param name="other">The other sort.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(QuerySort? other) => other is not null && Keys.SequenceEqual(other.Keys);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        foreach (SortCriterion k in Keys)
        {
            hash.Add(k);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Versioned query JSON: a document with <c>"v": 1</c> that describes a search, checked against a schema as it is read. Unknown
/// properties, wrong types and unknown fields are errors that name the JSON path. Reading uses <see cref="JsonDocument"/> and writing
/// uses <see cref="Utf8JsonWriter"/>, with no reflection. The format is in <c>docs/query-json.md</c>.
/// </summary>
public static partial class QueryJson
{
    /// <summary>The format version this build reads and writes.</summary>
    public const int Version = 1;

    /// <summary>The largest document read, in bytes.</summary>
    public const int MaxBytes = 1 << 20;

    /// <summary>The deepest nesting of query nodes.</summary>
    public const int MaxNodeDepth = 32;

    /// <summary>The most query nodes in one document.</summary>
    public const int MaxNodes = 2048;

    /// <summary>The longest string value, in UTF-16 code units.</summary>
    public const int MaxStringLength = 8192;

    /// <summary>The largest <c>limit + offset</c>.</summary>
    public const int MaxWindow = 10_000;

    /// <summary>Reads a query JSON document.</summary>
    /// <param name="json">The document.</param>
    /// <param name="schema">The schema its fields must belong to.</param>
    /// <returns>The request.</returns>
    /// <exception cref="QueryParseException">The document is not valid JSON, not a valid query document, or names a field the schema lacks; the exception carries the JSON path.</exception>
    public static QueryRequest Read(string json, Schema schema)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxBytes / 2)
        {
            throw QueryParseException.AtPath("$", $"The document is larger than the limit of {MaxBytes} bytes.");
        }

        byte[] utf8;
        try
        {
            utf8 = new UTF8Encoding(false, true).GetBytes(json);
        }
        catch (EncoderFallbackException)
        {
            throw QueryParseException.AtPath("$", "The text holds an unpaired surrogate, which JSON cannot carry.");
        }

        return Read(utf8, schema);
    }

    /// <summary>Reads a query JSON document from UTF-8 bytes.</summary>
    /// <param name="utf8Json">The document.</param>
    /// <param name="schema">The schema its fields must belong to.</param>
    /// <returns>The request.</returns>
    /// <exception cref="QueryParseException">As for the string overload.</exception>
    public static QueryRequest Read(ReadOnlyMemory<byte> utf8Json, Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (utf8Json.Length > MaxBytes)
        {
            throw QueryParseException.AtPath("$", $"The document is larger than the limit of {MaxBytes} bytes.");
        }

        // JsonDocument decodes strings lazily and throws InvalidOperationException for bad UTF-8, so check it once, here.
        if (!System.Text.Unicode.Utf8.IsValid(utf8Json.Span))
        {
            ReadOnlySpan<byte> bytes = utf8Json.Span;
            int at = 0;
            int line = 1;
            int lineStart = 0;
            while (at < bytes.Length)
            {
                if (Rune.DecodeFromUtf8(bytes[at..], out _, out int used) != System.Buffers.OperationStatus.Done)
                {
                    break;
                }

                for (int i = at; i < at + used; i++)
                {
                    if (bytes[i] == (byte)'\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }
                }

                at += used;
            }

            throw new QueryParseException("The document is not valid UTF-8.", at, 1, line, at - lineStart + 1);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 4 * MaxNodeDepth });
        }
        catch (JsonException e)
        {
            int line = (int)Math.Min(int.MaxValue - 1, (e.LineNumber ?? 0) + 1);
            int column = (int)Math.Min(int.MaxValue - 1, (e.BytePositionInLine ?? 0) + 1);
            throw new QueryParseException("The document is not valid JSON: " + e.Message.Split('.')[0] + ".", 0, 0, line, column);
        }

        using (doc)
        {
            QueryRequest request = new QueryJsonReader(schema).ReadRequest(doc.RootElement);
            CheckWrittenDepth(request, schema);
            return request;
        }
    }

    // Text, strict and filter nodes are compiled when read and written back as their expanded trees. The expansion
    // must stay inside the same depth bound, so that every accepted document writes a document that reads back.
    private static void CheckWrittenDepth(QueryRequest request, Schema schema)
    {
        byte[] written = Encoding.UTF8.GetBytes(Write(request));
        try
        {
            using JsonDocument again = JsonDocument.Parse(written, new JsonDocumentOptions { MaxDepth = 4 * MaxNodeDepth });
            _ = new QueryJsonReader(schema).ReadRequest(again.RootElement);
        }
        catch (Exception e) when (e is JsonException or QueryParseException)
        {
            throw QueryParseException.AtPath("$", $"The queries expand to a tree nested deeper than the limit of {MaxNodeDepth.ToString(CultureInfo.InvariantCulture)} levels.", null, null);
        }
    }

    /// <summary>Reads a query JSON document without throwing for invalid input.</summary>
    /// <param name="json">The document.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="request">The request when valid.</param>
    /// <param name="error">The error when not.</param>
    /// <returns><see langword="true"/> when the document is valid.</returns>
    public static bool TryRead(string json, Schema schema, [NotNullWhen(true)] out QueryRequest? request, [NotNullWhen(false)] out QueryParseException? error)
    {
        try
        {
            request = Read(json, schema);
            error = null;
            return true;
        }
        catch (QueryParseException e)
        {
            request = null;
            error = e;
            return false;
        }
    }

    /// <summary>Writes a request as canonical query JSON: fixed property order, no insignificant white space, defaults left out.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The document. Reading it back with the same schema gives an equal request.</returns>
    /// <exception cref="ArgumentException">The request holds a query kind or value that the format cannot carry.</exception>
    public static string Write(QueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            if (request.Query is not null)
            {
                w.WritePropertyName("query");
                WriteNode(w, request.Query);
            }

            if (request.Filter is not null)
            {
                w.WritePropertyName("filter");
                WriteNode(w, request.Filter);
            }

            if (request.Hybrid is { } hy)
            {
                w.WriteString("q", hy.Text);
                w.WriteStartObject("hybrid");
                if (hy.Field is not null)
                {
                    w.WriteString("field", hy.Field);
                }

                if (hy.Sparse)
                {
                    w.WriteBoolean("sparse", true);
                }

                if (hy.SparseWeight is { } hsw)
                {
                    w.WriteNumber("sparseWeight", hsw);
                }

                WriteHybridSettings(w, hy.Fusion, hy.RankConstant, hy.SemanticRatio, hy.Window);
                w.WriteEndObject();
            }

            if (request.Limit != 20)
            {
                w.WriteNumber("limit", request.Limit);
            }

            if (request.Offset != 0)
            {
                w.WriteNumber("offset", request.Offset);
            }

            if (request.CountExactly)
            {
                w.WriteBoolean("countExactly", true);
            }

            if (request.Highlight is { } h)
            {
                w.WriteStartObject("highlight");
                w.WriteNumber("fragmentLength", h.FragmentLength);
                w.WriteNumber("maxFragments", h.MaxFragments);
                w.WriteEndObject();
            }

            if (request.Sort is { } sort)
            {
                w.WriteStartArray("sort");
                foreach (SortCriterion key in sort.Keys)
                {
                    w.WriteStringValue(SortText.Write(key));
                }

                w.WriteEndArray();
            }

            if (request.SearchAfter is { } after)
            {
                w.WriteString("searchAfter", after);
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteHybridSettings(Utf8JsonWriter w, FusionMethod fusion, int rankConstant, double ratio, int window)
    {
        if (fusion != FusionMethod.ReciprocalRank)
        {
            w.WriteString("fusion", "linear");
        }

        if (rankConstant != HybridQuery.DefaultRankConstant)
        {
            w.WriteNumber("rankConstant", rankConstant);
        }

        if (ratio != HybridQuery.DefaultSemanticRatio)
        {
            w.WriteNumber("semanticRatio", ratio);
        }

        if (window != 100)
        {
            w.WriteNumber("window", window);
        }
    }

    /// <summary>Writes one query as a canonical query node object.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The JSON object text.</returns>
    /// <exception cref="ArgumentException">The query kind or a value cannot be carried.</exception>
    public static string WriteQuery(Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            WriteNode(w, query);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNode(Utf8JsonWriter w, Query q)
    {
        w.WriteStartObject();
        switch (q)
        {
            case MatchAllQuery:
                w.WriteString("type", "matchAll");
                break;
            case TermQuery t:
                w.WriteString("type", "term");
                w.WriteString("field", t.Field);
                w.WriteString("value", t.Term);
                break;
            case PhraseQuery p:
                w.WriteString("type", "phrase");
                w.WriteString("field", p.Field);
                w.WriteStartArray("terms");
                foreach (string term in p.Terms)
                {
                    w.WriteStringValue(term);
                }

                w.WriteEndArray();
                if (p.Slop != 0)
                {
                    w.WriteNumber("slop", p.Slop);
                }

                break;
            case PrefixQuery p:
                w.WriteString("type", "prefix");
                w.WriteString("field", p.Field);
                w.WriteString("prefix", p.Prefix);
                break;
            case FuzzyQuery f:
                w.WriteString("type", "fuzzy");
                w.WriteString("field", f.Field);
                w.WriteString("term", f.Term);
                if (f.MaxEdits != 2)
                {
                    w.WriteNumber("maxEdits", f.MaxEdits);
                }

                if (f.PrefixLength != 0)
                {
                    w.WriteNumber("prefixLength", f.PrefixLength);
                }

                if (!f.Transpositions)
                {
                    w.WriteBoolean("transpositions", false);
                }

                break;
            case RegexQuery r:
                w.WriteString("type", "regex");
                w.WriteString("field", r.Field);
                w.WriteString("pattern", r.Pattern);
                break;
            case RangeQuery r:
                if (r.Lower is null && r.Upper is null)
                {
                    w.WriteString("type", "exists");
                    w.WriteString("field", r.Field);
                    break;
                }

                // One inclusive value that is not text is an exact match: a term, which also covers booleans (a range does not).
                if (r.Lower is { } same && r.Upper is { } other && r.IncludeLower && r.IncludeUpper && same.Equals(other) && same.Kind != FieldValueKind.Text)
                {
                    w.WriteString("type", "term");
                    w.WriteString("field", r.Field);
                    w.WritePropertyName("value");
                    WriteValue(w, same);
                    break;
                }

                w.WriteString("type", "range");
                w.WriteString("field", r.Field);
                if (r.Lower is { } lo)
                {
                    w.WritePropertyName(r.IncludeLower ? "gte" : "gt");
                    WriteValue(w, lo);
                }

                if (r.Upper is { } hi)
                {
                    w.WritePropertyName(r.IncludeUpper ? "lte" : "lt");
                    WriteValue(w, hi);
                }

                break;
            case MultiFieldTermQuery m:
                w.WriteString("type", "multiFieldTerm");
                w.WriteStartArray("fields");
                foreach (string f in m.Fields)
                {
                    w.WriteStringValue(f);
                }

                w.WriteEndArray();
                w.WriteString("term", m.Term);
                break;
            case GeoRadiusQuery g:
                w.WriteString("type", "geoRadius");
                w.WriteString("field", g.Field);
                w.WriteNumber("lat", g.Latitude);
                w.WriteNumber("lng", g.Longitude);
                w.WriteNumber("meters", g.RadiusMeters);
                break;
            case GeoBoundingBoxQuery x:
                w.WriteString("type", "geoBoundingBox");
                w.WriteString("field", x.Field);
                w.WriteNumber("north", x.North);
                w.WriteNumber("south", x.South);
                w.WriteNumber("west", x.West);
                w.WriteNumber("east", x.East);
                break;
            case BooleanQuery b:
                WriteBoolean(w, b);
                break;
            case FunctionScoreQuery fs:
                WriteFunctionScore(w, fs);
                break;
            case HybridQuery hq:
                w.WriteString("type", "hybrid");
                w.WritePropertyName("text");
                WriteNode(w, hq.Text);
                if (hq.Vector is not null)
                {
                    w.WritePropertyName("vector");
                    WriteNode(w, hq.Vector);
                }

                if (hq.Sparse is not null)
                {
                    w.WritePropertyName("sparse");
                    WriteNode(w, hq.Sparse);
                }

                if (hq.SparseWeight is { } sparseWeight)
                {
                    w.WriteNumber("sparseWeight", sparseWeight);
                }

                WriteHybridSettings(w, hq.Fusion, hq.RankConstant, hq.SemanticRatio, hq.Window);
                break;
            case SparseQuery sparse:
                w.WriteString("type", "sparse");
                w.WriteString("field", sparse.Field);
                w.WriteStartObject("weights");
                for (int i = 0; i < sparse.Vector.Count; i++)
                {
                    w.WriteNumber(sparse.Vector.Tokens[i], sparse.Vector.Weights[i]);
                }

                w.WriteEndObject();
                break;
            case KnnQuery knn:
                w.WriteString("type", "knn");
                w.WriteString("field", knn.Field);
                w.WriteStartArray("vector");
                foreach (float x in knn.Vector)
                {
                    w.WriteNumberValue(x);
                }

                w.WriteEndArray();
                w.WriteNumber("k", knn.K);
                if (knn.Ef != 0)
                {
                    w.WriteNumber("ef", knn.Ef);
                }

                if (knn.Exact)
                {
                    w.WriteBoolean("exact", true);
                }

                if (knn.Filter is not null)
                {
                    w.WritePropertyName("filter");
                    WriteNode(w, knn.Filter);
                }

                break;
            default:
                throw new ArgumentException($"A {q.GetType().Name} cannot be written as query JSON.", nameof(q));
        }

        if (q.Boost != 1f)
        {
            w.WriteNumber("boost", q.Boost);
        }

        w.WriteEndObject();
    }

    private static void WriteBoolean(Utf8JsonWriter w, BooleanQuery b)
    {
        w.WriteString("type", "bool");

        // Clauses are written grouped by how they take part, in this fixed order; the order inside a group is kept.
        WriteClauses(w, b, "must", Occur.Must);
        WriteClauses(w, b, "filter", Occur.Filter);
        WriteClauses(w, b, "should", Occur.Should);
        WriteClauses(w, b, "mustNot", Occur.MustNot);
        if (b.MinimumShouldMatch != 0)
        {
            w.WriteNumber("minimumShouldMatch", b.MinimumShouldMatch);
        }
    }

    private static void WriteClauses(Utf8JsonWriter w, BooleanQuery b, string name, Occur occur)
    {
        bool open = false;
        foreach (BooleanClause c in b.Clauses)
        {
            if (c.Occur != occur)
            {
                continue;
            }

            if (!open)
            {
                w.WriteStartArray(name);
                open = true;
            }

            WriteNode(w, c.Query);
        }

        if (open)
        {
            w.WriteEndArray();
        }
    }

    private static void WriteValue(Utf8JsonWriter w, FieldValue v)
    {
        switch (v.Kind)
        {
            case FieldValueKind.Text:
                w.WriteStringValue(v.AsText());
                break;
            case FieldValueKind.Integral:
                w.WriteNumberValue(v.AsInt64());
                break;
            case FieldValueKind.Fractional:
                w.WriteNumberValue(v.AsFloat64());
                break;
            case FieldValueKind.Bool:
                w.WriteBooleanValue(v.AsBool());
                break;
            case FieldValueKind.Date:
                w.WriteStringValue(ValueBinder.FormatDate(v));
                break;
            default:
                throw new ArgumentException($"A {v.Kind} value cannot be written as query JSON.");
        }
    }
}

/// <summary>Reads query JSON into query objects. One instance reads one document.</summary>
internal sealed partial class QueryJsonReader
{
    private static readonly string[] RequestProperties = ["v", "q", "query", "filter", "limit", "offset", "countExactly", "highlight", "sort", "searchAfter", "hybrid"];

    // Names of features the format keeps room for. They are rejected with a message of their own, not as typos.
    private static readonly string[] Reserved = ["facets", "aggregations", "distinct", "vector"];

    private static readonly string[] NodeTypes =
        ["matchAll", "text", "strict", "filter", "term", "phrase", "prefix", "fuzzy", "regex", "range", "exists", "multiFieldTerm", "geoRadius", "geoBoundingBox", "bool", "functionScore", "knn", "sparse", "hybrid"];

    private readonly Schema _schema;
    private readonly UserTextCompiler _userText;
    private int _nodes;

    internal QueryJsonReader(Schema schema)
    {
        _schema = schema;
        _userText = new UserTextCompiler(schema);
    }

    internal QueryRequest ReadRequest(JsonElement root)
    {
        const string path = "$";
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Err(path, "A query document is a JSON object, such as {\"v\":1,\"q\":\"invoice\"}.");
        }

        CheckProperties(root, path, RequestProperties, Reserved);
        if (!root.TryGetProperty("v", out JsonElement v))
        {
            throw Err(path, "The document has no \"v\" (format version). Add \"v\": " + QueryJson.Version.ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int version))
        {
            throw Err("$.v", "\"v\" is the format version, a whole number.");
        }

        if (version != QueryJson.Version)
        {
            throw Err("$.v", $"Version {version.ToString(CultureInfo.InvariantCulture)} is not supported; this build reads version {QueryJson.Version.ToString(CultureInfo.InvariantCulture)}.");
        }

        var scored = new List<Query>();
        string? hybridQ = null;
        if (root.TryGetProperty("q", out JsonElement q))
        {
            string qText = ReadString(q, "$.q");
            if (root.TryGetProperty("hybrid", out _))
            {
                hybridQ = qText; // kept as text: it is searched by words and, embedded, by meaning
            }
            else
            {
                scored.Add(_userText.Compile(qText));
            }
        }

        if (root.TryGetProperty("query", out JsonElement query))
        {
            scored.Add(ReadNode(query, "$.query", 1));
        }

        Query? filter = null;
        if (root.TryGetProperty("filter", out JsonElement f))
        {
            filter = f.ValueKind == JsonValueKind.String ? ReadFilterString(f, "$.filter") : ReadNode(f, "$.filter", 1);
        }

        int limit = root.TryGetProperty("limit", out JsonElement l) ? ReadInt(l, "$.limit", 0, QueryJson.MaxWindow) : 20;
        int offset = root.TryGetProperty("offset", out JsonElement o) ? ReadInt(o, "$.offset", 0, QueryJson.MaxWindow) : 0;
        if ((long)limit + offset > QueryJson.MaxWindow)
        {
            throw Err("$.limit", $"limit + offset is {(limit + (long)offset).ToString(CultureInfo.InvariantCulture)}; the largest window is {QueryJson.MaxWindow.ToString(CultureInfo.InvariantCulture)}.");
        }

        bool exact = root.TryGetProperty("countExactly", out JsonElement c) && ReadBool(c, "$.countExactly");
        HighlightOptions? highlight = root.TryGetProperty("highlight", out JsonElement h) ? ReadHighlight(h) : null;

        HybridRequest? hybrid = null;
        if (root.TryGetProperty("hybrid", out JsonElement hybridElement))
        {
            if (hybridQ is null)
            {
                throw Err("$.hybrid", "hybrid searches by meaning the text of \"q\"; add a \"q\".");
            }

            hybrid = ReadHybridRequest(hybridElement, hybridQ);
        }

        QuerySort? sort = root.TryGetProperty("sort", out JsonElement sortElement) ? ReadSort(sortElement) : null;
        string? after = null;
        if (root.TryGetProperty("searchAfter", out JsonElement afterElement))
        {
            after = ReadString(afterElement, "$.searchAfter");
            if (sort is null)
            {
                throw Err("$.searchAfter", "searchAfter continues a sorted search; add a \"sort\".");
            }

            if (!SortCursor.TryParse(after, out SortCursor? cursor) || cursor!.Fingerprint != SortSpec.Fingerprint(sort.Keys) || cursor.Values.Count != sort.Keys.Count)
            {
                throw Err("$.searchAfter", "This is not a cursor made by the same sort. Use the cursor token of the last hit of the previous page.");
            }

            if (offset != 0)
            {
                throw Err("$.offset", "offset cannot be combined with searchAfter.");
            }
        }

        Query? main = scored.Count switch
        {
            0 => null,
            1 => scored[0],
            _ => new BooleanQuery(scored.ConvertAll(static x => new BooleanClause(x, Occur.Must))),
        };
        return new QueryRequest { Query = main, Filter = filter, Limit = limit, Offset = offset, CountExactly = exact, Highlight = highlight, Sort = sort, SearchAfter = after, Hybrid = hybrid };
    }

    private static QueryParseException Err(string path, string detail, string? suggestion = null)
        => QueryParseException.AtPath(path, detail, null, suggestion);

    private static string Child(string path, string name) => path + "." + name;

    private static string Index(string path, int i) => path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";

    // Checks that an object has no unknown or repeated properties.
    private static void CheckProperties(JsonElement obj, string path, string[] allowed, string[]? reserved = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            string name = p.Name;
            if (!seen.Add(name))
            {
                throw Err(Child(path, name), $"The property \"{name}\" appears twice.");
            }

            if (Array.IndexOf(allowed, name) >= 0)
            {
                continue;
            }

            if (reserved is not null && Array.IndexOf(reserved, name) >= 0)
            {
                throw Err(Child(path, name), $"\"{name}\" is reserved for a later version of the query format and is not available in version {QueryJson.Version.ToString(CultureInfo.InvariantCulture)}.");
            }

            string? near = QuerySyntax.Nearest(name, allowed);
            string hint = near is null ? string.Empty : $" Did you mean \"{near}\"?";
            throw Err(Child(path, name), $"Unknown property \"{name}\".{hint} Allowed here: {string.Join(", ", allowed)}.", near);
        }
    }

    private static string ReadString(JsonElement e, string path)
    {
        if (e.ValueKind != JsonValueKind.String)
        {
            throw Err(path, "Expected a string.");
        }

        string s = e.GetString()!;
        if (s.Length > QueryJson.MaxStringLength)
        {
            throw Err(path, $"The string is longer than the limit of {QueryJson.MaxStringLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        return s;
    }

    private static int ReadInt(JsonElement e, string path, int min, int max)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v))
        {
            throw Err(path, "Expected a whole number.");
        }

        if (v < min || v > max)
        {
            throw Err(path, $"Expected a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}, got {v.ToString(CultureInfo.InvariantCulture)}.");
        }

        return v;
    }

    private static bool ReadBool(JsonElement e, string path)
        => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : throw Err(path, "Expected true or false.");

    private static float ReadBoost(JsonElement obj, string path)
    {
        if (!obj.TryGetProperty("boost", out JsonElement e))
        {
            return 1f;
        }

        string p = Child(path, "boost");
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double d))
        {
            throw Err(p, "Expected a number.");
        }

        float boost = (float)d;
        if (!float.IsFinite(boost) || boost < 0f)
        {
            throw Err(p, "A boost is a finite number that is not negative.");
        }

        return boost;
    }

    private static HighlightOptions ReadHighlight(JsonElement e)
    {
        const string path = "$.highlight";
        if (e.ValueKind is JsonValueKind.True)
        {
            return new HighlightOptions();
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            throw Err(path, "Expected true or an object such as {\"fragmentLength\":160,\"maxFragments\":3}.");
        }

        CheckProperties(e, path, ["fragmentLength", "maxFragments"]);
        int length = e.TryGetProperty("fragmentLength", out JsonElement l) ? ReadInt(l, Child(path, "fragmentLength"), 1, 10_000) : new HighlightOptions().FragmentLength;
        int count = e.TryGetProperty("maxFragments", out JsonElement m) ? ReadInt(m, Child(path, "maxFragments"), 1, 100) : new HighlightOptions().MaxFragments;
        return new HighlightOptions { FragmentLength = length, MaxFragments = count };
    }

    private Query ReadFilterString(JsonElement e, string path)
    {
        string text = ReadString(e, path);
        try
        {
            return FilterString.Parse(text, _schema);
        }
        catch (QueryParseException inner)
        {
            throw Nested(inner, path, "filter string");
        }
    }

    private static QueryParseException Nested(QueryParseException inner, string path, string what)
        => new($"{inner.Detail} [{what}, offset {inner.Offset.ToString(CultureInfo.InvariantCulture)}, line {inner.Line.ToString(CultureInfo.InvariantCulture)}, column {inner.Column.ToString(CultureInfo.InvariantCulture)}]", inner.Offset, inner.Length, inner.Line, inner.Column, inner.Expected, path, inner.Suggestion);

    private Query ReadNode(JsonElement e, string path, int depth)
    {
        if (depth > QueryJson.MaxNodeDepth)
        {
            throw Err(path, $"Queries are nested deeper than the limit of {QueryJson.MaxNodeDepth.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (++_nodes > QueryJson.MaxNodes)
        {
            throw Err(path, $"The document has more than {QueryJson.MaxNodes.ToString(CultureInfo.InvariantCulture)} query nodes.");
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            throw Err(path, "Expected a query object, such as {\"type\":\"term\",\"field\":\"Tags\",\"value\":\"invoice\"}.");
        }

        if (!e.TryGetProperty("type", out JsonElement typeElement))
        {
            throw Err(path, "A query object needs a \"type\". Types: " + string.Join(", ", NodeTypes) + ".");
        }

        string type = ReadString(typeElement, Child(path, "type"));
        switch (type)
        {
            case "matchAll":
                CheckProperties(e, path, ["type", "boost"]);
                return Boosted(new MatchAllQuery(), e, path);
            case "text":
                {
                    CheckProperties(e, path, ["type", "text", "boost"]);
                    string text = ReadString(Require(e, path, "text"), Child(path, "text"));
                    return Boosted(_userText.Compile(text), e, path);
                }

            case "strict":
                {
                    CheckProperties(e, path, ["type", "text", "boost"]);
                    string text = ReadString(Require(e, path, "text"), Child(path, "text"));
                    try
                    {
                        return Boosted(StrictQuery.Parse(text, _schema), e, path);
                    }
                    catch (QueryParseException inner)
                    {
                        throw Nested(inner, Child(path, "text"), "strict query");
                    }
                }

            case "filter":
                CheckProperties(e, path, ["type", "filter", "boost"]);
                return Boosted(ReadFilterString(Require(e, path, "filter"), Child(path, "filter")), e, path);
            case "term":
                return ReadTerm(e, path);
            case "phrase":
                return ReadPhrase(e, path);
            case "prefix":
                {
                    CheckProperties(e, path, ["type", "field", "prefix", "boost"]);
                    FieldDefinition def = ReadField(e, path, FieldKinds.TextOrKeyword);
                    return Boosted(new PrefixQuery(def.Name, ReadString(Require(e, path, "prefix"), Child(path, "prefix"))), e, path);
                }

            case "fuzzy":
                return ReadFuzzy(e, path);
            case "regex":
                return ReadRegex(e, path);
            case "range":
                return ReadRange(e, path);
            case "exists":
                {
                    CheckProperties(e, path, ["type", "field", "boost"]);
                    FieldDefinition def = ReadField(e, path, FieldKinds.NotText);
                    return Boosted(new RangeQuery(def.Name, null, null), e, path);
                }

            case "multiFieldTerm":
                return ReadMultiFieldTerm(e, path);
            case "geoRadius":
                {
                    CheckProperties(e, path, ["type", "field", "lat", "lng", "meters", "boost"]);
                    string field = ReadGeoField(e, path);
                    double lat = ReadNumber(Require(e, path, "lat"), Child(path, "lat"), -90, 90);
                    double lng = ReadNumber(Require(e, path, "lng"), Child(path, "lng"), -180, 180);
                    double meters = ReadNumber(Require(e, path, "meters"), Child(path, "meters"), 0, double.MaxValue);
                    return Boosted(new GeoRadiusQuery(field, lat, lng, meters), e, path);
                }

            case "geoBoundingBox":
                {
                    CheckProperties(e, path, ["type", "field", "north", "south", "west", "east", "boost"]);
                    string field = ReadGeoField(e, path);
                    double north = ReadNumber(Require(e, path, "north"), Child(path, "north"), -90, 90);
                    double south = ReadNumber(Require(e, path, "south"), Child(path, "south"), -90, 90);
                    double west = ReadNumber(Require(e, path, "west"), Child(path, "west"), -180, 180);
                    double east = ReadNumber(Require(e, path, "east"), Child(path, "east"), -180, 180);
                    if (south > north)
                    {
                        throw Err(Child(path, "south"), "south is above north.");
                    }

                    return Boosted(new GeoBoundingBoxQuery(field, north, south, west, east), e, path);
                }

            case "bool":
                return ReadBoolQuery(e, path, depth);
            case "functionScore":
                return ReadFunctionScore(e, path, depth);
            case "knn":
                return ReadKnn(e, path, depth);
            case "sparse":
                return ReadSparse(e, path);
            case "hybrid":
                return ReadHybrid(e, path, depth);
            default:
                {
                    string? near = QuerySyntax.Nearest(type, NodeTypes);
                    string hint = near is null ? string.Empty : $" Did you mean \"{near}\"?";
                    throw Err(Child(path, "type"), $"Unknown query type \"{type}\".{hint} Types: {string.Join(", ", NodeTypes)}.", near);
                }
        }
    }

    // A learned sparse node: {"type":"sparse","field":"Expansion","weights":{"token":1.5,...}} (docs/sparse.md).
    private Query ReadSparse(JsonElement e, string path)
    {
        CheckProperties(e, path, ["type", "field", "weights", "boost"]);
        string fp = Child(path, "field");
        string name = ReadString(Require(e, path, "field"), fp);
        if (!_schema.TryGetField(name, out FieldDefinition? def) || def.Type != FieldType.Sparse || !def.Indexed)
        {
            throw Err(fp, $"\"{name}\" is not an indexed sparse field.");
        }

        string wp = Child(path, "weights");
        JsonElement obj = Require(e, path, "weights");
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw Err(wp, "weights is an object from token to weight.");
        }

        var entries = new List<KeyValuePair<string, float>>();
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (entries.Count >= SparseVector.MaxEntries)
            {
                throw Err(wp, $"weights holds at most {SparseVector.MaxEntries.ToString(CultureInfo.InvariantCulture)} tokens.");
            }

            if (p.Name.Length == 0)
            {
                throw Err(wp, "A token must not be empty.");
            }

            double weight = ReadNumber(p.Value, Child(wp, p.Name), 0, float.MaxValue);
            entries.Add(new(p.Name, (float)weight));
        }

        return Boosted(new SparseQuery(name, new SparseVector(entries)), e, path);
    }

    // A nearest-neighbour node: {"type":"knn","field":"Embedding","vector":[...],"k":10,"ef":100,"exact":false,"filter":{...}}.
    private Query ReadKnn(JsonElement e, string path, int depth)
    {
        CheckProperties(e, path, ["type", "field", "vector", "k", "ef", "exact", "filter", "boost"]);
        string fp = Child(path, "field");
        string name = ReadString(Require(e, path, "field"), fp);
        if (!_schema.TryGetField(name, out FieldDefinition? def) || def.Type != FieldType.Vector || !def.Indexed)
        {
            throw Err(fp, $"\"{name}\" is not an indexed vector field.");
        }

        string vp = Child(path, "vector");
        JsonElement array = Require(e, path, "vector");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != def.Dimensions)
        {
            throw Err(vp, $"vector is an array of exactly {def.Dimensions.ToString(CultureInfo.InvariantCulture)} numbers.");
        }

        var vector = new float[def.Dimensions];
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            vector[i] = (float)ReadNumber(item, Index(vp, i), -float.MaxValue, float.MaxValue);
            if (!float.IsFinite(vector[i]))
            {
                throw Err(Index(vp, i), "The number does not fit a 32-bit float.");
            }

            i++;
        }

        int k = e.TryGetProperty("k", out JsonElement kk) ? ReadInt(kk, Child(path, "k"), 1, KnnQuery.MaxK) : 10;
        int ef = e.TryGetProperty("ef", out JsonElement ee) ? ReadInt(ee, Child(path, "ef"), k, KnnQuery.MaxK) : 0;
        bool exact = e.TryGetProperty("exact", out JsonElement ex) && ReadBool(ex, Child(path, "exact"));
        Query? filter = e.TryGetProperty("filter", out JsonElement f) ? ReadNode(f, Child(path, "filter"), depth + 1) : null;
        return Boosted(new KnnQuery(name, vector, k) { Ef = ef, Exact = exact, Filter = filter }, e, path);
    }

    // {"type":"hybrid","text":<node>,"vector":<knn node>,"fusion":"rrf"|"linear","rankConstant":10,"semanticRatio":0.75,"window":100}
    private Query ReadHybrid(JsonElement e, string path, int depth)
    {
        CheckProperties(e, path, ["type", "text", "vector", "sparse", "sparseWeight", "fusion", "rankConstant", "semanticRatio", "window", "boost"]);
        Query text = ReadNode(Require(e, path, "text"), Child(path, "text"), depth + 1);
        string vp = Child(path, "vector");
        KnnQuery? knn = null;
        if (e.TryGetProperty("vector", out JsonElement vectorElement))
        {
            if (ReadNode(vectorElement, vp, depth + 1) is not KnnQuery k)
            {
                throw Err(vp, "The vector arm of a hybrid node is a knn node.");
            }

            knn = k;
        }

        Query? sparse = null;
        string sp = Child(path, "sparse");
        if (e.TryGetProperty("sparse", out JsonElement sparseElement))
        {
            sparse = ReadNode(sparseElement, sp, depth + 1);
        }

        if (knn is null && sparse is null)
        {
            throw Err(path, "A hybrid node needs a \"vector\" arm, a \"sparse\" arm, or both.");
        }

        double? sparseWeight = e.TryGetProperty("sparseWeight", out JsonElement swe) ? ReadNumber(swe, Child(path, "sparseWeight"), 0, 1000) : null;

        (FusionMethod fusion, int rankConstant, double ratio, int window) = ReadHybridSettings(e, path);
        return Boosted(new HybridQuery(text, knn) { Sparse = sparse, SparseWeight = sparseWeight, Fusion = fusion, RankConstant = rankConstant, SemanticRatio = ratio, Window = window }, e, path);
    }

    private static (FusionMethod Fusion, int RankConstant, double Ratio, int Window) ReadHybridSettings(JsonElement e, string path)
    {
        FusionMethod fusion = FusionMethod.ReciprocalRank;
        if (e.TryGetProperty("fusion", out JsonElement fe))
        {
            string name = ReadString(fe, Child(path, "fusion"));
            fusion = name switch
            {
                "rrf" => FusionMethod.ReciprocalRank,
                "linear" => FusionMethod.Linear,
                _ => throw Err(Child(path, "fusion"), $"Unknown fusion \"{name}\". Fusions: rrf, linear."),
            };
        }

        int rank = e.TryGetProperty("rankConstant", out JsonElement rc) ? ReadInt(rc, Child(path, "rankConstant"), 1, 1000) : HybridQuery.DefaultRankConstant;
        double ratio = e.TryGetProperty("semanticRatio", out JsonElement sr) ? ReadNumber(sr, Child(path, "semanticRatio"), 0, 1) : HybridQuery.DefaultSemanticRatio;
        int window = e.TryGetProperty("window", out JsonElement wi) ? ReadInt(wi, Child(path, "window"), 1, HybridQuery.MaxWindow) : 100;
        return (fusion, rank, ratio, window);
    }

    private HybridRequest ReadHybridRequest(JsonElement h, string text)
    {
        const string path = "$.hybrid";
        if (h.ValueKind != JsonValueKind.Object)
        {
            throw Err(path, "hybrid is an object such as {\"semanticRatio\":0.5}.");
        }

        CheckProperties(h, path, ["field", "sparse", "sparseWeight", "fusion", "rankConstant", "semanticRatio", "window"]);
        string? field = null;
        if (h.TryGetProperty("field", out JsonElement fe))
        {
            field = ReadString(fe, Child(path, "field"));
            if (!_schema.TryGetField(field, out FieldDefinition? def) || def.Type != FieldType.Vector || !def.Indexed)
            {
                throw Err(Child(path, "field"), $"\"{field}\" is not an indexed vector field.");
            }
        }

        (FusionMethod fusion, int rank, double ratio, int window) = ReadHybridSettings(h, path);
        bool sparse = h.TryGetProperty("sparse", out JsonElement se) && ReadBool(se, Child(path, "sparse"));
        double? sparseWeight = h.TryGetProperty("sparseWeight", out JsonElement swe) ? ReadNumber(swe, Child(path, "sparseWeight"), 0, 1000) : null;
        if (sparse && !_schema.Fields.Any(static f => f.Type == FieldType.Sparse && f.Indexed))
        {
            throw Err(Child(path, "sparse"), "The schema has no indexed sparse field.");
        }

        return new HybridRequest(text, ratio, fusion, rank, window, field, sparse, sparseWeight);
    }

    private string ReadGeoField(JsonElement obj, string path)
    {
        string fp = Child(path, "field");
        string name = ReadString(Require(obj, path, "field"), fp);
        return SortText.GeoFieldOrThrow(_schema, name, fp);
    }

    private static double ReadNumber(JsonElement e, string path, double min, double max)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double v) || !double.IsFinite(v))
        {
            throw Err(path, "Expected a finite number.");
        }

        if (v < min || v > max)
        {
            throw Err(path, $"Expected a number from {min.ToString(CultureInfo.InvariantCulture)} to {(max == double.MaxValue ? "a finite maximum" : max.ToString(CultureInfo.InvariantCulture))}, got {v.ToString(CultureInfo.InvariantCulture)}.");
        }

        return v;
    }

    private QuerySort ReadSort(JsonElement e)
    {
        const string path = "$.sort";
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() is < 1 or > 16)
        {
            throw Err(path, "sort is an array of 1 to 16 strings such as \"Modified:desc\", \"_score\" or \"_geoPoint(48.85, 2.29):asc\".");
        }

        var keys = new List<SortCriterion>();
        int i = 0;
        foreach (JsonElement item in e.EnumerateArray())
        {
            string ip = Index(path, i++);
            keys.Add(SortText.Read(ReadString(item, ip), _schema, ip));
        }

        return new QuerySort(keys);
    }

    private static JsonElement Require(JsonElement obj, string path, string name)
        => obj.TryGetProperty(name, out JsonElement e) ? e : throw Err(path, $"The property \"{name}\" is required here.");

    private static Query Boosted(Query q, JsonElement obj, string path)
    {
        float boost = ReadBoost(obj, path);
        return boost == 1f ? q : q with { Boost = q.Boost * boost };
    }

    private enum FieldKinds : byte
    {
        TextOrKeyword,
        NotText,
        Term,
    }

    private FieldDefinition ReadField(JsonElement obj, string path, FieldKinds kinds)
    {
        string fp = Child(path, "field");
        string name = ReadString(Require(obj, path, "field"), fp);
        return CheckField(name, fp, kinds);
    }

    private FieldDefinition CheckField(string name, string fp, FieldKinds kinds)
    {
        if (!_schema.TryGetField(name, out FieldDefinition? def) && !_schema.TryGetPath(name, out def))
        {
            string? near = QuerySyntax.Nearest(name, _schema.Fields.Select(static f => f.Name));
            string hint = near is null ? string.Empty : $" Did you mean \"{near}\"?";
            throw Err(fp, $"The schema has no field \"{name}\".{hint}", near);
        }

        if (!def.Indexed)
        {
            throw Err(fp, $"Field \"{name}\" is not searchable.");
        }

        bool ok = kinds switch
        {
            FieldKinds.TextOrKeyword => def.Type is FieldType.Text or FieldType.Keyword or FieldType.Json,
            FieldKinds.NotText => def.Type is FieldType.Keyword or FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool or FieldType.Json,
            _ => def.Type is FieldType.Text or FieldType.Keyword or FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool or FieldType.Json,
        };
        if (!ok)
        {
            throw Err(fp, $"Field \"{name}\" is {def.Type}, which this query cannot use.");
        }

        return def;
    }

    private static LiteralKind KindOf(JsonElement e, string path, out string text)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                text = ReadString(e, path);
                return LiteralKind.Text;
            case JsonValueKind.Number:
                text = e.GetRawText();
                return LiteralKind.Number;
            case JsonValueKind.True:
                text = "true";
                return LiteralKind.Bool;
            case JsonValueKind.False:
                text = "false";
                return LiteralKind.Bool;
            default:
                throw Err(path, "Expected a string, a number or true/false.");
        }
    }

    private static FieldValue BindValue(FieldDefinition def, JsonElement e, string path, BoundRole role, out bool rounded, out bool nothing)
    {
        LiteralKind kind = KindOf(e, path, out string text);
        string? error = ValueBinder.Bind(def, kind, text, role, lenient: false, out FieldValue v, out rounded, out nothing);
        return error is null ? v : throw Err(path, error);
    }

    private Query ReadTerm(JsonElement e, string path)
    {
        CheckProperties(e, path, ["type", "field", "value", "boost"]);
        FieldDefinition def = ReadField(e, path, FieldKinds.Term);
        string vp = Child(path, "value");
        JsonElement value = Require(e, path, "value");
        if (def.Type is FieldType.Keyword or FieldType.Text)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw Err(vp, $"Field \"{def.Name}\" holds text; the value must be a string.");
            }

            return Boosted(new TermQuery(def.Name, ReadString(value, vp)), e, path);
        }

        FieldValue v = BindValue(def, value, vp, BoundRole.Equal, out _, out bool nothing);
        if (v.Kind == FieldValueKind.Text)
        {
            return Boosted(new TermQuery(def.Name, v.AsText()), e, path); // a string at a JSON path
        }

        return Boosted(nothing ? new BooleanQuery([]) : new RangeQuery(def.Name, v, v), e, path);
    }

    private Query ReadPhrase(JsonElement e, string path)
    {
        CheckProperties(e, path, ["type", "field", "terms", "slop", "boost"]);
        FieldDefinition def = ReadField(e, path, FieldKinds.TextOrKeyword);
        string tp = Child(path, "terms");
        JsonElement terms = Require(e, path, "terms");
        if (terms.ValueKind != JsonValueKind.Array)
        {
            throw Err(tp, "Expected an array of terms.");
        }

        int n = terms.GetArrayLength();
        if (n < 1 || n > 256)
        {
            throw Err(tp, "A phrase has 1 to 256 terms.");
        }

        var list = new List<string>(n);
        int i = 0;
        foreach (JsonElement t in terms.EnumerateArray())
        {
            list.Add(ReadString(t, Index(tp, i++)));
        }

        int slop = e.TryGetProperty("slop", out JsonElement s) ? ReadInt(s, Child(path, "slop"), 0, StrictQuery.MaxSlop) : 0;
        return Boosted(new PhraseQuery(def.Name, list) { Slop = slop }, e, path);
    }

    private Query ReadFuzzy(JsonElement e, string path)
    {
        CheckProperties(e, path, ["type", "field", "term", "maxEdits", "prefixLength", "transpositions", "boost"]);
        FieldDefinition def = ReadField(e, path, FieldKinds.TextOrKeyword);
        string term = ReadString(Require(e, path, "term"), Child(path, "term"));
        int edits = e.TryGetProperty("maxEdits", out JsonElement m) ? ReadInt(m, Child(path, "maxEdits"), 0, 2) : 2;
        int prefix = e.TryGetProperty("prefixLength", out JsonElement p) ? ReadInt(p, Child(path, "prefixLength"), 0, 1024) : 0;
        bool transpositions = !e.TryGetProperty("transpositions", out JsonElement t) || ReadBool(t, Child(path, "transpositions"));
        return Boosted(new FuzzyQuery(def.Name, term) { MaxEdits = edits, PrefixLength = prefix, Transpositions = transpositions }, e, path);
    }

    private Query ReadRegex(JsonElement e, string path)
    {
        CheckProperties(e, path, ["type", "field", "pattern", "boost"]);
        FieldDefinition def = ReadField(e, path, FieldKinds.TextOrKeyword);
        string pp = Child(path, "pattern");
        string pattern = ReadString(Require(e, path, "pattern"), pp);
        try
        {
            _ = new Codecs.RegexAutomaton(pattern);
        }
        catch (Codecs.RegexSyntaxException ex)
        {
            throw Err(pp, $"Invalid regular expression at position {ex.Position.ToString(CultureInfo.InvariantCulture)}: {ex.Message}");
        }

        return Boosted(new RegexQuery(def.Name, pattern), e, path);
    }

    private Query ReadRange(JsonElement e, string path)
    {
        CheckProperties(e, path, ["type", "field", "gt", "gte", "lt", "lte", "boost"]);
        FieldDefinition def = ReadField(e, path, FieldKinds.NotText);
        if (def.Type == FieldType.Bool)
        {
            throw Err(Child(path, "field"), $"Field \"{def.Name}\" is a boolean; a range needs a numeric, date or keyword field. Use a term.");
        }

        bool hasGt = e.TryGetProperty("gt", out JsonElement gt);
        bool hasGte = e.TryGetProperty("gte", out JsonElement gte);
        bool hasLt = e.TryGetProperty("lt", out JsonElement lt);
        bool hasLte = e.TryGetProperty("lte", out JsonElement lte);
        if (hasGt && hasGte)
        {
            throw Err(Child(path, "gt"), "Give either \"gt\" or \"gte\", not both.");
        }

        if (hasLt && hasLte)
        {
            throw Err(Child(path, "lt"), "Give either \"lt\" or \"lte\", not both.");
        }

        if (!(hasGt || hasGte || hasLt || hasLte))
        {
            throw Err(path, "A range needs at least one of \"gt\", \"gte\", \"lt\", \"lte\". To match documents that have any value, use type \"exists\".");
        }

        FieldValue? lower = null;
        FieldValue? upper = null;
        bool includeLower = true;
        bool includeUpper = true;
        if (hasGt || hasGte)
        {
            string name = hasGt ? "gt" : "gte";
            lower = BindValue(def, hasGt ? gt : gte, Child(path, name), BoundRole.Lower, out bool rounded, out _);
            includeLower = hasGte || rounded;
        }

        if (hasLt || hasLte)
        {
            string name = hasLt ? "lt" : "lte";
            upper = BindValue(def, hasLt ? lt : lte, Child(path, name), BoundRole.Upper, out bool rounded, out _);
            includeUpper = hasLte || rounded;
        }

        return Boosted(new RangeQuery(def.Name, lower, upper) { IncludeLower = includeLower, IncludeUpper = includeUpper }, e, path);
    }

    private Query ReadMultiFieldTerm(JsonElement e, string path)
    {
        CheckProperties(e, path, ["type", "fields", "term", "boost"]);
        string fp = Child(path, "fields");
        JsonElement fields = Require(e, path, "fields");
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() < 1 || fields.GetArrayLength() > 64)
        {
            throw Err(fp, "Expected an array of 1 to 64 full-text field names.");
        }

        var names = new List<string>();
        int i = 0;
        foreach (JsonElement f in fields.EnumerateArray())
        {
            string ip = Index(fp, i++);
            FieldDefinition def = CheckField(ReadString(f, ip), ip, FieldKinds.TextOrKeyword);
            names.Add(def.Name);
        }

        string term = ReadString(Require(e, path, "term"), Child(path, "term"));
        return Boosted(new MultiFieldTermQuery(names, term), e, path);
    }

    private Query ReadBoolQuery(JsonElement e, string path, int depth)
    {
        CheckProperties(e, path, ["type", "must", "filter", "should", "mustNot", "minimumShouldMatch", "boost"]);
        var clauses = new List<BooleanClause>();
        AddClauses(e, path, "must", Occur.Must, clauses, depth);
        AddClauses(e, path, "filter", Occur.Filter, clauses, depth);
        AddClauses(e, path, "should", Occur.Should, clauses, depth);
        AddClauses(e, path, "mustNot", Occur.MustNot, clauses, depth);
        int msm = e.TryGetProperty("minimumShouldMatch", out JsonElement m) ? ReadInt(m, Child(path, "minimumShouldMatch"), 0, 1024) : 0;
        return Boosted(new BooleanQuery(clauses) { MinimumShouldMatch = msm }, e, path);
    }

    private void AddClauses(JsonElement e, string path, string name, Occur occur, List<BooleanClause> into, int depth)
    {
        if (!e.TryGetProperty(name, out JsonElement arr))
        {
            return;
        }

        string ap = Child(path, name);
        if (arr.ValueKind != JsonValueKind.Array)
        {
            throw Err(ap, "Expected an array of query objects.");
        }

        int i = 0;
        foreach (JsonElement item in arr.EnumerateArray())
        {
            into.Add(new BooleanClause(ReadNode(item, Index(ap, i++), depth + 1), occur));
        }
    }
}
