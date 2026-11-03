namespace OneIndex.Tests;

public sealed class UnsizedDoc
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Text { get; set; } = "";

    // No Dimensions: the embedder's dimension is used. An empty array is "no vector supplied".
    [Vector(From = nameof(Text))]
    public float[] Embedding { get; set; } = [];
}

public class UnsizedVectorTests
{
    [Fact]
    public void TheEmbedderSizesTheVector()
    {
        using SearchIndex<UnsizedDoc> index = SearchIndex.InMemory<UnsizedDoc>(new CountingEmbedder());
        index.Upsert(new UnsizedDoc { Id = "a", Text = "alpha beta" });
        index.Commit();
        Assert.Equal(CountingEmbedder.Dim, index.Schema.Fields.Single(f => f.Name == "Embedding").Dimensions);
        Assert.Equal(["a"], index.Search("alpha").ToList().Select(h => h.Id));
    }

    [Fact]
    public void WithoutAnEmbedderTheErrorNamesTheMember()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => SearchIndex.InMemory<UnsizedDoc>(null));
        Assert.Contains("Embedding", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolSchemaNeedsNoEmbedder() => Assert.Contains("search_UnsizedDoc", SearchTool.For<UnsizedDoc>(), StringComparison.Ordinal);

    [Fact]
    public void ExcludedWordsApplyToTheVectorListToo()
    {
        using SearchIndex<UnsizedDoc> index = SearchIndex.InMemory<UnsizedDoc>(new CountingEmbedder());
        index.Upsert(new UnsizedDoc { Id = "keep", Text = "overdue notice acme" });
        index.Upsert(new UnsizedDoc { Id = "drop", Text = "overdue draft acme" });
        index.Commit();
        string[] ids = [.. index.Search("overdue acme -draft").Take(10).ToList().Select(h => h.Id)];
        Assert.Equal(["keep"], ids);
    }
}
