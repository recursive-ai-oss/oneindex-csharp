using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Suggest;

namespace OneIndex.Tests;

public class QueryDataFormTests
{
    private static readonly Schema Schema = QueryGen.Schema;

    // ---------------------------------------------------------------- positioned errors

    private static void CheckError(List<string> failures, string input, Func<string, QueryParseException?> parse, int offset, int line, int column, string contains, string? suggestion = null)
    {
        QueryParseException? e = parse(input);
        if (e is null)
        {
            failures.Add($"{Escape(input)}: no error");
            return;
        }

        if (e.Offset != offset || e.Line != line || e.Column != column || !e.Detail.Contains(contains, StringComparison.Ordinal) || e.Suggestion != suggestion)
        {
            failures.Add($"{Escape(input)}: got offset {e.Offset} line {e.Line} col {e.Column} suggestion {e.Suggestion} '{e.Detail}', want {offset}/{line}/{column} suggestion {suggestion} containing '{contains}'");
        }
    }

    private static string Escape(string s) => s.Replace("\n", "\\n", StringComparison.Ordinal);

    private static QueryParseException? Strict(string s)
        => StrictQuery.TryParse(s, Schema, out _, out QueryParseException? e) ? null : e;

    private static QueryParseException? Filter(string s)
        => FilterString.TryParse(s, Schema, out _, out QueryParseException? e) ? null : e;

    [Fact]
    public void StrictErrorsArePositioned()
    {
        var f = new List<string>();
        CheckError(f, "\"abc", Strict, 0, 1, 1, "not closed");
        CheckError(f, "a AND", Strict, 5, 1, 6, "Expected a word");
        CheckError(f, "(a b", Strict, 4, 1, 5, "Expected ')'");
        CheckError(f, "a b)", Strict, 3, 1, 4, "no '('");
        CheckError(f, "Colr:red", Strict, 0, 1, 1, "Did you mean 'Color'?", "Color");
        CheckError(f, "Text:ok AND Colr:red", Strict, 12, 1, 13, "The schema has no field 'Colr'", "Color");
        CheckError(f, "Count:[1 5]", Strict, 9, 1, 10, "Expected TO");
        CheckError(f, "a^x", Strict, 2, 1, 3, "boost");
        CheckError(f, "foo~3", Strict, 4, 1, 5, "at most 2");
        CheckError(f, "a*b", Strict, 1, 1, 2, "only allowed at the end");
        CheckError(f, "[1 TO 2]", Strict, 0, 1, 1, "needs a field");
        CheckError(f, "x:", Strict, 2, 1, 3, "Expected a value after 'x:'");
        CheckError(f, "a AND\n  )", Strict, 8, 2, 3, "Expected a word");
        CheckError(f, "a\r\nb OR OR", Strict, 8, 2, 6, "Expected a word");
        CheckError(f, "Count:abc", Strict, 0, 1, 1, "holds an integer");
        CheckError(f, "Flag:maybe", Strict, 0, 1, 1, "true or false");
        CheckError(f, "NOT", Strict, 3, 1, 4, "Expected a word");
        CheckError(f, "ab\\", Strict, 2, 1, 3, "backslash");
        CheckError(f, "", Strict, 0, 1, 1, "Expected a word");
        CheckError(f, "OR a", Strict, 0, 1, 1, "operator OR");
        CheckError(f, "Color:/a(/", Strict, 8, 1, 9, "Invalid regular expression");
        CheckError(f, "Text:\"a b\"~", Strict, 11, 1, 12, "slop");
        CheckError(f, "Color:\"a\"~2", Strict, 0, 1, 1, "keyword");
        CheckError(f, "Text:[a TO b]", Strict, 0, 1, 1, "numeric, date or keyword");
        CheckError(f, "Unfiltered:x", Strict, 0, 1, 1, "not searchable");
        CheckError(f, "Count:>", Strict, 7, 1, 8, "Expected a value");
        CheckError(f, "-", Strict, 1, 1, 2, "Expected a term after '-'");
        Assert.Empty(f);

        var deep = new string('(', StrictQuery.MaxDepth + 1) + "a" + new string(')', StrictQuery.MaxDepth + 1);
        QueryParseException e = Assert.Throws<QueryParseException>(() => StrictQuery.Parse(deep, Schema));
        Assert.Contains("nested deeper", e.Detail, StringComparison.Ordinal);
        e = Assert.Throws<QueryParseException>(() => StrictQuery.Parse(new string('a', StrictQuery.MaxLength + 1), Schema));
        Assert.Equal(StrictQuery.MaxLength, e.Offset);
    }

    [Fact]
    public void FilterErrorsArePositioned()
    {
        var f = new List<string>();
        CheckError(f, "Count >", Filter, 7, 1, 8, "Expected a value");
        CheckError(f, "Colr = red", Filter, 0, 1, 1, "Did you mean 'Color'?", "Color");
        CheckError(f, "Text = x", Filter, 0, 1, 1, "full-text");
        CheckError(f, "Unfiltered = x", Filter, 0, 1, 1, "not filterable");
        CheckError(f, "Count = abc", Filter, 8, 1, 9, "holds an integer");
        CheckError(f, "Count > 1 AND", Filter, 13, 1, 14, "Expected a field name");
        CheckError(f, "(Count > 1", Filter, 10, 1, 11, "to close the '(' at offset 0");
        CheckError(f, "Color = 'abc", Filter, 8, 1, 9, "not closed");
        CheckError(f, "Flag > true", Filter, 5, 1, 6, "boolean");
        CheckError(f, "Count 1 TO", Filter, 10, 1, 11, "Expected a value");
        CheckError(f, "Count 1 2", Filter, 8, 1, 9, "Expected TO");
        CheckError(f, "Color IN red", Filter, 9, 1, 10, "Expected '['");
        CheckError(f, "Color = a\nAND Labls = b", Filter, 14, 2, 5, "Did you mean 'Labels'?", "Labels");
        CheckError(f, "Count ! 3", Filter, 6, 1, 7, "Expected '=' after '!'");
        CheckError(f, "Color = 'a\\n'", Filter, 10, 1, 11, "Unknown escape");
        CheckError(f, "", Filter, 0, 1, 1, "Expected a condition");
        CheckError(f, "AND", Filter, 0, 1, 1, "Expected a field name");
        CheckError(f, "Count = 1 Count = 2", Filter, 10, 1, 11, "Expected AND, OR");
        CheckError(f, "Text EXISTS", Filter, 0, 1, 1, "full-text");
        CheckError(f, "When = yesterday", Filter, 7, 1, 8, "date");
        CheckError(f, "Count = 99999999999999999999", Filter, 8, 1, 9, "outside the range");
        CheckError(f, "Labels IS SOMETHING", Filter, 10, 1, 11, "Expected NULL or EMPTY");
        Assert.Empty(f);
        Assert.Throws<QueryParseException>(() => FilterString.Parse(new string(' ', FilterString.MaxLength + 1), Schema));
    }

    [Fact]
    public void KeywordsAreCaseInsensitiveAndQuotable()
    {
        Query a = FilterString.Parse("Count > 1 and not Flag = true or Color in [red, 'green']", Schema);
        Query b = FilterString.Parse("Count > 1 AND NOT Flag = true OR Color IN [red, \"green\"]", Schema);
        Assert.Equal(a, b);
        Assert.Equal(new TermQuery("Color", "AND"), FilterString.Parse("Color = \"AND\"", Schema));
        Assert.Equal(new TermQuery("Color", "AND"), FilterString.Parse("Color = AND", Schema));
        Assert.Equal(new TermQuery("Color", "a b"), FilterString.Parse("'Color' = 'a b'", Schema));
    }

    [Fact]
    public void FilterStringShapes()
    {
        Assert.Equal(new RangeQuery("Count", null, null), FilterString.Parse("Count EXISTS", Schema));
        Assert.Equal(FilterString.Parse("NOT Count EXISTS", Schema), FilterString.Parse("Count IS NULL", Schema));
        Assert.Equal(FilterString.Parse("Count EXISTS", Schema), FilterString.Parse("Count IS NOT EMPTY", Schema));
        Assert.Equal(FilterString.Parse("NOT Color IN [a, b]", Schema), FilterString.Parse("Color NOT IN [a, b]", Schema));
        Assert.Equal(FilterString.Parse("Count >= 3", Schema), FilterString.Parse("Count > 2.5", Schema));
        Assert.Equal(new RangeQuery("Count", FieldValue.FromInt64(3), FieldValue.FromInt64(7)), FilterString.Parse("Count 2.5 TO 7.5", Schema));
        Assert.Equal(new BooleanQuery([]), FilterString.Parse("Count = 2.5", Schema));
        Assert.Equal(new RangeQuery("When", FieldValue.FromDate(new DateTimeOffset(QueryGen.Epoch)), null) { IncludeLower = false }, FilterString.Parse("When > 2024-01-01", Schema));
    }

    // ---------------------------------------------------------------- round trips

    [Fact]
    public void StrictPrintParseIsIdentity()
    {
        var r = new Random(15001);
        for (int i = 0; i < 6000; i++)
        {
            StrictNode tree = QueryGen.Strict(r, 4);
            string text = StrictQuery.Print(tree);
            StrictNode back;
            try
            {
                back = StrictQuery.ParseSyntax(text);
            }
            catch (QueryParseException e)
            {
                throw new Xunit.Sdk.XunitException($"case {i}: '{Escape(text)}' did not parse: {e.Message}");
            }

            Assert.True(tree.Equals(back), $"case {i}: '{Escape(text)}' read back as '{Escape(StrictQuery.Print(back))}'");
            Assert.Equal(text, StrictQuery.Print(back));
        }
    }

    [Fact]
    public void FilterPrintParseIsIdentity()
    {
        var r = new Random(15002);
        for (int i = 0; i < 6000; i++)
        {
            Query q = QueryGen.Filter(r, 4);
            string text = FilterString.Print(q, Schema);
            Query back;
            try
            {
                back = FilterString.Parse(text, Schema);
            }
            catch (QueryParseException e)
            {
                throw new Xunit.Sdk.XunitException($"case {i}: '{Escape(text)}' did not parse: {e.Message}");
            }

            Assert.True(q.Equals(back), $"case {i}: '{Escape(text)}' read back differently");
            Assert.Equal(text, FilterString.Print(back, Schema));
        }
    }

    [Fact]
    public void JsonObjectToJsonToObjectIsIdentity()
    {
        var r = new Random(15003);
        for (int i = 0; i < 4000; i++)
        {
            QueryRequest req = QueryGen.Request(r);
            string json = QueryJson.Write(req);
            QueryRequest back;
            try
            {
                back = QueryJson.Read(json, Schema);
            }
            catch (QueryParseException e)
            {
                throw new Xunit.Sdk.XunitException($"case {i}: {e.Message}\n{json}");
            }

            Assert.True(req.Equals(back), $"case {i}: not equal\n{json}\n{QueryJson.Write(back)}");

            // The second direction: text to object to text is canonical, whatever order and spacing the input had.
            Assert.Equal(json, QueryJson.Write(back));
            Assert.Equal(json, QueryJson.Write(QueryJson.Read(Reformat(json), Schema)));
        }
    }

    // Same document, different property order and white space.
    private static string Reformat(string json)
    {
        JsonNode node = JsonNode.Parse(json)!;
        return Reverse(node).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode Reverse(JsonNode n)
    {
        switch (n)
        {
            case JsonObject o:
                var result = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> p in o.Reverse())
                {
                    result[p.Key] = p.Value is null ? null : Reverse(p.Value);
                }

                return result;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (JsonNode? item in a)
                {
                    arr.Add(item is null ? null : Reverse(item));
                }

                return arr;
            default:
                return n.DeepClone();
        }
    }

    [Fact]
    public void JsonSugarNodesCompileToQueryObjects()
    {
        QueryRequest r = QueryJson.Read("""
            {"v":1,"q":"doc","query":{"type":"strict","text":"Color:red"},
             "filter":"Count > 3","limit":5,"highlight":true,"countExactly":true}
            """, Schema);
        Assert.Equal(5, r.Limit);
        Assert.True(r.CountExactly);
        Assert.Equal(new Highlighting.HighlightOptions(), r.Highlight);
        Assert.Equal(FilterString.Parse("Count > 3", Schema), r.Filter);
        var must = Assert.IsType<BooleanQuery>(r.Query);
        Assert.Equal(2, must.Clauses.Count);
        Assert.Equal(new TermQuery("Color", "red"), must.Clauses[1].Query);

        // A filter given as an object is the same as the string.
        QueryRequest r2 = QueryJson.Read("""{"v":1,"filter":{"type":"filter","filter":"Count > 3"}}""", Schema);
        Assert.Equal(r.Filter, r2.Filter);
        Assert.Null(r2.Query);
    }

    [Fact]
    public void AnUnpairedSurrogateEscapeIsAParseErrorNotAnInvalidOperation()
    {
        // Found by the query JSON fuzz target (testdata/fuzz/queryjson-lone-surrogate.json): the parser accepts the escape, reading the string threw.
        string path = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "testdata", "fuzz", "queryjson-lone-surrogate.json");
        Assert.False(QueryJson.TryRead(File.ReadAllText(path), Schema, out _, out QueryParseException? error));
        Assert.Contains("surrogate", error!.Message);
    }

    [Fact]
    public void JsonSugarThatExpandsPastTheDepthLimitIsRejectedAtRead()
    {
        // Found by the query JSON fuzz target: a strict text of stacked NOTs is within the input depth but writes a much deeper tree.
        string path = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "testdata", "fuzz", "queryjson-strict-depth.json");
        byte[] bytes = File.ReadAllBytes(path);
        if (QueryJson.TryRead(Encoding.UTF8.GetString(bytes), Schema, out QueryRequest? request, out QueryParseException? error))
        {
            Assert.True(QueryJson.TryRead(QueryJson.Write(request), Schema, out QueryRequest? again, out _));
            Assert.True(request.Equals(again));
        }
        else
        {
            Assert.DoesNotContain("not valid JSON", error.Detail, StringComparison.Ordinal);
            Assert.Equal("$", error.Path);
        }
    }

    [Fact]
    public void ARankingRuleNamingAnUnsortableFieldIsAParseErrorWithItsPosition()
    {
        // Found by the query JSON fuzz target (testdata/fuzz/queryjson-ranking-rule-field.json): the rule was accepted when read and failed with an
        // ArgumentException when the search ran, although the schema is known at read time.
        string path = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "testdata", "fuzz", "queryjson-ranking-rule-field.json");
        string found = File.ReadAllText(path);
        Assert.Contains("\"rankingRules\":[\"Numbep:asc\"", found, StringComparison.Ordinal);
        Assert.False(QueryJson.TryRead("{\"v\":1,\"rankingRules\":[\"words\",\"Numbep:asc\"]}", Schema, out _, out QueryParseException? error));
        Assert.Equal("$.rankingRules[1]", error!.Path);
        Assert.Contains("not a sortable property", error.Detail, StringComparison.Ordinal);
        Assert.True(QueryJson.TryRead("{\"v\":1,\"rankingRules\":[\"words\",\"Count:asc\"]}", Schema, out _, out QueryParseException? ok), ok?.Message);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void JsonErrorsNameThePath()
    {
        var f = new List<string>();
        void Case(string json, string path, string contains, string? suggestion = null)
        {
            QueryParseException? e = QueryJson.TryRead(json, Schema, out _, out QueryParseException? err) ? null : err;
            if (e is null || e.Path != path || !e.Detail.Contains(contains, StringComparison.Ordinal) || e.Suggestion != suggestion)
            {
                f.Add($"{json}: got path {e?.Path} '{e?.Detail}' suggestion {e?.Suggestion}; want {path} containing '{contains}' suggestion {suggestion}");
            }
        }

        Case("{}", "$", "no \"v\"");
        Case("""{"v":2}""", "$.v", "not supported");
        Case("""{"v":1.5}""", "$.v", "whole number");
        Case("""{"v":1,"limt":3}""", "$.limt", "Did you mean \"limit\"?", "limit");
        Case("""{"v":1,"facets":["a"]}""", "$.facets", "reserved");
        Case("""{"v":1,"v":1}""", "$.v", "twice");
        Case("""{"v":1,"limit":-1}""", "$.limit", "from 0 to 10000");
        Case("""{"v":1,"limit":9000,"offset":2000}""", "$.limit", "largest window");
        Case("""{"v":1,"query":{"type":"term","field":"Colr","value":"x"}}""", "$.query.field", "Did you mean \"Color\"?", "Color");
        Case("""{"v":1,"query":{"type":"term","field":"Color","valu":"x"}}""", "$.query.valu", "Did you mean \"value\"?", "value");
        Case("""{"v":1,"query":{"type":"trem"}}""", "$.query.type", "Did you mean \"term\"?", "term");
        Case("""{"v":1,"query":{"field":"Color"}}""", "$.query", "needs a \"type\"");
        Case("""{"v":1,"query":{"type":"term","field":"Color","value":3}}""", "$.query.value", "must be a string");
        Case("""{"v":1,"query":{"type":"term","field":"Count","value":"3"}}""", "$.query.value", "not a string");
        
        Case("""{"v":1,"query":{"type":"bool","must":[{"type":"matchAll"},{"type":"range","field":"Count"}]}}""", "$.query.must[1]", "at least one of");
        Case("""{"v":1,"query":{"type":"bool","should":[{"type":"range","field":"Count","gt":1,"gte":2}]}}""", "$.query.should[0].gt", "not both");
        Case("""{"v":1,"query":{"type":"range","field":"When","gt":"yesterday"}}""", "$.query.gt", "date");
        Case("""{"v":1,"query":{"type":"range","field":"Flag","gt":true}}""", "$.query.field", "boolean");
        Case("""{"v":1,"query":{"type":"regex","field":"Color","pattern":"a("}}""", "$.query.pattern", "Invalid regular expression");
        Case("""{"v":1,"query":{"type":"matchAll","boost":-1}}""", "$.query.boost", "not negative");
        Case("""{"v":1,"filter":"Colr = x"}""", "$.filter", "Did you mean 'Color'?", "Color");
        Case("""{"v":1,"query":{"type":"strict","text":"(a"}}""", "$.query.text", "Expected ')'");
        Case("""{"v":1,"query":"x"}""", "$.query", "Expected a query object");
        Case("""{"v":1,"highlight":{"fragmentLength":0}}""", "$.highlight.fragmentLength", "from 1 to");
        Case("""{"v":1,"query":{"type":"phrase","field":"Text","terms":[]}}""", "$.query.terms", "1 to 256");
        Case("""{"v":1,"query":{"type":"exists","field":"Text"}}""", "$.query.field", "cannot use");
        Assert.Empty(f);

        // Not JSON at all: a line and column, no path.
        QueryParseException syntax = Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\n \"q\": }", Schema));
        Assert.Null(syntax.Path);
        Assert.Equal(2, syntax.Line);
        Assert.Contains("not valid JSON", syntax.Detail, StringComparison.Ordinal);

        // Nesting and size limits.
        string nested = "{\"type\":\"matchAll\"}";
        for (int i = 0; i < QueryJson.MaxNodeDepth + 1; i++)
        {
            nested = "{\"type\":\"bool\",\"must\":[" + nested + "]}";
        }

        Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\"query\":" + nested + "}", Schema));
        Assert.Throws<QueryParseException>(() => QueryJson.Read(new string(' ', QueryJson.MaxBytes + 1), Schema));
        Assert.Throws<QueryParseException>(() => QueryJson.Read("\"\ud800\"", Schema));
    }

    // ---------------------------------------------------------------- differential against a reference

    private sealed record Ref(string Text, Func<DiffDoc, bool> Eval, int Prec);

    private static string Wrap(Ref x, int p) => x.Prec < p ? "(" + x.Text + ")" : x.Text;

    // Filter keywords are case-insensitive; strict operators are upper case only (a lower-case "and" is a word).
    private static Ref And(Random r, Ref a, Ref b, bool lowerOk = true)
        => new(Wrap(a, 2) + " " + (lowerOk && r.Next(5) == 0 ? "and" : "AND") + " " + Wrap(b, 2), d => a.Eval(d) && b.Eval(d), 2);

    private static Ref Or(Random r, Ref a, Ref b, bool lowerOk = true)
        => new(Wrap(a, 1) + " " + (lowerOk && r.Next(5) == 0 ? "or" : "OR") + " " + Wrap(b, 1), d => a.Eval(d) || b.Eval(d), 1);

    private static Ref Not(Random r, Ref a)
        => new((r.Next(5) == 0 ? "not" : "NOT") + " " + Wrap(a, 3), d => !a.Eval(d), 3);

    private static string Lit(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static readonly string[] Ops = ["=", "!=", ">", ">=", "<", "<="];

    private static bool Compare(double? v, string op, double lit) => op switch
    {
        "=" => v == lit,
        "!=" => v != lit,
        ">" => v > lit,
        ">=" => v >= lit,
        "<" => v < lit,
        _ => v <= lit,
    };

    private static string Pad(Random r, string s) => r.Next(4) == 0 ? "  " + s + " " : s;

    private static Ref FilterAtom(Random r)
    {
        switch (r.Next(9))
        {
            case 0:
                {
                    string c = QueryGen.Colors[r.Next(5)];
                    bool eq = r.Next(2) == 0;
                    string q = r.Next(3) == 0 ? "'" + c + "'" : c;
                    return new Ref(Pad(r, "Color " + (eq ? "=" : "!=") + " " + q), d => (d.Color == c) == eq, 3);
                }

            case 1:
                {
                    string[] set = Enumerable.Range(0, r.Next(1, 4)).Select(_ => QueryGen.Colors[r.Next(5)]).ToArray();
                    bool not = r.Next(3) == 0;
                    string text = "Color " + (not ? "NOT " : string.Empty) + "IN [" + string.Join(r.Next(2) == 0 ? ", " : ",", set) + "]";
                    return new Ref(text, d => set.Contains(d.Color) != not, 3);
                }

            case 2:
                {
                    string w = QueryGen.LabelWords[r.Next(6)];
                    int form = r.Next(5);
                    return form switch
                    {
                        0 => new Ref("Labels = " + w, d => d.Labels.Contains(w), 3),
                        1 => new Ref("Labels != " + w, d => !d.Labels.Contains(w), 3),
                        2 => new Ref("Labels EXISTS", d => d.Labels.Length > 0, 3),
                        3 => new Ref("Labels IS EMPTY", d => d.Labels.Length == 0, 3),
                        _ => new Ref("Labels IN [" + w + ", " + QueryGen.LabelWords[0] + "]", d => d.Labels.Contains(w) || d.Labels.Contains(QueryGen.LabelWords[0]), 3),
                    };
                }

            case 3:
                {
                    // Integers, compared against whole and fractional literals.
                    string op = Ops[r.Next(6)];
                    double lit = r.Next(3) == 0 ? (r.Next(-6, 24) / 2.0) : r.Next(-4, 13);
                    return new Ref(Pad(r, "Count " + op + " " + Lit(lit)), d => Compare(d.Count, op, lit), 3);
                }

            case 4:
                {
                    double a = r.Next(-4, 12) / (r.Next(2) == 0 ? 1.0 : 2.0);
                    double b = a + r.Next(0, 8);
                    return new Ref("Count " + Lit(a) + " TO " + Lit(b), d => d.Count >= a && d.Count <= b, 3);
                }

            case 5:
                {
                    string op = Ops[r.Next(6)];
                    double lit = r.Next(-6, 10) * 0.5;
                    int form = r.Next(5);
                    return form switch
                    {
                        0 => new Ref("Rate " + op + " " + Lit(lit), d => Compare(d.Rate, op, lit), 3),
                        1 => new Ref("Maybe " + op + " " + Lit(Math.Round(lit)), d => Compare(d.Maybe, op, Math.Round(lit)), 3),
                        2 => new Ref("Maybe EXISTS", d => d.Maybe is not null, 3),
                        3 => new Ref("Maybe IS NULL", d => d.Maybe is null, 3),
                        _ => BigAtom(r, op),
                    };
                }

            case 6:
                {
                    string op = Ops[r.Next(6)];
                    var when = new DateTimeOffset(QueryGen.Epoch.AddDays(r.Next(-1, 11)).AddMilliseconds(r.Next(0, 3) * 250));
                    string text = when.ToString(when.Millisecond == 0 && when.Hour == 0 ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                    string quoted = r.Next(2) == 0 ? "\"" + text + "\"" : text;
                    double ms = when.ToUnixTimeMilliseconds();
                    return new Ref("When " + op + " " + quoted, d => Compare(new DateTimeOffset(d.When, TimeSpan.Zero).ToUnixTimeMilliseconds(), op, ms), 3);
                }

            case 7:
                {
                    var due = new DateTimeOffset(QueryGen.Epoch.AddHours(r.Next(0, 7) * 12));
                    string op = Ops[r.Next(6)];
                    return new Ref("Due " + op + " " + due.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), d => Compare(d.Due.ToUnixTimeMilliseconds(), op, due.ToUnixTimeMilliseconds()), 3);
                }

            default:
                {
                    bool v = r.Next(2) == 0;
                    bool eq = r.Next(2) == 0;
                    return new Ref("Flag " + (eq ? "=" : "!=") + " " + (v ? "true" : "FALSE").ToLowerInvariant(), d => (d.Flag == v) == eq, 3);
                }
        }
    }

    private static Ref BigAtom(Random r, string op)
    {
        long lit = r.Next(-4, 5) * 1_000_000_000_000L + (r.Next(4) == 0 ? 1 : 0);
        return new Ref("Big " + op + " " + lit.ToString(CultureInfo.InvariantCulture), d => Compare(d.Big, op, lit), 3);
    }

    private static Ref FilterTree(Random r, int depth)
    {
        if (depth <= 0 || r.Next(4) == 0)
        {
            return FilterAtom(r);
        }

        return r.Next(5) switch
        {
            0 => Not(r, FilterTree(r, depth - 1)),
            1 or 2 => And(r, FilterTree(r, depth - 1), FilterTree(r, depth - 1)),
            _ => Or(r, FilterTree(r, depth - 1), FilterTree(r, depth - 1)),
        };
    }

    private static string Ids(IEnumerable<SearchHit<DiffDoc>> hits) => string.Join(",", hits.Select(h => h.Id).OrderBy(x => x, StringComparer.Ordinal));

    private static string Expected(List<DiffDoc> docs, Func<DiffDoc, bool> eval)
        => string.Join(",", docs.Where(eval).Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal));

    private static string JsonRequest(string field, string value)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString(field, value);
            w.WriteNumber("limit", 10000);
            w.WriteBoolean("countExactly", true);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void FilterStringsMatchTheReference()
    {
        var r = new Random(15004);
        List<DiffDoc> docs = QueryGen.Docs(r, 300);
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(docs);
        index.Commit();
        int nonEmpty = 0;
        for (int i = 0; i < 2500; i++)
        {
            Ref tree = FilterTree(r, 3);
            string expected = Expected(docs, tree.Eval);
            string actual = Ids(index.Parse(JsonRequest("filter", tree.Text)).Execute());
            Assert.True(expected == actual, $"case {i}: {tree.Text}\nexpected {expected}\nactual   {actual}");
            if (expected.Length > 0 && expected.Length < docs.Count * 5)
            {
                nonEmpty++;
            }
        }

        Assert.True(nonEmpty > 1500, $"only {nonEmpty} filters selected something");
    }

    private static readonly string[] Pieces = ["alpha", "doc", "beta"];

    private static Ref StrictAtom(Random r)
    {
        switch (r.Next(12))
        {
            case 0:
                {
                    string c = QueryGen.Colors[r.Next(5)];
                    return new Ref("Color:" + c, d => d.Color == c, 3);
                }

            case 1:
                {
                    string w = QueryGen.LabelWords[r.Next(6)];
                    return new Ref("Labels:" + w, d => d.Labels.Contains(w), 3);
                }

            case 2:
                {
                    int lo = r.Next(-3, 8);
                    int hi = lo + r.Next(0, 6);
                    bool il = r.Next(2) == 0;
                    bool iu = r.Next(2) == 0;
                    return new Ref($"Count:{(il ? '[' : '{')}{lo} TO {hi}{(iu ? ']' : '}')}", d => (il ? d.Count >= lo : d.Count > lo) && (iu ? d.Count <= hi : d.Count < hi), 3);
                }

            case 3:
                {
                    string op = new[] { ">", ">=", "<", "<=" }[r.Next(4)];
                    double lit = r.Next(-6, 24) / 2.0;
                    return new Ref("Count:" + op + Lit(lit), d => Compare(d.Count, op, lit), 3);
                }

            case 4:
                {
                    double lo = r.Next(-4, 4) * 0.5;
                    return new Ref($"Rate:[{Lit(lo)} TO *}}", d => d.Rate >= lo, 3);
                }

            case 5:
                {
                    var from = new DateTimeOffset(QueryGen.Epoch.AddDays(r.Next(0, 9)));
                    return new Ref("When:>=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => new DateTimeOffset(d.When, TimeSpan.Zero) >= from, 3);
                }

            case 6:
                return r.Next(2) == 0 ? new Ref("Maybe:*", d => d.Maybe is not null, 3) : new Ref("Flag:true", d => d.Flag, 3);
            case 7:
                {
                    string[] patterns = ["(red|blue)", "gr.*n", "[a-b].*", "v.*t"];
                    string p = patterns[r.Next(patterns.Length)];
                    var rx = new Regex("^(?:" + p + ")$", RegexOptions.CultureInvariant);
                    return new Ref("Color:/" + p + "/", d => rx.IsMatch(d.Color), 3);
                }

            case 8:
                {
                    string c = QueryGen.Colors[r.Next(5)];
                    string prefix = c[..r.Next(1, 3)];
                    return new Ref("Color:" + prefix + "*", d => d.Color.StartsWith(prefix, StringComparison.Ordinal), 3);
                }

            case 9:
                {
                    int n = r.Next(0, 40);
                    string form = r.Next(3) == 0 ? "" : "Text:";
                    return new Ref(form + n.ToString(CultureInfo.InvariantCulture), d => Tokens(d).Contains(n.ToString(CultureInfo.InvariantCulture)), 3);
                }

            case 10:
                {
                    string p = r.Next(0, 4).ToString(CultureInfo.InvariantCulture);
                    return r.Next(2) == 0
                        ? new Ref("Text:" + p + "*", d => Tokens(d).Any(t => t.StartsWith(p, StringComparison.Ordinal)), 3)
                        : new Ref("Text:\"doc " + p + "\"", d => Tokens(d).Count >= 2 && Tokens(d)[1] == p, 3);
                }

            default:
                {
                    string c = QueryGen.Colors[r.Next(5)];
                    string typo = c.Length > 3 ? c[..2] + c[3] + c[2] + c[4..] : c;
                    string typed = c[..^1] + (c[^1] == 'x' ? 'y' : 'x');
                    string word = r.Next(2) == 0 ? typo : typed;
                    return new Ref("Color:" + word + "~1", d => EditDistance.OptimalStringAlignment(word, d.Color, 1) <= 1, 3);
                }
        }
    }

    private static List<string> Tokens(DiffDoc d) => d.Text.ToLowerInvariant().Split(' ').ToList();

    private static Ref StrictTree(Random r, int depth)
    {
        if (depth <= 0 || r.Next(4) == 0)
        {
            return StrictAtom(r);
        }

        switch (r.Next(6))
        {
            case 0:
                {
                    Ref a = StrictTree(r, depth - 1);
                    return new Ref("NOT " + Wrap(a, 3), d => !a.Eval(d), 3);
                }

            case 1:
                {
                    Ref a = StrictTree(r, depth - 1);
                    return new Ref("-" + Wrap(a, 3), d => !a.Eval(d), 3);
                }

            case 2:
                {
                    // Adjacency means AND.
                    Ref a = StrictTree(r, depth - 1);
                    Ref b = StrictTree(r, depth - 1);
                    return new Ref(Wrap(a, 2) + " " + Wrap(b, 2), d => a.Eval(d) && b.Eval(d), 2);
                }

            case 3:
                return And(r, StrictTree(r, depth - 1), StrictTree(r, depth - 1), lowerOk: false);
            case 4:
                return Or(r, StrictTree(r, depth - 1), StrictTree(r, depth - 1), lowerOk: false);
            default:
                {
                    Ref a = StrictTree(r, depth - 1);
                    return new Ref("(" + a.Text + ")^" + (r.Next(1, 4)).ToString(CultureInfo.InvariantCulture), a.Eval, 3);
                }
        }
    }

    [Fact]
    public void StrictQueriesMatchTheReference()
    {
        var r = new Random(15005);
        List<DiffDoc> docs = QueryGen.Docs(r, 300);
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(docs);
        index.Commit();
        int selective = 0;
        for (int i = 0; i < 2500; i++)
        {
            Ref tree = StrictTree(r, 3);
            string expected = Expected(docs, tree.Eval);
            string actual = Ids(index.SearchStrict(tree.Text).Take(1000).Execute());
            Assert.True(expected == actual, $"case {i}: {tree.Text}\nexpected {expected}\nactual   {actual}");
            if (expected.Length > 0 && expected.Length < docs.Count * 5)
            {
                selective++;
            }
        }

        Assert.True(selective > 1500, $"only {selective} queries selected something");
    }

    [Fact]
    public void StrictQuerySemantics()
    {
        // Operators bind NOT > AND > OR; adjacency is AND; a stop word is dropped, as in user text.
        StrictNode t = StrictQuery.ParseSyntax("a b OR c NOT d");
        Assert.Equal("a AND b OR c AND NOT d", Unspace(StrictQuery.Print(t)));
        Query q = StrictQuery.Parse("Text:doc AND NOT Color:red", Schema);
        var b = Assert.IsType<BooleanQuery>(q);
        Assert.Equal(Occur.Must, b.Clauses[0].Occur);
        Assert.Equal(Occur.MustNot, b.Clauses[1].Occur);
        Assert.Equal(2f, StrictQuery.Parse("Color:red^2", Schema).Boost);
        Assert.Equal(6f, StrictQuery.Parse("(Color:red^2)^3", Schema).Boost);
        Assert.Equal(new TermQuery("Color", "red") { Boost = 0.5f }, StrictQuery.Parse("Color:(red)^.5", Schema));

        // A field before a group applies to the unqualified parts inside it.
        Assert.Equal(StrictQuery.Parse("Color:red OR Color:blue", Schema), StrictQuery.Parse("Color:(red OR blue)", Schema));
        Assert.Equal(StrictQuery.Parse("Color:red OR Labels:blue", Schema), StrictQuery.Parse("Color:(red OR Labels:blue)", Schema));

        // Backslash makes operators and specials ordinary.
        Assert.Equal(new TermQuery("Color", "AND"), StrictQuery.Parse("Color:\\AND", Schema));
        Assert.Equal(new TermQuery("Color", "a b"), StrictQuery.Parse("Color:\"a b\"", Schema));
        Assert.Equal(new MatchAllQuery(), StrictQuery.Parse("*", Schema));
        Assert.Equal(new RangeQuery("Count", null, null), StrictQuery.Parse("Count:*", Schema));
        Assert.Equal(new PrefixQuery("Color", "re"), StrictQuery.Parse("Color:re*", Schema));
        Assert.Equal(new FuzzyQuery("Color", "red") { MaxEdits = 2 }, StrictQuery.Parse("Color:red~", Schema));

        // Words that analysis removes leave nothing: no document matches.
        Assert.Equal(new BooleanQuery([]), StrictQuery.Parse("?!", Schema));
        Assert.Equal(StrictQuery.Parse("Color:red", Schema), StrictQuery.Parse("Color:red AND ?!", Schema));
    }

    private static string Unspace(string s) => s.Replace("(", string.Empty, StringComparison.Ordinal).Replace(")", string.Empty, StringComparison.Ordinal);

    // ---------------------------------------------------------------- the index entry points

    [Fact]
    public void IndexParseRunsTheRequest()
    {
        var r = new Random(15006);
        List<DiffDoc> docs = QueryGen.Docs(r, 120);
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(docs);
        index.Commit();

        SearchResults<DiffDoc> page = index.Parse("""{"v":1,"q":"doc","filter":"Color = red","limit":3,"offset":1,"countExactly":true}""").Execute();
        int reds = docs.Count(d => d.Color == "red");
        Assert.Equal(reds, page.TotalHits);
        Assert.Equal(Math.Min(3, reds - 1), page.Count);
        Assert.All(page, h => Assert.Equal("red", h.Document.Color));

        Assert.False(index.TryParse("""{"v":1,"filter":"Nope = 1"}""", out _, out QueryParseException? error));
        Assert.Equal("$.filter", error!.Path);
        Assert.True(index.TryParse("""{"v":1}""", out SearchQuery<DiffDoc>? all, out _));
        Assert.Equal(20, all!.Execute().Count);
        Assert.Throws<QueryParseException>(() => index.SearchStrict("Color:"));
    }

    // ---------------------------------------------------------------- SearchTool

    private static void Objects(JsonNode n, List<JsonObject> into)
    {
        switch (n)
        {
            case JsonObject o:
                into.Add(o);
                foreach (KeyValuePair<string, JsonNode?> p in o)
                {
                    if (p.Value is not null)
                    {
                        Objects(p.Value, into);
                    }
                }

                break;
            case JsonArray a:
                foreach (JsonNode? item in a)
                {
                    if (item is not null)
                    {
                        Objects(item, into);
                    }
                }

                break;
        }
    }

    // Each mutation makes the document invalid for any reader that knows the schema, so the verdicts must agree.
    private static IEnumerable<JsonNode> Mutations(JsonNode valid, Random r)
    {
        for (int kind = 0; kind < 6; kind++)
        {
            JsonNode copy = valid.DeepClone();
            var objects = new List<JsonObject>();
            Objects(copy, objects);
            JsonObject root = copy.AsObject();
            JsonObject target = objects[r.Next(objects.Count)];
            switch (kind)
            {
                case 0:
                    target["unknownProperty"] = 1;
                    break;
                case 1:
                    root["v"] = 2;
                    break;
                case 2:
                    {
                        JsonObject typed = objects.Where(o => o != root && o.ContainsKey("type")).DefaultIfEmpty(root).ElementAt(0);
                        if (typed == root)
                        {
                            root["unknownProperty"] = true;
                        }
                        else
                        {
                            typed["type"] = "nonsense";
                        }

                        break;
                    }

                case 3:
                    {
                        JsonObject withField = objects.FirstOrDefault(o => o.ContainsKey("field")) ?? root;
                        if (withField == root)
                        {
                            root["limit"] = 10001;
                        }
                        else
                        {
                            withField["field"] = "NoSuchField";
                        }

                        break;
                    }

                case 4:
                    target["boost"] = -1;
                    break;
                default:
                    root.Remove("v");
                    break;
            }

            yield return copy;
        }
    }

    [Fact]
    public void SearchToolSchemaDescribesTheFields()
    {
        string tool = SearchTool.For<DiffDoc>();
        using JsonDocument doc = JsonDocument.Parse(tool);
        Assert.Equal("search_DiffDoc", doc.RootElement.GetProperty("name").GetString());
        JsonElement schema = doc.RootElement.GetProperty("input_schema");
        Assert.Equal(JsonValueKind.Object, schema.GetProperty("properties").ValueKind);
        string text = schema.GetRawText();
        Assert.Contains("\"Labels\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Unfiltered\"", text, StringComparison.Ordinal);
        Assert.Equal(SearchTool.SchemaFor<DiffDoc>(), text);
        Assert.Equal(tool, SearchTool.For(Schema, "DiffDoc"));
    }

    /// <summary>
    /// With ONEINDEX_JSONSCHEMA_PYTHON pointing at a Python that has the <c>jsonschema</c> wheel, the generated schema is checked by that
    /// validator against valid and mutated documents; without it, only our own reader gives verdicts.
    /// </summary>
    [Fact]
    public void SearchToolSchemaAgreesWithParse()
    {
        var r = new Random(15007);
        string schemaText = SearchTool.SchemaFor<DiffDoc>();
        List<DiffDoc> docs = QueryGen.Docs(r, 50);
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(docs);
        index.Commit();

        var documents = new List<(string Json, bool Valid)>();
        for (int i = 0; i < 160; i++)
        {
            string json = QueryJson.Write(QueryGen.Request(r));
            documents.Add((json, true));
            foreach (JsonNode m in Mutations(JsonNode.Parse(json)!, r))
            {
                documents.Add((m.ToJsonString(), false));
            }
        }

        // The canonical writer never produces a document that Parse refuses; every mutation is refused.
        foreach ((string json, bool valid) in documents)
        {
            Assert.Equal(valid, index.TryParse(json, out _, out QueryParseException? err));
            Assert.True(valid || err is not null);
        }

        string? python = Environment.GetEnvironmentVariable("ONEINDEX_JSONSCHEMA_PYTHON");
        if (string.IsNullOrEmpty(python))
        {
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "oneindex-jsonschema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "schema.json"), schemaText);
            File.WriteAllLines(Path.Combine(dir, "docs.jsonl"), documents.Select(d => d.Json));
            string script = Path.Combine(AppContext.BaseDirectory, "jsonschema_check.py");
            var start = new System.Diagnostics.ProcessStartInfo(python, $"\"{script}\" \"{dir}/schema.json\" \"{dir}/docs.jsonl\"") { RedirectStandardOutput = true, RedirectStandardError = true };
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd();
            string errors = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, errors);
            string[] verdicts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(documents.Count, verdicts.Length);
            int mismatches = 0;
            for (int i = 0; i < verdicts.Length; i++)
            {
                bool schemaSays = verdicts[i].StartsWith("valid", StringComparison.Ordinal);
                if (schemaSays != documents[i].Valid)
                {
                    mismatches++;
                    Assert.Fail($"validator says {verdicts[i]} but Parse says {documents[i].Valid}: {documents[i].Json}");
                }
            }

            Assert.Equal(0, mismatches);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
