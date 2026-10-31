using System.Globalization;
using System.Text;
using System.Text.Json;
using OneIndex.Highlighting;
using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex.Tests;

/// <summary>Seeded generators shared by the tests of the strict syntax, filter strings and query JSON.</summary>
internal static class QueryGen
{
    internal static readonly Schema Schema = SearchMappings.Require<DiffDoc>().Schema;

    internal static readonly string[] KeywordFields = ["Id", "Color", "Labels"];
    internal static readonly string[] Colors = ["red", "green", "blue", "amber", "violet"];
    internal static readonly string[] LabelWords = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta"];
    internal static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly float[] Boosts = [0.5f, 2f, 1.25f, 3f, 0f, 7.5f];
    private static readonly string[] Operators = ["AND", "OR", "NOT", "TO", "*"];
    private const string WordAlphabet = "abcXYZ019 \\()\"^~:[]{}*/-<>!ANDOTé😀\n\t,.=";

    internal static string Word(Random r, int max = 6, string alphabet = WordAlphabet)
    {
        int n = r.Next(1, max + 1);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            int at = r.Next(alphabet.Length);
            if (char.IsHighSurrogate(alphabet[at]) && at + 1 < alphabet.Length)
            {
                sb.Append(alphabet[at]).Append(alphabet[at + 1]);
            }
            else if (!char.IsLowSurrogate(alphabet[at]))
            {
                sb.Append(alphabet[at]);
            }
        }

        if (sb.Length == 0)
        {
            sb.Append('a');
        }

        // Operator words are the interesting collisions.
        return r.Next(15) == 0 ? Operators[r.Next(Operators.Length)] : sb.ToString();
    }

    private static float Boost(Random r) => r.Next(4) == 0 ? Boosts[r.Next(Boosts.Length)] : 1f;

    private static readonly string[] AllFields = ["Id", "Color", "Labels", "Count", "Big", "Rate", "When", "Flag", "Maybe", "Due", "Text", "Nope"];

    internal static StrictNode Strict(Random r, int depth)
    {
        int k = depth <= 0 ? r.Next(7) : r.Next(10);
        string? field = r.Next(3) == 0 ? null : (r.Next(6) == 0 ? Word(r, 4) : AllFields[r.Next(AllFields.Length)]);
        float boost = Boost(r);
        switch (k)
        {
            case 0:
                return new StrictNode { Kind = StrictNodeKind.MatchAll, Boost = boost };
            case 1:
                return new StrictNode { Kind = StrictNodeKind.Term, Field = field, Text = Word(r), Boost = boost };
            case 2:
                return new StrictNode { Kind = StrictNodeKind.Phrase, Field = field, Text = r.Next(8) == 0 ? string.Empty : Word(r, 10), Number = r.Next(3) == 0 ? r.Next(1, 6) : 0, Boost = boost };
            case 3:
                return new StrictNode { Kind = StrictNodeKind.Prefix, Field = field, Text = Word(r), Boost = boost };
            case 4:
                return new StrictNode { Kind = StrictNodeKind.Fuzzy, Field = field, Text = Word(r), Number = r.Next(3), Boost = boost };
            case 5:
                {
                    string[] pieces = ["a", "b", ".", "*", "(", ")", "|", "\\d", "\\w", "[a-z]", "/", "x", "+", "é"];
                    var sb = new StringBuilder();
                    for (int i = r.Next(1, 6); i > 0; i--)
                    {
                        sb.Append(pieces[r.Next(pieces.Length)]);
                    }

                    return new StrictNode { Kind = StrictNodeKind.Regex, Field = field, Text = sb.ToString(), Boost = boost };
                }

            case 6:
                {
                    string? lower = r.Next(4) == 0 ? null : (r.Next(3) == 0 ? Word(r, 4) : r.Next(100).ToString(CultureInfo.InvariantCulture));
                    string? upper = r.Next(4) == 0 ? null : (r.Next(3) == 0 ? Word(r, 4) : r.Next(100).ToString(CultureInfo.InvariantCulture));
                    return new StrictNode
                    {
                        Kind = StrictNodeKind.Range,
                        Field = field ?? "Count",
                        Lower = lower,
                        Upper = upper,
                        IncludeLower = lower is null || r.Next(2) == 0,
                        IncludeUpper = upper is null || r.Next(2) == 0,
                        Boost = boost,
                    };
                }

            case 9:
                return new StrictNode { Kind = StrictNodeKind.Not, Children = [Strict(r, depth - 1)], Boost = boost };
            default:
                {
                    var kids = new List<StrictNode>();
                    for (int i = r.Next(2, 5); i > 0; i--)
                    {
                        kids.Add(Strict(r, depth - 1));
                    }

                    return new StrictNode { Kind = k == 7 ? StrictNodeKind.And : StrictNodeKind.Or, Children = kids, Boost = boost };
                }
        }
    }

    internal static FieldValue Value(Random r, string field)
    {
        FieldDefinition def = Schema.Fields.First(f => f.Name == field);
        switch (def.Type)
        {
            case FieldType.Integral:
                return FieldValue.FromInt64(field == "Big" && r.Next(3) == 0 ? r.NextInt64(long.MinValue / 2, long.MaxValue / 2) : r.NextInt64(-50, 50));
            case FieldType.Fractional:
                return FieldValue.FromFloat64(r.Next(3) == 0 ? (r.NextDouble() * 1000) - 500 : r.Next(-20, 20) * 0.25);
            case FieldType.Date:
                return FieldValue.FromDate(new DateTimeOffset(Epoch.AddDays(r.Next(-3, 12)).AddMilliseconds(r.Next(0, 4) * 250)));
            case FieldType.Bool:
                return FieldValue.FromBool(r.Next(2) == 0);
            default:
                return FieldValue.FromText(r.Next(3) == 0 ? Word(r, 6) : Colors[r.Next(Colors.Length)]);
        }
    }

    private static readonly string[] Patterns = ["a.*", "[a-z]+", "(ab|cd)x?", "x{2,3}"];
    private static readonly Occur[] CanonicalOrder = [Occur.Must, Occur.Filter, Occur.Should, Occur.MustNot];

    private static readonly string[] OrderedFields = ["Count", "Big", "Rate", "When", "Due", "Maybe"];
    private static readonly string[] AnyFields = ["Count", "Big", "Rate", "When", "Due", "Maybe", "Flag", "Color", "Labels", "Id"];

    /// <summary>A query of the shapes the filter-string parser produces.</summary>
    internal static Query Filter(Random r, int depth)
    {
        int k = depth <= 0 ? r.Next(6) : r.Next(9);
        switch (k)
        {
            case 0:
            case 1:
                {
                    string f = KeywordFields[r.Next(KeywordFields.Length)];
                    return new TermQuery(f, Value(r, "Color").AsText());
                }

            case 2:
                {
                    string f = AnyFields[r.Next(7)];
                    FieldValue v = Value(r, f);
                    return new RangeQuery(f, v, v);
                }

            case 3:
                {
                    string f = OrderedFields[r.Next(OrderedFields.Length)];
                    FieldValue v = Value(r, f);
                    return r.Next(2) == 0 ? new RangeQuery(f, v, null) { IncludeLower = r.Next(2) == 0 } : new RangeQuery(f, null, v) { IncludeUpper = r.Next(2) == 0 };
                }

            case 4:
                {
                    string f = r.Next(4) == 0 ? "Color" : OrderedFields[r.Next(OrderedFields.Length)];
                    return new RangeQuery(f, Value(r, f), Value(r, f));
                }

            case 5:
                return new RangeQuery(AnyFields[r.Next(AnyFields.Length)], null, null);
            case 6:
                return new BooleanQuery([new BooleanClause(new MatchAllQuery(), Occur.Filter), new BooleanClause(Filter(r, depth - 1), Occur.MustNot)]);
            default:
                {
                    var kids = new List<BooleanClause>();
                    bool and = k == 7;
                    for (int i = r.Next(2, 5); i > 0; i--)
                    {
                        kids.Add(new BooleanClause(Filter(r, depth - 1), and ? Occur.Filter : Occur.Should));
                    }

                    return and ? new BooleanQuery(kids) : new BooleanQuery(kids) { MinimumShouldMatch = 1 };
                }
        }
    }

    private static T WithBoost<T>(T q, Random r)
        where T : Query
        => (T)(q with { Boost = Boost(r) });

    /// <summary>Every kind of query the JSON format carries, in canonical clause order.</summary>
    internal static Query Json(Random r, int depth)
    {
        int k = depth <= 0 ? r.Next(11) : r.Next(12);
        string[] textish = ["Text", "Color", "Labels", "Id"];
        string tf = textish[r.Next(textish.Length)];
        Query q;
        switch (k)
        {
            case 0:
                q = new MatchAllQuery();
                break;
            case 1:
                q = new TermQuery(KeywordFields[r.Next(KeywordFields.Length)], Value(r, "Color").AsText());
                break;
            case 2:
                {
                    string f = AnyFields[r.Next(7)];
                    FieldValue v = Value(r, f);
                    q = new RangeQuery(f, v, v);
                    break;
                }

            case 3:
                {
                    string f = OrderedFields[r.Next(OrderedFields.Length)];
                    FieldValue v = Value(r, f);
                    q = r.Next(2) == 0 ? new RangeQuery(f, v, null) { IncludeLower = r.Next(2) == 0 } : new RangeQuery(f, null, v) { IncludeUpper = r.Next(2) == 0 };
                    break;
                }

            case 4:
                {
                    string f = r.Next(4) == 0 ? "Color" : OrderedFields[r.Next(OrderedFields.Length)];
                    q = new RangeQuery(f, Value(r, f), Value(r, f)) { IncludeLower = r.Next(2) == 0, IncludeUpper = r.Next(2) == 0 };
                    break;
                }

            case 5:
                q = new RangeQuery(AnyFields[r.Next(AnyFields.Length)], null, null);
                break;
            case 6:
                {
                    var terms = new List<string>();
                    for (int i = r.Next(1, 4); i > 0; i--)
                    {
                        terms.Add(Word(r, 6));
                    }

                    q = new PhraseQuery(tf, terms) { Slop = r.Next(3) == 0 ? r.Next(1, 20) : 0 };
                    break;
                }

            case 7:
                q = r.Next(2) == 0
                    ? new PrefixQuery(tf, Word(r))
                    : new FuzzyQuery(tf, Word(r)) { MaxEdits = r.Next(3), PrefixLength = r.Next(3) == 0 ? r.Next(1, 4) : 0, Transpositions = r.Next(2) == 0 };
                break;
            case 8:
                q = r.Next(2) == 0 ? new RegexQuery(tf, Patterns[r.Next(Patterns.Length)]) : new MultiFieldTermQuery(["Text", "Color"], Word(r));
                break;
            case 9:
            case 10:
                q = r.Next(2) == 0 ? new TermQuery("Color", Word(r)) : new MatchAllQuery();
                break;
            default:
                {
                    var clauses = new List<BooleanClause>();
                    foreach (Occur occur in CanonicalOrder)
                    {
                        for (int i = r.Next(3); i > 0; i--)
                        {
                            clauses.Add(new BooleanClause(Json(r, depth - 1), occur));
                        }
                    }

                    q = new BooleanQuery(clauses) { MinimumShouldMatch = r.Next(4) == 0 ? r.Next(1, 3) : 0 };
                    break;
                }
        }

        return WithBoost(q, r);
    }

    internal static QueryRequest Request(Random r)
        => new()
        {
            Query = r.Next(5) == 0 ? null : Json(r, 3),
            Filter = r.Next(2) == 0 ? null : Filter(r, 2),
            Limit = r.Next(3) == 0 ? r.Next(0, 5000) : 20,
            Offset = r.Next(3) == 0 ? r.Next(0, 5000) : 0,
            CountExactly = r.Next(3) == 0,
            Highlight = r.Next(3) == 0 ? new HighlightOptions { FragmentLength = r.Next(1, 500), MaxFragments = r.Next(1, 10) } : null,
        };

    internal static List<DiffDoc> Docs(Random r, int count)
    {
        var docs = new List<DiffDoc>(count);
        for (int i = 0; i < count; i++)
        {
            docs.Add(new DiffDoc
            {
                Id = "d" + i.ToString(CultureInfo.InvariantCulture),
                Color = Colors[r.Next(Colors.Length)],
                Labels = LabelWords.Where(_ => r.Next(4) == 0).ToArray(),
                Count = r.Next(-3, 12),
                Big = r.NextInt64(-5, 5) * 1_000_000_000_000L,
                Rate = r.Next(-4, 8) * 0.5,
                When = Epoch.AddDays(r.Next(0, 10)).AddMilliseconds(r.Next(0, 3) * 250),
                Flag = r.Next(2) == 0,
                Maybe = r.Next(3) == 0 ? null : r.Next(0, 6),
                Due = new DateTimeOffset(Epoch.AddHours(r.Next(0, 6) * 12)),
                Text = "doc " + i.ToString(CultureInfo.InvariantCulture) + (r.Next(2) == 0 ? " " + LabelWords[r.Next(LabelWords.Length)] : string.Empty),
                Unfiltered = "u",
            });
        }

        return docs;
    }
}
