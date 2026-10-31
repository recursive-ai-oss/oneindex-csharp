namespace OneIndex.Tests;

public class PackageShapeTests
{
    [Fact]
    public void TheCoreAssemblyReferencesNothingButTheFrameworkLibraries()
    {
        var references = typeof(SearchIndex).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.All(references, name => Assert.True(name == "System.Private.CoreLib" || name.StartsWith("System.", StringComparison.Ordinal) || name == "netstandard", name));
    }

    [Fact]
    public void TheToolDefinitionIsBuilt()
    {
        Assert.StartsWith("{\"name\":\"search_Invoice\"", SearchTool.For<Invoice>(), StringComparison.Ordinal);
    }
}
