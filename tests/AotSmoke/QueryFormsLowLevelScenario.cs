using System.Text.Json;
using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex.AotSmoke;

/// <summary>
/// The data forms of a search without the source generator or an index: the strict syntax, filter strings, query JSON and the tool schema
/// over a schema built by hand. Shared by the NativeAOT app and the browser app.
/// </summary>
internal static class QueryFormsLowLevelScenario
{
    public static void Run()
    {
        Schema schema = new SchemaBuilder()
            .Id("Id")
            .Add(new FieldDefinition("Title", FieldType.Text) { Indexed = true, Analyzer = "standard" })
            .Add(new FieldDefinition("Tag", FieldType.Keyword) { Indexed = true })
            .Add(new FieldDefinition("Count", FieldType.Integral) { Indexed = true, Column = true })
            .Add(new FieldDefinition("Due", FieldType.Date) { Indexed = true, Column = true })
            .Build();

        Query strict = StrictQuery.Parse("Title:(invoice OR receipt)^2 AND NOT Tag:draft AND Count:[1 TO 9}", schema);
        Require(strict is BooleanQuery, "strict query");
        StrictNode tree = StrictQuery.ParseSyntax("a~1 OR \"b c\"~2 OR d*");
        Require(tree.Equals(StrictQuery.ParseSyntax(StrictQuery.Print(tree))), "strict round trip");

        Query filter = FilterString.Parse("Count 2 TO 5 AND (Tag = 'x' OR Due >= 2024-01-01) AND NOT Tag IN [a, b]", schema);
        Require(filter.Equals(FilterString.Parse(FilterString.Print(filter, schema), schema)), "filter round trip");

        string json = """{"v":1,"q":"invoice","filter":"Count > 3","query":{"type":"range","field":"Due","gte":"2024-01-01"},"limit":5}""";
        QueryRequest request = QueryJson.Read(json, schema);
        Require(request.Limit == 5 && QueryJson.Read(QueryJson.Write(request), schema).Equals(request), "json round trip");

        string error = string.Empty;
        try
        {
            _ = QueryJson.Read("""{"v":1,"query":{"type":"term","field":"Tg","value":"x"}}""", schema);
        }
        catch (QueryParseException e)
        {
            error = e.Path + "|" + e.Suggestion;
        }

        Require(error == "$.query.field|Tag", "json error path");
        try
        {
            _ = StrictQuery.Parse("Titel:x", schema);
        }
        catch (QueryParseException e)
        {
            error = e.Offset + "|" + e.Suggestion;
        }

        Require(error == "0|Title", "strict error");

        using JsonDocument tool = JsonDocument.Parse(SearchTool.For(schema, "Doc"));
        Require(tool.RootElement.GetProperty("name").GetString() == "search_Doc", "tool name");
        Require(tool.RootElement.GetProperty("input_schema").GetProperty("$defs").TryGetProperty("rangeDate", out _), "tool schema");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("query forms (low level) failed: " + what);
        }
    }
}
