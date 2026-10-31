using System.Text;
using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex.Fuzz;

/// <summary>
/// The strict query syntax, filter strings and query JSON. Arbitrary input may only raise <see cref="QueryParseException"/>; what parses
/// must print and read back to an equal value, and the query it compiles to must run on a small index without a query error (the schema
/// checks belong to the parsers, so a compiled query the engine refuses is a missing check).
/// </summary>
internal static class QueryTextTargets
{
    private static SearchIndex<FuzzDocument>? built;
    private static Schema? schema;

    private static SearchIndex<FuzzDocument> Index => built ??= Build();

    private static Schema Schema => schema ??= new FuzzMapping().Schema;

    private static SearchIndex<FuzzDocument> Build()
    {
        SearchMappings.Register(new FuzzMapping());
        SearchIndex<FuzzDocument> index = SearchIndex.InMemory<FuzzDocument>();
        string[] words = ["overdue", "acme", "corp", "draft", "cat", "dog", "state", "art", "running", "runner", "the", "of"];
        for (int i = 0; i < 24; i++)
        {
            index.Upsert(new FuzzDocument
            {
                Id = "d" + i,
                Title = words[i % words.Length] + " " + words[(i * 3) % words.Length],
                Body = string.Join(' ', Enumerable.Range(0, 6).Select(k => words[(i + (k * 5)) % words.Length])),
                Tag = words[i % 4],
                Number = i,
            });
        }

        index.Commit();
        return index;
    }

    private static string Utf16(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(data[2 * i] | (data[(2 * i) + 1] << 8));
        }

        return new string(chars);
    }

    public static void RunStrict(ReadOnlySpan<byte> data)
    {
        string text = Utf16(data);
        StrictNode tree;
        try
        {
            tree = StrictQuery.ParseSyntax(text);
        }
        catch (QueryParseException e)
        {
            CheckError(e, text);
            return;
        }

        // Whatever the parser accepts has a text form that reads back to the same tree.
        string printed = StrictQuery.Print(tree);
        Require(tree.Equals(StrictQuery.ParseSyntax(printed)), "strict print/parse identity");
        try
        {
            Query q = StrictQuery.Bind(tree, Schema, text);
            Run(q);
        }
        catch (QueryParseException e)
        {
            CheckError(e, text);
        }
    }

    public static void RunFilter(ReadOnlySpan<byte> data)
    {
        string text = Utf16(data);
        Query q;
        try
        {
            q = FilterString.Parse(text, Schema);
        }
        catch (QueryParseException e)
        {
            CheckError(e, text);
            return;
        }

        string? printed = null;
        try
        {
            printed = FilterString.Print(q, Schema);
        }
        catch (ArgumentException)
        {
            // Queries that match nothing by construction (an empty IN list, a fractional equality on an integer) have no filter text.
        }

        if (printed is not null)
        {
            Require(q.Equals(FilterString.Parse(printed, Schema)), "filter print/parse identity");
        }

        Run(q);
    }

    public static void RunJson(ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        QueryRequest request;
        try
        {
            request = QueryJson.Read(bytes, Schema);
        }
        catch (QueryParseException e)
        {
            Require(e.Path is not null || e.Line >= 1, "json error position");
            return;
        }

        string json = QueryJson.Write(request);
        QueryRequest again = QueryJson.Read(Encoding.UTF8.GetBytes(json), Schema);
        Require(request.Equals(again), "json round trip");
        Require(QueryJson.Write(again) == json, "json canonical");
        Run(request.ToQuery());
    }

    private static void Run(Query q)
    {
        try
        {
            SearchResults<FuzzDocument> page = Index.SearchBy(q).Take(3).Execute();
            Require(page.Count <= 3, "page size");
        }
        catch (TooManyTermsException)
        {
            // A prefix or fuzzy query may legitimately match more terms than the engine expands.
        }
    }

    private static void CheckError(QueryParseException e, string text)
    {
        Require(e.Offset >= 0 && e.Offset <= text.Length && e.Line >= 1 && e.Column >= 1 && e.Detail.Length > 0, "error position");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("query text invariant failed: " + what);
        }
    }

    public static void WriteSeeds(string directory, int kind)
    {
        string[] strict =
        [
            "overdue \"acme corp\"~2 -draft", "Title:(cat OR dog)^2 AND NOT Tag:acm*", "Number:[3 TO 9} Body:/ru.*/", "Tag:cta~1", "(a (b (c", "x:y:z", "Number:>=4 OR *",
        ];
        string[] filter =
        [
            "Number > 3 AND (Tag = cat OR Tag = 'dog')", "NOT Number 2 TO 8", "Tag IN [cat, dog] AND Number EXISTS", "Number != 2.5", "Tag = \"unterminated", "Number IS NULL OR Tag IS NOT EMPTY",
        ];
        string[] json =
        [
            "{\"v\":1,\"q\":\"overdue acme\",\"filter\":\"Number > 3\",\"limit\":5}",
            "{\"v\":1,\"query\":{\"type\":\"bool\",\"must\":[{\"type\":\"term\",\"field\":\"Tag\",\"value\":\"cat\"}],\"mustNot\":[{\"type\":\"range\",\"field\":\"Number\",\"gte\":3,\"lt\":9}]},\"highlight\":true}",
            "{\"v\":1,\"query\":{\"type\":\"strict\",\"text\":\"Title:cat~1\"},\"countExactly\":true}",
            "{\"v\":1,\"query\":{\"type\":\"fuzzy\",\"field\":\"Body\",\"term\":\"acme\",\"maxEdits\":1}}",
            "{\"v\":1,\"query\":{\"type\":\"bool\",\"should\":[{\"type\":\"phrase\",\"field\":\"Body\",\"terms\":[\"acme\",\"corp\"],\"slop\":2},{\"type\":\"prefix\",\"field\":\"Tag\",\"prefix\":\"ca\"},{\"type\":\"regex\",\"field\":\"Tag\",\"pattern\":\"d.*\"},{\"type\":\"exists\",\"field\":\"Number\"}],\"minimumShouldMatch\":2,\"boost\":1.5},\"offset\":3}",
            "{\"v\":1,\"query\":{\"type\":\"multiFieldTerm\",\"fields\":[\"Title\",\"Body\"],\"term\":\"cat\"},\"filter\":{\"type\":\"range\",\"field\":\"Number\",\"gt\":2.5}}",
            "{\"v\":1,\"query\":{\"type\":\"text\",\"text\":\"cat OR dog\"},\"filter\":{\"type\":\"term\",\"field\":\"Number\",\"value\":4}}",
            "{\"v\":2}",
        ];
        string[] seeds = kind == 0 ? strict : kind == 1 ? filter : json;
        for (int i = 0; i < seeds.Length; i++)
        {
            byte[] bytes;
            if (kind == 2)
            {
                bytes = Encoding.UTF8.GetBytes(seeds[i]);
            }
            else
            {
                bytes = new byte[seeds[i].Length * 2];
                for (int c = 0; c < seeds[i].Length; c++)
                {
                    bytes[2 * c] = (byte)seeds[i][c];
                    bytes[(2 * c) + 1] = (byte)(seeds[i][c] >> 8);
                }
            }

            File.WriteAllBytes(Path.Combine(directory, "seed-" + i), bytes);
        }
    }
}
