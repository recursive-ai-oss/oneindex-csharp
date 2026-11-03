using System.Globalization;
using System.Text.Json;
using OneIndex.Files;
using OneIndex.Storage;
using OneIndex.Models;

namespace OneIndex.Cli;

internal static class QueryCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var a = new Arguments(args, ["--limit", "--filter"], ["--json", "--hybrid", "--files"]);
        a.Expect(2, "oneindex query <index> \"<text>\" [--limit N] [--filter \"<filter>\"] [--hybrid] [--json] [--files]");
        string indexPath = Path.GetFullPath(a.Positionals[0]);
        if (!Directory.Exists(indexPath))
        {
            error.WriteLine($"oneindex: the index '{indexPath}' does not exist.");
            return 1;
        }

        int limit = a.GetInt("--limit", 10, 1, 1000);
        BgeSmallEmbedder? embedder = a.Has("--hybrid") ? BgeSmallEmbedder.Load() : null;
        using SearchIndex<FilePassage> index = SearchIndex.OpenReadOnly<FilePassage>(indexPath, embedder);

        SearchQuery<FilePassage> q = index.Passages(a.Positionals[1]);
        if (a.Get("--filter") is { } filter)
        {
            try
            {
                q = q.Where(filter);
            }
            catch (OneIndex.Queries.QueryParseException e)
            {
                error.WriteLine($"oneindex: --filter: {e.Message}");
                return 2;
            }
        }

        if (a.Has("--hybrid"))
        {
            q = q.Hybrid();
        }

        if (a.Has("--files"))
        {
            q = q.Distinct(p => p.FileId);
        }

        SearchResults<FilePassage> results = q.Take(limit).Execute(ct);
        if (a.Has("--json"))
        {
            WriteJson(output, results);
        }
        else
        {
            WriteText(output, results);
        }

        return 0;
    }

    private static void WriteText(TextWriter output, SearchResults<FilePassage> results)
    {
        if (results.Count == 0)
        {
            output.WriteLine("No matches.");
            return;
        }

        int n = 1;
        foreach (SearchHit<FilePassage> hit in results)
        {
            Citation c = hit.Cite();
            string where = c.Entry is null ? c.File : $"{c.File} ({c.Entry})";
            string page = c.Page > 0 ? $" page {c.Page}" : string.Empty;
            output.WriteLine($"{n++}. {where}{page} [{c.Start}-{c.End}]  score {hit.Score.ToString("0.000", CultureInfo.InvariantCulture)}");
            if (c.Heading.Length > 0)
            {
                output.WriteLine($"   {c.Heading}");
            }

            output.WriteLine($"   {Snippet(c.Text)}");
        }
    }

    private static string Snippet(string text)
    {
        string one = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 200 ? one : string.Concat(one.AsSpan(0, 197), "...");
    }

    private static void WriteJson(TextWriter output, SearchResults<FilePassage> results)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("totalHits", results.TotalHits);
            w.WriteBoolean("totalHitsExact", results.TotalHitsExact);
            w.WriteStartArray("hits");
            foreach (SearchHit<FilePassage> hit in results)
            {
                Citation c = hit.Cite();
                w.WriteStartObject();
                w.WriteString("id", hit.Id);
                w.WriteNumber("score", hit.Score);
                w.WriteStartObject("citation");
                w.WriteString("file", c.File);
                if (c.Entry is not null)
                {
                    w.WriteString("entry", c.Entry);
                }

                w.WriteString("format", c.Format);
                w.WriteNumber("page", c.Page);
                w.WriteNumber("start", c.Start);
                w.WriteNumber("end", c.End);
                if (c.Heading.Length > 0)
                {
                    w.WriteString("heading", c.Heading);
                }

                if (c.Boxes.Count > 0)
                {
                    w.WriteString("boxUnits", c.BoxUnits);
                    w.WriteStartArray("boxes");
                    foreach (SourceBox b in c.Boxes)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(b.Left);
                        w.WriteNumberValue(b.Top);
                        w.WriteNumberValue(b.Right);
                        w.WriteNumberValue(b.Bottom);
                        w.WriteEndArray();
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
                w.WriteString("text", c.Text);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
    }
}
