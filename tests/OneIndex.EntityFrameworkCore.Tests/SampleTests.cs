using OneIndex.Samples.Ef;
using OneIndex.Samples.HappyPath;

namespace OneIndex.EntityFrameworkCore.Tests;

/// <summary>The two samples of the design document run as written (samples/OneIndex.Samples).</summary>
public class SampleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oneindex-sample-" + Guid.NewGuid().ToString("N"));

    public SampleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void TheHappyPathSampleRuns()
    {
        Outcome o = Sample.Run(Path.Combine(_root, "search"));
        Assert.Equal(["inv-1"], o.Ids);
        Assert.Equal(["invoice=1", "overdue=1"], o.Facets);
        Assert.True(o.HadHighlights);
        Assert.True(o.Deleted);
        Assert.Equal("search_Invoice", o.ToolName);
        Assert.Contains("inv-1", o.ParsedIds);
        Assert.Equal(1, o.InMemoryCount);
    }

    [Fact]
    public async Task TheEntityFrameworkSampleRuns()
    {
        string[] ids = await EfSample.RunAsync(Path.Combine(_root, "app.db"), Path.Combine(_root, "ef-search"));
        Assert.Equal(["a"], ids);
    }
}
