using System.Text.Json;
using OneIndex.Index;

namespace OneIndex.AotSmoke;

/// <summary>A document with a dynamic JSON property. This is the first version of the type.</summary>
public class Ticket
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Subject { get; set; } = string.Empty;

    [Keyword]
    public string Queue { get; set; } = string.Empty;

    [Dynamic]
    public JsonElement Fields { get; set; }
}

/// <summary>The same index, a release later: the subject is stemmed and the queue is filterable and a facet.</summary>
public class TicketV2
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Language = "en")]
    public string Subject { get; set; } = string.Empty;

    [Keyword]
    [Filterable]
    [Facet]
    public string Queue { get; set; } = string.Empty;

    [Dynamic]
    public JsonElement Fields { get; set; }
}

/// <summary>Indexes JSON by path, filters on it, then reopens the directory with a changed document type and lets the index rebuild itself.</summary>
internal static class DynamicAndEvolutionScenario
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "oneindex-aot-evolution-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (SearchIndex<Ticket> v1 = SearchIndex.Open<Ticket>(directory))
            {
                for (int i = 0; i < 30; i++)
                {
                    v1.Upsert(new Ticket
                    {
                        Id = "t" + i,
                        Subject = i % 2 == 0 ? "printer is running out of toner" : "login page broken",
                        Queue = i % 3 == 0 ? "hardware" : "web",
                        Fields = JsonDocument.Parse("{\"customer\":{\"tier\":" + (i % 4) + ",\"name\":\"c" + (i % 5) + "\"},\"tags\":[\"a\",\"b" + (i % 2) + "\"]}").RootElement,
                    });
                    if (i % 10 == 9)
                    {
                        v1.Commit();
                    }
                }

                Require(v1.Search().Where(x => x.Fields.Path("customer.tier") >= 2 && x.Fields.Path("tags") == "b1").CountExactly().Take(1).Count() == 7, "dynamic filter");
                Require(v1.Search().Where(x => x.Fields.Path("customer.name") == "c3").CountExactly().Take(1).Count() == 6, "dynamic keyword");
            }

            using SearchIndex<TicketV2> v2 = SearchIndex.Open<TicketV2>(directory);
            Require(v2.Count == 30, "documents after the type changed");
            v2.CompleteSchemaEvolution();
            Require(!v2.SchemaEvolution.InProgress, "rebuild finished");
            Require(v2.Search().Where(x => x.Queue == "hardware").CountExactly().Take(1).Count() == 10, "new filter on rebuilt segments");
            Require(v2.Search("run").CountExactly().Take(1).Count() == 15, "stemmed text after the rebuild");
            Require(v2.Search().Where(x => x.Fields.Path("customer.tier") == 3).CountExactly().Take(1).Count() == 7, "dynamic filter after the rebuild");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("dynamic/evolution scenario failed: " + what);
        }
    }
}
