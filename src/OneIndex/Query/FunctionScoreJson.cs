using System.Globalization;
using System.Text.Json;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Queries;

public static partial class QueryJson
{
    // {"type":"proximity","query":<node>,"words":[<word node>...],"weight":0.5}; the default weight is left out.
    private static void WriteProximity(Utf8JsonWriter w, ProximityQuery q)
    {
        w.WriteString("type", "proximity");
        w.WritePropertyName("query");
        WriteNode(w, q.Inner);
        w.WriteStartArray("words");
        foreach (Query word in q.Words)
        {
            WriteNode(w, word);
        }

        w.WriteEndArray();
        if (q.Weight != Proximity.DefaultWeight)
        {
            w.WriteNumber("weight", q.Weight);
        }
    }

    private static void WriteMoreLikeThis(Utf8JsonWriter w, MoreLikeThisQuery q)
    {
        w.WriteString("type", "moreLikeThis");
        if (q.Text is not null)
        {
            w.WriteString("text", q.Text);
        }
        else
        {
            w.WriteString("id", q.DocumentId);
        }

        if (q.Fields.Count > 0)
        {
            w.WriteStartArray("fields");
            foreach (string f in q.Fields)
            {
                w.WriteStringValue(f);
            }

            w.WriteEndArray();
        }

        if (q.MaxQueryTerms != 25)
        {
            w.WriteNumber("maxQueryTerms", q.MaxQueryTerms);
        }

        if (q.MinTermFrequency != 1)
        {
            w.WriteNumber("minTermFrequency", q.MinTermFrequency);
        }

        if (q.MinDocumentFrequency != 1)
        {
            w.WriteNumber("minDocFrequency", q.MinDocumentFrequency);
        }

        if (q.MinimumShouldMatchFraction != 0.3)
        {
            w.WriteNumber("minimumShouldMatch", q.MinimumShouldMatchFraction);
        }

        if (!q.BoostTerms)
        {
            w.WriteBoolean("boostTerms", false);
        }

        if (q.IncludeSource)
        {
            w.WriteBoolean("includeSource", true);
        }
    }

    // The function-score node. Defaults are left out, so a document that spells them out reads back to the same query and writes the same text.
    private static void WriteFunctionScore(Utf8JsonWriter w, FunctionScoreQuery q)
    {
        w.WriteString("type", "functionScore");
        w.WritePropertyName("query");
        WriteNode(w, q.Inner);
        w.WriteStartArray("functions");
        foreach (ScoreFunction f in q.Functions)
        {
            WriteFunction(w, f);
        }

        w.WriteEndArray();
        if (q.ScoreMode != FunctionScoreMode.Multiply)
        {
            w.WriteString("scoreMode", FunctionNames.ScoreModes[(int)q.ScoreMode]);
        }

        if (q.BoostMode != FunctionBoostMode.Multiply)
        {
            w.WriteString("boostMode", FunctionNames.BoostModes[(int)q.BoostMode]);
        }

        if (q.MaxBoost != float.MaxValue)
        {
            w.WriteNumber("maxBoost", q.MaxBoost);
        }

        if (q.MinScore is { } min)
        {
            w.WriteNumber("minScore", min);
        }
    }

    private static void WriteFunction(Utf8JsonWriter w, ScoreFunction f)
    {
        w.WriteStartObject();
        switch (f)
        {
            case WeightFunction:
                w.WriteString("type", "weight");
                break;
            case FieldValueFactorFunction v:
                w.WriteString("type", "fieldValueFactor");
                w.WriteString("field", v.Field);
                if (v.Factor != 1d)
                {
                    w.WriteNumber("factor", v.Factor);
                }

                if (v.Modifier != FieldValueModifier.None)
                {
                    w.WriteString("modifier", FunctionNames.Modifier(v.Modifier));
                }

                if (v.Missing is { } m)
                {
                    w.WriteNumber("missing", m);
                }

                break;
            case DecayFunction d:
                w.WriteString("type", DecayMath.Name(d.Shape));
                w.WriteString("field", d.Field);
                w.WriteNumber("origin", d.Origin);
                w.WriteNumber("scale", d.Scale);
                WriteDecayTail(w, d.Offset, d.Decay);
                break;
            case GeoDecayFunction g:
                w.WriteString("type", DecayMath.Name(g.Shape));
                w.WriteString("field", g.Field);
                w.WriteStartObject("origin");
                w.WriteNumber("lat", g.Latitude);
                w.WriteNumber("lng", g.Longitude);
                w.WriteEndObject();
                w.WriteNumber("scale", g.Scale);
                WriteDecayTail(w, g.Offset, g.Decay);
                break;
            default:
                throw new ArgumentException($"A {f.GetType().Name} cannot be written as query JSON.", nameof(f));
        }

        if (f.Weight != 1f)
        {
            w.WriteNumber("weight", f.Weight);
        }

        if (f.Filter is { } filter)
        {
            w.WritePropertyName("filter");
            WriteNode(w, filter);
        }

        w.WriteEndObject();
    }

    private static void WriteDecayTail(Utf8JsonWriter w, double offset, double decay)
    {
        if (offset != 0d)
        {
            w.WriteNumber("offset", offset);
        }

        if (decay != 0.5d)
        {
            w.WriteNumber("decay", decay);
        }
    }
}

internal sealed partial class QueryJsonReader
{
    private Query ReadProximity(JsonElement e, string path, int depth)
    {
        CheckProperties(e, path, ["type", "query", "words", "weight", "boost"]);
        Query inner = ReadNode(Require(e, path, "query"), Child(path, "query"), depth + 1);
        string wp = Child(path, "words");
        JsonElement arr = Require(e, path, "words");
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > 64)
        {
            throw Err(wp, "words is an array of at most 64 single-word queries (term, multiFieldTerm or typoTerm).");
        }

        var words = new List<Query>();
        int i = 0;
        foreach (JsonElement item in arr.EnumerateArray())
        {
            string ip = Index(wp, i++);
            Query word = ReadNode(item, ip, depth + 1);
            if (!WordResolver.IsWord(word))
            {
                throw Err(ip, "A proximity word is a term, multiFieldTerm or typoTerm query.");
            }

            words.Add(word);
        }

        float weight = e.TryGetProperty("weight", out JsonElement wt) ? (float)ReadNumber(wt, Child(path, "weight"), 0, 4) : Proximity.DefaultWeight;
        return Boosted(new ProximityQuery(inner, words) { Weight = weight }, e, path);
    }

    private static readonly string[] FunctionTypes = ["weight", "fieldValueFactor", "gauss", "exp", "linear"];

    private Query ReadFunctionScore(JsonElement e, string path, int depth)
    {
        CheckProperties(e, path, ["type", "query", "functions", "scoreMode", "boostMode", "maxBoost", "minScore", "boost"]);
        Query inner = e.TryGetProperty("query", out JsonElement q) ? ReadNode(q, Child(path, "query"), depth + 1) : new MatchAllQuery();
        string fp = Child(path, "functions");
        JsonElement arr = Require(e, path, "functions");
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() is < 1 or > FunctionScoreQuery.MaxFunctions)
        {
            throw Err(fp, $"functions is an array of 1 to {FunctionScoreQuery.MaxFunctions.ToString(CultureInfo.InvariantCulture)} function objects, such as {{\"type\":\"fieldValueFactor\",\"field\":\"Rating\",\"modifier\":\"log1p\"}}.");
        }

        var functions = new List<ScoreFunction>();
        int i = 0;
        foreach (JsonElement item in arr.EnumerateArray())
        {
            functions.Add(ReadFunction(item, Index(fp, i++), depth + 1));
        }

        FunctionScoreMode scoreMode = e.TryGetProperty("scoreMode", out JsonElement sm)
            ? (FunctionScoreMode)ReadChoice(sm, Child(path, "scoreMode"), FunctionNames.ScoreModes) : FunctionScoreMode.Multiply;
        FunctionBoostMode boostMode = e.TryGetProperty("boostMode", out JsonElement bm)
            ? (FunctionBoostMode)ReadChoice(bm, Child(path, "boostMode"), FunctionNames.BoostModes) : FunctionBoostMode.Multiply;
        float maxBoost = float.MaxValue;
        if (e.TryGetProperty("maxBoost", out JsonElement mb))
        {
            maxBoost = (float)ReadNumber(mb, Child(path, "maxBoost"), double.Epsilon, float.MaxValue);
        }

        float? minScore = e.TryGetProperty("minScore", out JsonElement ms) ? (float)ReadNumber(ms, Child(path, "minScore"), 0, float.MaxValue) : null;
        return Boosted(new FunctionScoreQuery(inner, functions) { ScoreMode = scoreMode, BoostMode = boostMode, MaxBoost = maxBoost, MinScore = minScore }, e, path);
    }

    private static int ReadChoice(JsonElement e, string path, string[] names)
    {
        string text = ReadString(e, path);
        int at = Array.IndexOf(names, text);
        if (at < 0)
        {
            string? near = QuerySyntax.Nearest(text, names);
            throw Err(path, $"Unknown value \"{text}\".{(near is null ? string.Empty : $" Did you mean \"{near}\"?")} Values: {string.Join(", ", names)}.", near);
        }

        return at;
    }

    private ScoreFunction ReadFunction(JsonElement e, string path, int depth)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw Err(path, "Expected a function object, such as {\"type\":\"gauss\",\"field\":\"Opened\",\"origin\":\"2024-06-01\",\"scale\":86400000}.");
        }

        if (!e.TryGetProperty("type", out JsonElement typeElement))
        {
            throw Err(path, "A function needs a \"type\". Types: " + string.Join(", ", FunctionTypes) + ".");
        }

        string type = ReadString(typeElement, Child(path, "type"));
        float weight = 1f;
        if (e.TryGetProperty("weight", out JsonElement w))
        {
            weight = (float)ReadNumber(w, Child(path, "weight"), 0, float.MaxValue);
        }

        Query? filter = null;
        if (e.TryGetProperty("filter", out JsonElement f))
        {
            filter = f.ValueKind == JsonValueKind.String ? ReadFilterString(f, Child(path, "filter")) : ReadNode(f, Child(path, "filter"), depth + 1);
        }

        ScoreFunction function;
        switch (type)
        {
            case "weight":
                CheckProperties(e, path, ["type", "weight", "filter"]);
                function = new WeightFunction();
                break;
            case "fieldValueFactor":
                {
                    CheckProperties(e, path, ["type", "field", "factor", "modifier", "missing", "weight", "filter"]);
                    string field = ReadFunctionField(e, path);
                    double factor = e.TryGetProperty("factor", out JsonElement fe) ? ReadNumber(fe, Child(path, "factor"), -double.MaxValue, double.MaxValue) : 1d;
                    var modifier = e.TryGetProperty("modifier", out JsonElement me) ? (FieldValueModifier)ReadChoice(me, Child(path, "modifier"), FunctionNames.Modifiers) : FieldValueModifier.None;
                    double? missing = e.TryGetProperty("missing", out JsonElement mi) ? ReadNumber(mi, Child(path, "missing"), -double.MaxValue, double.MaxValue) : null;
                    function = new FieldValueFactorFunction(field) { Factor = factor, Modifier = modifier, Missing = missing };
                    break;
                }

            case "gauss":
            case "exp":
            case "linear":
                {
                    CheckProperties(e, path, ["type", "field", "origin", "scale", "offset", "decay", "weight", "filter"]);
                    var shape = (DecayShape)Array.IndexOf(FunctionNames.Shapes, type);
                    function = ReadDecay(e, path, shape);
                    break;
                }

            default:
                {
                    string? near = QuerySyntax.Nearest(type, FunctionTypes);
                    throw Err(Child(path, "type"), $"Unknown function type \"{type}\".{(near is null ? string.Empty : $" Did you mean \"{near}\"?")} Types: {string.Join(", ", FunctionTypes)}.", near);
                }
        }

        function = function with { Weight = weight, Filter = filter };
        try
        {
            function.Validate(_schema);
        }
        catch (QueryException ex)
        {
            throw Err(path, ex.Message);
        }

        return function;
    }

    // A function reads a column, so the field needs no index; it only has to exist.
    private string ReadFunctionField(JsonElement obj, string path)
    {
        string fp = Child(path, "field");
        string name = ReadString(Require(obj, path, "field"), fp);
        if (!_schema.TryGetField(name, out _))
        {
            string? near = QuerySyntax.Nearest(name, _schema.Fields.Select(static x => x.Name));
            throw Err(fp, $"The schema has no field \"{name}\".{(near is null ? string.Empty : $" Did you mean \"{near}\"?")}", near);
        }

        return name;
    }

    private ScoreFunction ReadDecay(JsonElement e, string path, DecayShape shape)
    {
        string field = ReadFunctionField(e, path);
        _ = _schema.TryGetField(field, out FieldDefinition? def);
        double scale = ReadNumber(Require(e, path, "scale"), Child(path, "scale"), double.Epsilon, double.MaxValue);
        double offset = e.TryGetProperty("offset", out JsonElement oe) ? ReadNumber(oe, Child(path, "offset"), 0, double.MaxValue) : 0d;
        double decay = e.TryGetProperty("decay", out JsonElement de) ? ReadNumber(de, Child(path, "decay"), double.Epsilon, 1d) : 0.5d;
        string op = Child(path, "origin");
        JsonElement origin = Require(e, path, "origin");
        if (def!.Type == FieldType.GeoPoint)
        {
            if (origin.ValueKind != JsonValueKind.Object)
            {
                throw Err(op, "A geo point origin is an object such as {\"lat\":48.85,\"lng\":2.29}.");
            }

            CheckProperties(origin, op, ["lat", "lng"]);
            double lat = ReadNumber(Require(origin, op, "lat"), Child(op, "lat"), -90, 90);
            double lng = ReadNumber(Require(origin, op, "lng"), Child(op, "lng"), -180, 180);
            return new GeoDecayFunction(shape, field, lat, lng, scale) { Offset = offset, Decay = decay };
        }

        double value;
        if (def.Type == FieldType.Date && origin.ValueKind == JsonValueKind.String)
        {
            string text = ReadString(origin, op);
            if (!ValueBinder.TryParseDate(text, out long ms))
            {
                throw Err(op, $"Expected a date such as 2024-01-31 or 2024-01-31T10:00:00Z, not \"{text}\".");
            }

            value = ms;
        }
        else
        {
            value = ReadNumber(origin, op, -double.MaxValue, double.MaxValue);
        }

        return new DecayFunction(shape, field, value, scale) { Offset = offset, Decay = decay };
    }
}
