using System.Text.Json;
using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex.AotSmoke;

/// <summary>The data forms of a search: tool definition, query JSON, strict syntax and filter strings, compiled and run under NativeAOT.</summary>
internal static class QueryFormsScenario
{
    public static void Run()
    {
        using SearchIndex<Invoice> index = SearchIndex.InMemory<Invoice>();
        index.Upsert(new Invoice { Id = "inv-1", Title = "Overdue notice", Body = "Payment from Acme Corp is overdue.", Modified = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice"] });
        index.Upsert(new Invoice { Id = "inv-2", Title = "Overdue draft", Body = "Acme Corp draft.", Modified = new DateTime(2024, 3, 2, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice", "draft"] });
        index.Upsert(new Invoice { Id = "inv-3", Title = "Old", Body = "Acme Corp overdue in 2022.", Modified = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), Tags = ["invoice"] });
        index.Commit();

        string tool = SearchTool.For<Invoice>();
        using (JsonDocument doc = JsonDocument.Parse(tool))
        {
            Require(doc.RootElement.GetProperty("name").GetString() == "search_Invoice", "tool name");
            Require(doc.RootElement.GetProperty("input_schema").GetProperty("$defs").GetProperty("node").GetProperty("oneOf").GetArrayLength() > 5, "tool schema");
        }

        // A tool call: free text, an exact filter and a structured query, paged.
        const string call = """
            {"v":1,"q":"overdue","filter":"Tags = invoice AND Modified > 2024-01-01 AND NOT Tags = draft",
             "query":{"type":"bool","should":[{"type":"phrase","field":"Title","terms":["overdue","notice"]}],"minimumShouldMatch":1},
             "limit":10,"countExactly":true,"highlight":true}
            """;
        SearchResults<Invoice> hits = index.Parse(call).Execute();
        Require(hits.Count == 1 && hits[0].Id == "inv-1" && hits.TotalHits == 1, $"parse and run {hits.Count} {(hits.Count > 0 ? hits[0].Id : "-")} {hits.TotalHits}");
        Require(index.TryParse("""{"v":1,"filter":"Tagz = x"}""", out _, out QueryParseException? error) is false && error!.Path == "$.filter" && error.Suggestion == "Tags", "named error");

        Require(index.SearchStrict("Title:overdue AND NOT Tags:draft").Count() == 1, "strict query");
        bool threw = false;
        try
        {
            _ = index.SearchStrict("Titel:overdue");
        }
        catch (QueryParseException e)
        {
            threw = e.Offset == 0 && e.Suggestion == "Title";
        }

        Require(threw, "strict error position");

        Query filter = FilterString.Parse("Modified 2024-01-01 TO 2024-12-31 OR Tags IN [draft]", index.Schema);
        Require(FilterString.Print(filter, index.Schema) == "Modified \"2024-01-01T00:00:00.000Z\" TO \"2024-12-31T00:00:00.000Z\" OR Tags = \"draft\"", "filter text");

        QueryRequest request = QueryJson.Read(call, index.Schema);
        Require(QueryJson.Read(QueryJson.Write(request), index.Schema).Equals(request), "json round trip");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("query forms scenario failed: " + what);
        }
    }
}
