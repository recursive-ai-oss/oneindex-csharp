using System.Linq.Expressions;
using OneIndex.Linq;
using OneIndex.Queries;

namespace OneIndex.Tests;

/// <summary>Where(string): the filter-string form against the LINQ form (and LINQ-to-Objects) on the same random predicates the LINQ differential uses, and its errors.</summary>
public sealed class WhereStringTests(ITestOutputHelper output)
{
    [Fact]
    public void FilterStringWhereEqualsTheLinqWhereOnRandomPredicates()
    {
        int cases = 0;
        int printed = 0;
        int unprintable = 0;
        int selective = 0;
        for (int seed = 1; seed <= 40; seed++)
        {
            var random = new Random(seed);
            List<DiffDoc> docs = LinqTests.MakeDocs(random, 120);
            using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
            index.Upsert(docs);
            index.Commit();
            foreach (DiffDoc d in docs.Where(_ => random.Next(15) == 0).ToList())
            {
                index.Delete(d.Id);
                docs.Remove(d);
            }

            index.Commit();
            ParameterExpression x = Expression.Parameter(typeof(DiffDoc), "x");
            var generator = new LinqTests.ExpressionGenerator(random, x);
            for (int i = 0; i < 100; i++)
            {
                Expression<Func<DiffDoc, bool>> predicate = Expression.Lambda<Func<DiffDoc, bool>>(generator.Next(3), x);
                cases++;
                Search.Query translated = new FilterTranslator<DiffDoc>(index.Mapping).Translate(predicate);
                string text;
                try
                {
                    text = FilterString.Print(translated, index.Schema);
                }
                catch (ArgumentException)
                {
                    unprintable++; // a shape the filter grammar has no spelling for
                    continue;
                }

                printed++;
                string[] expected = docs.Where(predicate.Compile()).Select(d => d.Id).Order(StringComparer.Ordinal).ToArray();
                string[] viaLinq = index.Search().Where(predicate).Take(1000).Select(h => h.Id).Order(StringComparer.Ordinal).ToArray();
                string[] viaString = index.Search().Where(text).Take(1000).Select(h => h.Id).Order(StringComparer.Ordinal).ToArray();
                Assert.True(expected.SequenceEqual(viaString), $"seed {seed} case {i}: {predicate}\nfilter: {text}\nexpected {expected.Length}, string form {viaString.Length}");
                Assert.True(viaLinq.SequenceEqual(viaString), $"seed {seed} case {i}: {predicate}\nfilter: {text}");

                // Both forms together narrow like and.
                string[] both = index.Search().Where(text).Where(predicate).Take(1000).Select(h => h.Id).Order(StringComparer.Ordinal).ToArray();
                Assert.True(expected.SequenceEqual(both), $"seed {seed} case {i}: combined {predicate}");
                if (expected.Length > 0 && expected.Length < docs.Count)
                {
                    selective++;
                }
            }
        }

        output.WriteLine($"cases {cases}, with a filter-string spelling {printed}, without {unprintable}, selective {selective}");
        Assert.Equal(4000, cases);
        Assert.True(printed > cases * 9 / 10, $"only {printed} of {cases} predicates could be written as a filter string");
        Assert.True(selective > printed / 2);
    }

    [Fact]
    public void FilterStringWhereCombinesWithTextAndStaysNonScoring()
    {
        var random = new Random(7);
        List<DiffDoc> docs = LinqTests.MakeDocs(random, 100);
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        index.Upsert(docs);
        index.Commit();
        string[] expected = docs.Where(d => d.Count > 3 && d.Flag).Select(d => d.Id).Order(StringComparer.Ordinal).ToArray();
        string[] actual = index.Search("doc").Where("Count > 3").Where(x => x.Flag).Take(1000).Select(h => h.Id).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
        float plain = index.Search("doc").Take(1).Single().Score;
        Assert.Equal(plain, index.Search("doc").Where("Count > -100").Take(1).Single().Score);
        Assert.Equal(["d5"], index.Search("\"doc 5\"").Where("Count > -100").Select(h => h.Id).ToArray());
        Assert.Empty(index.Search("\"doc 5\"").Where("Count > 100"));
    }

    [Fact]
    public void BadFilterStringsThrowAtTheCallWithPositions()
    {
        using SearchIndex<DiffDoc> index = SearchIndex.InMemory<DiffDoc>();
        QueryParseException e = Assert.Throws<QueryParseException>(() => index.Search("x").Where("Count >"));
        Assert.Equal(7, e.Offset);
        Assert.Equal((1, 8), (e.Line, e.Column));
        Assert.Contains("Expected a value", e.Message);
        e = Assert.Throws<QueryParseException>(() => index.Search().Where("Colr = red"));
        Assert.Equal(0, e.Offset);
        Assert.Equal("Color", e.Suggestion);
        e = Assert.Throws<QueryParseException>(() => index.Search().Where("Unfiltered = x"));
        Assert.Contains("not filterable", e.Message);
        e = Assert.Throws<QueryParseException>(() => index.Search().Where("Text = x"));
        Assert.Contains("full-text", e.Message);
        e = Assert.Throws<QueryParseException>(() => index.Search().Where("Color = a\nAND Labls = b"));
        Assert.Equal((2, 5), (e.Line, e.Column));
        Assert.Equal("Labels", e.Suggestion);
        e = Assert.Throws<QueryParseException>(() => index.Search().Where("(Count > 1"));
        Assert.Contains("to close the '('", e.Message);
        Assert.Throws<ArgumentNullException>(() => index.Search().Where((string)null!));
    }
}
