namespace OneIndex.Relevance;

public class Placeholder
{
    [Fact]
    public void ProjectBuildsAgainstTheCore() => Assert.NotNull(typeof(global::OneIndex.Storage.IStorage));
}
