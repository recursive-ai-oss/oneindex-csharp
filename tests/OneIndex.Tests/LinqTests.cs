using System.Linq.Expressions;
using OneIndex.Linq;

namespace OneIndex.Tests;

public class DiffDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Keyword]
    public string Color { get; set; } = string.Empty;

    [Keyword]
    public string[] Labels { get; set; } = [];

    [Filterable]
    public int Count { get; set; }

    [Filterable]
    public long Big { get; set; }

    [Filterable]
    public double Rate { get; set; }

    [Filterable]
    public DateTime When { get; set; }

    [Filterable]
    public bool Flag { get; set; }

    [Filterable]
    public int? Maybe { get; set; }

    [Filterable]
    public DateTimeOffset Due { get; set; }

    [FullText]
    public string Text { get; set; } = string.Empty;

    public string Unfiltered { get; set; } = string.Empty;
}

public class LinqTests
{
    private static readonly string[] Colors = ["red", "green", "blue", "amber", "violet"];
    private static readonly string[] Labels = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta"];
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<DiffDoc> MakeDocs(Random random, int count)
    {
        var docs = new List<DiffDoc>(count);
        for (int i = 0; i < count; i++)
        {
            docs.Add(new DiffDoc
            {
                Id = "d" + i,
                Color = Colors[random.Next(Colors.Length)],
                Labels = Labels.Where(_ => random.Next(4) == 0).ToArray(),
                Count = random.Next(-3, 12),
                Big = random.NextInt64(-5, 5) * 1_000_000_000_000L,
                Rate = random.Next(-4, 8) * 0.5,
                When = Epoch.AddDays(random.Next(0, 10)).AddMilliseconds(random.Next(0, 3) * 250),
                Flag = random.Next(2) == 0,
                Maybe = random.Next(3) == 0 ? null : random.Next(0, 6),
                Due = new DateTimeOffset(Epoch.AddHours(random.Next(0, 6) * 12)),
                Text = "doc " + i,
                Unfiltered = "u",
            });
        }

        return docs;
    }

    private sealed class ExpressionGenerator(Random random, ParameterExpression x)
    {
        private static readonly ExpressionType[] Operators =
            [ExpressionType.Equal, ExpressionType.NotEqual, ExpressionType.LessThan, ExpressionType.LessThanOrEqual, ExpressionType.GreaterThan, ExpressionType.GreaterThanOrEqual];

        public Expression Next(int depth)
        {
            if (depth <= 0 || random.Next(3) == 0)
            {
                return Leaf();
            }

            return random.Next(5) switch
            {
                0 or 1 => Expression.AndAlso(Next(depth - 1), Next(depth - 1)),
                2 or 3 => Expression.OrElse(Next(depth - 1), Next(depth - 1)),
                _ => Expression.Not(Next(depth - 1)),
            };
        }

        private Expression Leaf()
        {
            ExpressionType op = Operators[random.Next(Operators.Length)];
            ExpressionType orderless = random.Next(2) == 0 ? ExpressionType.Equal : ExpressionType.NotEqual;
            switch (random.Next(14))
            {
                case 0:
                    return Compare(op, Expression.Property(x, "Count"), Expression.Constant(random.Next(-4, 14)));
                case 1:
                    // Widened member: int compared with a long.
                    return Compare(op, Expression.Convert(Expression.Property(x, "Count"), typeof(long)), Expression.Constant((long)random.Next(-4, 14)));
                case 2:
                    return Compare(op, Expression.Property(x, "Big"), Expression.Constant(random.NextInt64(-6, 6) * 1_000_000_000_000L));
                case 3:
                    return Compare(op, Expression.Property(x, "Rate"), Expression.Constant(random.Next(-5, 9) * 0.5));
                case 4:
                    // A fractional bound against an integer field.
                    return Compare(op, Expression.Convert(Expression.Property(x, "Count"), typeof(double)), Expression.Constant((random.Next(-5, 25) * 0.5)));
                case 5:
                    return Compare(op, Expression.Property(x, "When"), Expression.Constant(Epoch.AddDays(random.Next(-1, 11)).AddMilliseconds(random.Next(0, 3) * 250)));
                case 6:
                    return Compare(op, Expression.Property(x, "Due"), Expression.Constant(new DateTimeOffset(Epoch.AddHours(random.Next(-1, 7) * 12))));
                case 7:
                    return random.Next(2) == 0 ? Expression.Property(x, "Flag") : Expression.Equal(Expression.Property(x, "Flag"), Expression.Constant(random.Next(2) == 0));
                case 8:
                    return Compare(op, Expression.Property(x, "Maybe"), Expression.Constant((int?)random.Next(-1, 7), typeof(int?)));
                case 9:
                    return Expression.MakeBinary(orderless, Expression.Property(x, "Color"), Expression.Constant(Colors[random.Next(Colors.Length)]));
                case 10:
                    // Constant on the left.
                    return Compare(op, Expression.Constant(random.Next(-4, 14)), Expression.Property(x, "Count"));
                case 11:
                    return Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], Expression.Property(x, "Labels"), Expression.Constant(Labels[random.Next(Labels.Length)]));
                case 12:
                    return Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], Expression.Constant(Colors.Where(_ => random.Next(2) == 0).ToArray()), Expression.Property(x, "Color"));
                default:
                    return Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(int)], Expression.Constant(Enumerable.Range(-2, 8).Where(_ => random.Next(2) == 0).ToArray()), Expression.Property(x, "Count"));
            }
        }

        private static BinaryExpression Compare(ExpressionType op, Expression left, Expression right) => Expression.MakeBinary(op, left, right);
    }

    [Fact]
    public void WhereMatchesLinqToObjectsOnSeededRandomExpressions()
    {
        int cases = 0;
        int nonTrivial = 0;
        for (int seed = 1; seed <= 40; seed++)
        {
            var random = new Random(seed);
            List<DiffDoc> docs = MakeDocs(random, 120);
            using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
            index.Upsert(docs);
            index.Commit();
            for (int round = 0; round < 3; round++)
            {
                // Churn: some replaced, some deleted, so segments and deletes take part.
                foreach (DiffDoc d in docs.Where(_ => random.Next(6) == 0).ToList())
                {
                    d.Count = random.Next(-3, 12);
                    d.Flag = !d.Flag;
                    index.Upsert(d);
                }

                foreach (DiffDoc d in docs.Where(_ => random.Next(15) == 0).ToList())
                {
                    index.Delete(d.Id);
                    docs.Remove(d);
                }

                index.Commit();
            }

            ParameterExpression x = Expression.Parameter(typeof(DiffDoc), "x");
            var generator = new ExpressionGenerator(random, x);
            for (int i = 0; i < 100; i++)
            {
                Expression body = generator.Next(3);
                Expression<Func<DiffDoc, bool>> predicate = Expression.Lambda<Func<DiffDoc, bool>>(body, x);
                string[] expected = docs.Where(predicate.Compile()).Select(d => d.Id).Order(StringComparer.Ordinal).ToArray();
                string[] actual = index.Search().Where(predicate).Take(1000).Select(h => h.Id).Order(StringComparer.Ordinal).ToArray();
                Assert.True(expected.SequenceEqual(actual), $"seed {seed} case {i}: {predicate}\nexpected {expected.Length}: {string.Join(',', expected.Take(20))}\nactual   {actual.Length}: {string.Join(',', actual.Take(20))}");
                cases++;
                if (expected.Length > 0 && expected.Length < docs.Count)
                {
                    nonTrivial++;
                }
            }
        }

        Assert.Equal(40 * 100, cases);
        Assert.True(nonTrivial > cases / 2, $"only {nonTrivial} of {cases} cases were selective");
    }

    [Fact]
    public void WhereAlsoFiltersTextSearches()
    {
        var random = new Random(7);
        List<DiffDoc> docs = MakeDocs(random, 100);
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(docs);
        index.Commit();
        string[] expected = docs.Where(d => d.Count > 3 && d.Flag).Select(d => d.Id).Order(StringComparer.Ordinal).ToArray();
        string[] actual = index.Search("doc").Where(x => x.Count > 3).Where(x => x.Flag).Take(1000).Select(h => h.Id).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        Assert.Equal(["d5"], index.Search("\"doc 5\"").Where(x => x.Count > -100).Select(h => h.Id).ToArray());
        Assert.Empty(index.Search("\"doc 5\"").Where(x => x.Count > 100));
    }

    [Fact]
    public void CapturedVariablesConstantsAndSimpleCallsAreRead()
    {
        var random = new Random(3);
        List<DiffDoc> docs = MakeDocs(random, 100);
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(docs);
        index.Commit();

        int min = 4;
        DateTime from = Epoch.AddDays(2);
        string color = "red";
        string[] labels = ["alpha", "beta"];
        List<int> counts = [1, 3, 5];
        var holder = new Holder { Threshold = 2.5, Since = Epoch.AddDays(1) };
        DateTimeOffset due = new(Epoch.AddHours(12));

        void Check(Expression<Func<DiffDoc, bool>> predicate)
        {
            string[] expected = docs.Where(predicate.Compile()).Select(d => d.Id).Order(StringComparer.Ordinal).ToArray();
            string[] actual = index.Search().Where(predicate).Take(1000).Select(h => h.Id).Order(StringComparer.Ordinal).ToArray();
            Assert.True(expected.Length > 0, predicate.ToString());
            Assert.Equal(expected, actual);
        }

        Check(x => x.Count >= min);
        Check(x => x.When > from && x.Color == color);
        Check(x => x.Color != color || x.Flag);
        Check(x => labels.Contains(x.Color) || x.Labels.Contains(labels[1]));
        Check(x => counts.Contains(x.Count));
        Check(x => x.Rate < holder.Threshold);
        Check(x => x.When >= holder.Since);
        Check(x => x.When >= Epoch.AddDays(3).AddHours(-1));
        Check(x => x.Due > due);
        Check(x => x.Due > new DateTimeOffset(Epoch.AddHours(24)));
        Check(x => x.Count > min - 1 && !(x.Flag));
        Check(x => !x.Flag);
        Check(x => x.Maybe > 2);
        Check(x => x.Maybe != 3);
        Check(x => x.Count > 2.5);
        Check(x => x.Count <= 2.5);
        Check(x => x.Count == 3.0);
        Check(x => x.Labels.Contains("alpha") && !x.Labels.Contains("beta"));
        Check(x => new[] { "red", "blue" }.Contains(x.Color));
    }

    private sealed class Holder
    {
        public double Threshold { get; set; }

        public DateTime Since { get; set; }
    }

    [Fact]
    public void EveryMappedTypeRoundTripsThroughTheIndex()
    {
        var doc = new DiffDoc
        {
            Id = "r", Color = "red", Labels = ["alpha", "beta"], Count = -7, Big = long.MinValue / 2, Rate = -0.25, When = new DateTime(2024, 2, 29, 23, 59, 59, 123, DateTimeKind.Utc),
            Flag = true, Maybe = null, Due = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), Text = "round trip", Unfiltered = "kept",
        };
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(doc);
        index.Upsert(new DiffDoc { Id = "s", Maybe = 5, Text = "second", Color = "blue", When = doc.When.ToLocalTime() });
        index.Commit();
        DiffDoc back = index.Search("round").Single().Document;
        Assert.Equivalent(doc, back, strict: true);
        Assert.Null(back.Maybe);
        Assert.Equal(5, index.Search("second").Single().Document.Maybe);
        Assert.Equal(DateTimeKind.Utc, index.Search("second").Single().Document.When.Kind);
        Assert.Equal(doc.When, index.Search("second").Single().Document.When);
    }

    [Fact]
    public void AnEmptyKeywordIsNotIndexed()
    {
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(new DiffDoc { Id = "e", Color = string.Empty, Text = "empty" });
        index.Upsert(new DiffDoc { Id = "f", Color = "x", Text = "filled" });
        index.Commit();
        Assert.Empty(index.Search().Where(d => d.Color == string.Empty));
        Assert.Equal(["f"], index.Search().Where(d => d.Color == "x").Select(h => h.Id).ToArray());
        Assert.Equal(["e"], index.Search().Where(d => d.Color != "x").Select(h => h.Id).ToArray());
    }

    [Fact]
    public void ConstantPredicatesAreAcceptedToo()
    {
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(MakeDocs(new Random(1), 10));
        index.Commit();
        bool yes = true;
        Assert.Equal(10, index.Search().Where(x => true).Take(100).Count());
        Assert.Equal(0, index.Search().Where(x => false).Take(100).Count());
        Assert.Equal(10, index.Search().Where(x => yes).Take(100).Count());
        Assert.Equal(0, index.Search().Where(x => !yes).Take(100).Count());
    }

    [Theory]
    [InlineData("x => x.Text == \"doc 1\"", "x.Text", "full-text")]
    [InlineData("x => x.Unfiltered == \"u\"", "x.Unfiltered", "not filterable")]
    [InlineData("x => x.Color.StartsWith(\"r\")", "x.Color.StartsWith(", "string methods")]
    [InlineData("x => x.Color.Contains(\"r\")", "x.Color.Contains(", "")]
    [InlineData("x => x.Color == null", "(x.Color == null)", "null")]
    [InlineData("x => x.Count + 1 > 3", "x.Count + 1", "one side must be a member")]
    [InlineData("x => x.Count > x.Big", "> x.Big)", "document")]
    [InlineData("x => x.Color.Length > 2", "x.Color.Length", "")]
    [InlineData("x => x.Count.ToString() == \"3\"", "x.Count.ToString(", "")]
    [InlineData("x => x.Color.CompareTo(\"a\") > 0", "CompareOrdinal", "")]
    [InlineData("x => x.Labels.Any()", "x.Labels", "")]
    [InlineData("x => x.Color == \"a\" && x.Text == \"b\"", "x.Text", "full-text")]
    [InlineData("x => x.Count == int.Parse(x.Color)", "x.Color", "")]
    public void AnUntranslatableExpressionNamesTheSubExpression(string source, string fragment, string reason)
    {
        Expression<Func<DiffDoc, bool>> predicate = Parse(source);
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        var e = Assert.Throws<UnsupportedExpressionException>(() => index.Search().Where(predicate));
        Assert.Contains(fragment, e.Message);
        Assert.Contains(reason, e.Message);
        Assert.StartsWith("The expression '", e.Message);
        Assert.NotNull(e.Expression);
    }

    // The test table above is written as source text for readability; these are the same lambdas, built by hand so that no compiler is needed at run time.
    private static Expression<Func<DiffDoc, bool>> Parse(string source) => source switch
    {
        "x => x.Text == \"doc 1\"" => x => x.Text == "doc 1",
        "x => x.Unfiltered == \"u\"" => x => x.Unfiltered == "u",
        "x => x.Color.StartsWith(\"r\")" => x => x.Color.StartsWith('r'),
        "x => x.Color.Contains(\"r\")" => x => x.Color.Contains('r', StringComparison.Ordinal),
        "x => x.Color == null" => x => x.Color == null!,
        "x => x.Count + 1 > 3" => x => x.Count + 1 > 3,
        "x => x.Count > x.Big" => x => x.Count > x.Big,
        "x => x.Color.Length > 2" => x => x.Color.Length > 2,
        "x => x.Count.ToString() == \"3\"" => x => x.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) == "3",
        "x => x.Color.CompareTo(\"a\") > 0" => x => string.CompareOrdinal(x.Color, "a") > 0,
        "x => x.Labels.Any()" => x => x.Labels.Any(),
        "x => x.Color == \"a\" && x.Text == \"b\"" => x => x.Color == "a" && x.Text == "b",
        "x => x.Count == int.Parse(x.Color)" => x => x.Count == int.Parse(x.Color, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentException(source),
    };

    [Fact]
    public void ThrowingWhileReadingACapturedValueIsReportedAsUnsupported()
    {
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        string? none = null;
        var e = Assert.Throws<UnsupportedExpressionException>(() => index.Search().Where(x => x.Count > none!.Length));
        Assert.Contains("none", e.Message);
    }
}
