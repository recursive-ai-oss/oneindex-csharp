using OneIndex.Queries;
using OneIndex.Search;

namespace OneIndex.Tests;

public class MltDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;

    [FullText]
    public string Body { get; set; } = string.Empty;
}

public sealed class MoreLikeThisTests
{
    private static SearchIndex<MltDoc> Corpus()
    {
        SearchIndex<MltDoc> index = SearchIndex.InMemory<MltDoc>();
        index.Upsert(new MltDoc { Id = "c1", Title = "Cat care", Body = "feline kitten whiskers purring litter feline grooming" });
        index.Upsert(new MltDoc { Id = "c2", Title = "Kitten litter", Body = "kitten litter whiskers feline playing the and" });
        index.Upsert(new MltDoc { Id = "c3", Title = "Feline grooming", Body = "grooming feline fur brushing kitten" });
        index.Upsert(new MltDoc { Id = "s1", Title = "Rockets", Body = "orbit rocket thrust launch payload the and" });
        index.Upsert(new MltDoc { Id = "s2", Title = "Orbit", Body = "orbit payload satellite launch the and" });
        index.Upsert(new MltDoc { Id = "m1", Title = "Kitchen", Body = "recipe oven flour the and sugar" });
        index.Commit();
        return index;
    }

    [Fact]
    public void ADocumentFindsItsNeighboursAndNotItself()
    {
        using SearchIndex<MltDoc> index = Corpus();
        List<string> ids = [.. index.MoreLikeThis("c1").Execute().Select(h => h.Id)];
        Assert.DoesNotContain("c1", ids);
        Assert.Contains("c2", ids);
        Assert.Contains("c3", ids);
        Assert.DoesNotContain("s1", ids);
        Assert.DoesNotContain("m1", ids);
    }

    [Fact]
    public void TextFindsTheTopicAndStopWordLikeTermsAreNotChosen()
    {
        using SearchIndex<MltDoc> index = Corpus();
        List<string> ids = [.. index.MoreLikeThisText("a rocket launch into orbit", x => x.Body).Execute().Select(h => h.Id)];
        Assert.Equal(["s1", "s2"], ids.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASingleOccurrenceInTheSourceOnlyCannotSelectATerm()
    {
        using SearchIndex<MltDoc> index = Corpus();
        // "purring" is in c1 alone, so it is dropped for c1 as a source; with all terms of c1 kept, only shared terms remain.
        var q = MoreLikeThisQuery.FromDocument("c1") with { Fields = ["Body"], MaxQueryTerms = 100 };
        var searcher = index.SearchBy(q).Execute();
        Assert.All(searcher, h => Assert.NotEqual("c1", h.Id));
        Assert.True(searcher.Count >= 2);
    }

    [Fact]
    public void IncludeSourceReturnsTheDocumentToo()
    {
        using SearchIndex<MltDoc> index = Corpus();
        var q = MoreLikeThisQuery.FromDocument("c1") with { IncludeSource = true };
        Assert.Contains("c1", index.SearchBy(q).Execute().Select(h => h.Id));
    }

    [Fact]
    public void MinTermFrequencyAndMaxQueryTermsLimitTheSelection()
    {
        using SearchIndex<MltDoc> index = Corpus();
        // Only "feline" occurs twice in c1's body.
        var q = MoreLikeThisQuery.FromDocument("c1") with { Fields = ["Body"], MinTermFrequency = 2, MinimumShouldMatchFraction = 1 };
        Assert.Equal(["c2", "c3"], index.SearchBy(q).Execute().Select(h => h.Id).Order(StringComparer.Ordinal));
        var none = MoreLikeThisQuery.FromText("zzz qqq") with { Fields = ["Body"] };
        Assert.Empty(index.SearchBy(none).Execute());
    }

    [Fact]
    public void UnknownDocumentsAndBadSettingsAreTypedErrors()
    {
        using SearchIndex<MltDoc> index = Corpus();
        Assert.Throws<QueryException>(() => index.MoreLikeThis("nope").Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(new MoreLikeThisQuery()).Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(MoreLikeThisQuery.FromText("x") with { MaxQueryTerms = 0 }).Execute());
        Assert.Throws<QueryException>(() => index.SearchBy(MoreLikeThisQuery.FromText("x") with { Fields = ["Id"] }).Execute());
    }

    [Fact]
    public void QueryJsonRoundTripsAndNamesBadFields()
    {
        using SearchIndex<MltDoc> index = Corpus();
        string json = "{\"v\":1,\"query\":{\"type\":\"moreLikeThis\",\"id\":\"c1\",\"fields\":[\"Body\"],\"maxQueryTerms\":10,\"minimumShouldMatch\":0.5,\"boost\":2}}";
        QueryRequest request = QueryJson.Read(json, index.Schema);
        var node = Assert.IsType<MoreLikeThisQuery>(request.Query);
        Assert.Equal("c1", node.DocumentId);
        Assert.Equal(10, node.MaxQueryTerms);
        Assert.Equal(2f, node.Boost);
        string canonical = QueryJson.Write(request);
        Assert.Equal(request, QueryJson.Read(canonical, index.Schema));
        Assert.Equal(canonical, QueryJson.Write(QueryJson.Read(canonical, index.Schema)));
        Assert.Contains("c2", index.Parse(json).Execute().Select(h => h.Id));

        QueryParseException both = Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\"query\":{\"type\":\"moreLikeThis\",\"id\":\"a\",\"text\":\"b\"}}", index.Schema));
        Assert.Equal("$.query", both.Path);
        QueryParseException typo = Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\"query\":{\"type\":\"moreLikeThis\",\"id\":\"a\",\"maxTerms\":3}}", index.Schema));
        Assert.Equal("$.query.maxTerms", typo.Path);
    }

    [Fact]
    public void ToolSchemaListsTheNode()
    {
        Assert.Contains("moreLikeThis", SearchTool.For<MltDoc>().ToString());
    }
}
