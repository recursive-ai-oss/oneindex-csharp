using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex.Tests;

public class UserTextTests
{
    // Lone surrogates, controls and astral characters are built from code points so that no tool rewrites them on the way.
    private static readonly string HighSurrogate = ((char)0xD800).ToString();
    private static readonly string LowSurrogate = ((char)0xDC00).ToString();
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static string Describe(string? input)
    {
        UserText t = UserText.Parse(input);
        string required = string.Join(" & ", t.Required.Select(g => "(" + string.Join(" | ", g.Select(Show)) + ")"));
        string excluded = string.Join(" ", t.Excluded.Select(o => "!" + Show(o)));
        return (required + " " + excluded).Trim();
    }

    private static string Show(UserTextOperand o) => o.IsPhrase ? "\"" + o.Text + "\"" : o.Text;

    [Theory]
    [InlineData("overdue invoice", "(overdue) & (invoice)")]
    [InlineData("  overdue \t invoice \n", "(overdue) & (invoice)")]
    [InlineData("\"acme corp\" overdue", "(\"acme corp\") & (overdue)")]
    [InlineData("overdue -draft", "(overdue) !draft")]
    [InlineData("overdue -\"first draft\"", "(overdue) !\"first draft\"")]
    [InlineData("cat OR dog", "(cat | dog)")]
    [InlineData("cat OR dog OR bird fish", "(cat | dog | bird) & (fish)")]
    [InlineData("cat or dog", "(cat) & (or) & (dog)")]
    [InlineData("OR cat", "(cat)")]
    [InlineData("cat OR", "(cat)")]
    [InlineData("cat OR OR dog", "(cat | dog)")]
    [InlineData("cat OR -dog bird", "(cat) & (bird) !dog")]
    [InlineData("-", "(-)")]
    [InlineData("- cat", "(-) & (cat)")]
    [InlineData("cat -", "(cat) & (-)")]
    [InlineData("--cat", "!-cat")]
    [InlineData("state-of-the-art", "(state-of-the-art)")]
    [InlineData("\"unterminated phrase", "(\"unterminated phrase\")")]
    [InlineData("word\"inner phrase\"word", "(word) & (\"inner phrase\") & (word)")]
    [InlineData("\"\" cat", "(cat)")]
    [InlineData("\"", "")]
    public void TheGrammarReadsWordsPhrasesExclusionsAndOr(string input, string expected) => Assert.Equal(expected, Describe(input));

    [Fact]
    public void ControlsAndLoneSurrogatesSeparateWordsLikeSpaces()
    {
        string nul = ((char)0).ToString();
        string bell = ((char)7).ToString();
        Assert.Equal("(cat) & (dog) & (bird)", Describe("cat" + nul + "dog" + bell + "bird"));
        Assert.Equal("(cat) & (dog)", Describe("cat " + HighSurrogate + " dog"));
        Assert.Equal("(cat) & (dog)", Describe("cat " + LowSurrogate + "dog"));
        Assert.Equal("(cat) & (dog)", Describe("cat" + HighSurrogate + HighSurrogate + "dog"));
        Assert.Equal("(cat) & (dog)", Describe("cat" + LowSurrogate + HighSurrogate + "dog"));
        Assert.Equal("(a" + Emoji + "b)", Describe("a" + Emoji + "b"));
        Assert.Equal(string.Empty, Describe(HighSurrogate));
    }

    [Fact]
    public void BlankInputIsBlank()
    {
        Assert.True(UserText.Parse(null).IsBlank);
        Assert.True(UserText.Parse(string.Empty).IsBlank);
        Assert.True(UserText.Parse(" \t\r\n" + (char)1 + " ").IsBlank);
        Assert.False(UserText.Parse("?").IsBlank);
    }

    [Fact]
    public void HugeInputIsCutAndTheOperandCountIsCapped()
    {
        string many = string.Join(' ', Enumerable.Range(0, 5000).Select(i => "w" + i));
        UserText t = UserText.Parse(many);
        Assert.Equal(UserText.MaxOperands, t.Required.Count);
        string wide = new('x', 1_000_000);
        Assert.True(UserText.Parse(wide).Required.Single().Single().Text.Length <= UserText.MaxLength);

        // A surrogate pair at the cut is dropped whole, not split.
        string pair = new string('a', UserText.MaxLength - 1) + Emoji + "tail";
        Assert.DoesNotContain(UserText.Parse(pair).Required.Single().Single().Text, c => char.IsSurrogate(c));
    }

    private static UserTextCompiler Compiler() => new(SearchMappings.Require<Article>().Schema);

    [Fact]
    public void WordsBecomeBm25fTermsOverTheFullTextFieldsPerAnalyzer()
    {
        // Title is analyzed as "standard", Body as "en": each is searched with the terms its own analyzer produces.
        Search.Query q = Compiler().Compile("Running");
        var shoulds = Assert.IsType<BooleanQuery>(q);
        Assert.Equal(Occur.Should, shoulds.Clauses[0].Occur);
        Assert.Contains(shoulds.Clauses, c => c.Query is TypoTermQuery { Fields: ["Title"], Term: "running" });
        Assert.Contains(shoulds.Clauses, c => c.Query is TypoTermQuery { Fields: ["Body"], Term: "run" });
    }

    [Fact]
    public void FieldsThatShareAnAnalyzerAreOneBm25fTerm()
    {
        var compiler = new UserTextCompiler(SearchMappings.Require<TwoStandardFields>().Schema);
        // A finished short word has no typos and is not a beginning: the plain BM25F term. The word being typed is a typo-tolerant term.
        var q = Assert.IsType<MultiFieldTermQuery>(compiler.Compile("Word "));
        Assert.Equal(["First", "Second"], q.Fields);
        Assert.Equal("word", q.Term);
        var typing = Assert.IsType<TypoTermQuery>(compiler.Compile("Word"));
        Assert.Equal(["First", "Second"], typing.Fields);
        Assert.True(typing.Prefix);
        Assert.Equal(0, typing.MaxTypos);
    }

    [Fact]
    public void ExclusionsAreMustNotAndAnExclusionOnlyQueryStartsFromEverything()
    {
        var q = Assert.IsType<BooleanQuery>(Compiler().Compile("-draft"));
        Assert.IsType<MatchAllQuery>(q.Clauses[0].Query);
        Assert.Equal(Occur.MustNot, q.Clauses[1].Occur);
    }

    [Fact]
    public void ABlankQueryMatchesAllAndAQueryWithNothingToSearchMatchesNothing()
    {
        Assert.IsType<MatchAllQuery>(Compiler().Compile(null));
        Assert.IsType<MatchAllQuery>(Compiler().Compile("  "));
        var none = Assert.IsType<BooleanQuery>(Compiler().Compile("?!... --"));
        Assert.Empty(none.Clauses);
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        index.Upsert(new Article { Id = "1", Title = "x", Body = "y" });
        index.Commit();
        Assert.Empty(index.Search("?!"));
        Assert.Single(index.Search(string.Empty));
    }

    [Fact]
    public void ASchemaWithoutFullTextFieldsMatchesNothingForWords()
    {
        var compiler = new UserTextCompiler(SearchMappings.Require<Unsearchable>().Schema);
        Assert.False(compiler.HasSearchableFields);
        Assert.Empty(Assert.IsType<BooleanQuery>(compiler.Compile("anything")).Clauses);
    }

    [Fact]
    public void TheSemanticsAreAndOrPhraseAndExclusion()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        string[] bodies = ["cat dog", "cat bird", "dog bird", "the cat sat with a dog", "cat", "fish draft", "big black cat"];
        for (int i = 0; i < bodies.Length; i++)
        {
            index.Upsert(new Article { Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Title = string.Empty, Body = bodies[i] });
        }

        index.Commit();
        string[] Ids(string q) => index.Search(q).Take(100).Select(h => bodies[int.Parse(h.Id, System.Globalization.CultureInfo.InvariantCulture)]).Order().ToArray();
        static string[] Sorted(params string[] values) => values.Order().ToArray();
        Assert.Equal(Sorted("cat dog", "the cat sat with a dog"), Ids("cat dog"));
        Assert.Equal(Sorted("cat dog"), Ids("\"cat dog\""));
        Assert.Equal(Sorted("big black cat"), Ids("\"black cat\""));
        Assert.Equal(Sorted("cat", "cat bird", "big black cat"), Ids("cat -dog"));
        Assert.Equal(Sorted("cat", "cat bird", "cat dog", "big black cat", "the cat sat with a dog"), Ids("cat"));
        Assert.Equal(Sorted("cat bird", "cat dog", "dog bird", "the cat sat with a dog"), Ids("dog OR bird"));
        Assert.Equal(Sorted("cat", "cat bird", "cat dog", "big black cat", "the cat sat with a dog"), Ids("cat OR zebra"));
        Assert.Equal(bodies.Length - 1, Ids("-draft").Length);
    }

    [Fact]
    public void StopWordsAreDroppedFromTheStemmedField()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        index.Upsert(new Article { Id = "1", Title = string.Empty, Body = "state of the art" });
        index.Commit();
        Assert.Single(index.Search("\"state of the art\""));
        Assert.Single(index.Search("state-of-the-art"));
        Assert.Empty(index.Search("the"));
    }

    [Fact]
    public void AwkwardInputNeverThrows()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        index.Upsert(new Article { Id = "1", Title = "Title", Body = "body text" });
        index.Commit();
        string[] inputs =
        [
            "\"", "\"\"\"", "-", "--", "- -", "OR", "OR OR OR", "-OR", "\" OR \"", "(", ")", "*", "?", "~", ":", "field:value", "a AND b", "NOT a", "\\",
            ((char)0).ToString(), HighSurrogate, LowSurrogate, HighSurrogate + HighSurrogate, Emoji, "-" + Emoji, "\"" + Emoji + "\"",
            new string('"', 5000), new string('-', 5000), new string('a', 100_000), string.Join(" OR ", Enumerable.Repeat("x", 3000)),
            string.Concat(Enumerable.Repeat("-\"a b\" ", 2000)), "\u200b\u200d", "\ufeff", "\uff41\uff42\uff43", "\u00df", "\u0130",
        ];
        foreach (string input in inputs)
        {
            _ = index.Search(input).ToList();
            _ = index.Search(input).CountExactly().Count();
        }
    }
}

public class Unsearchable
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Keyword]
    public string Tag { get; set; } = string.Empty;
}

public class TwoStandardFields
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string First { get; set; } = string.Empty;

    [FullText]
    public string Second { get; set; } = string.Empty;
}
