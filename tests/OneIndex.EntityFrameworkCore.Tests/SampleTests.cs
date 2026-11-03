using OneIndex.Samples.Ef;
using OneIndex.Samples.HappyPath;
using OneIndex.Samples.Migration;
using OneIndex.Samples.ReadsLike;

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

    [Fact]
    public void TheMigrationGuideExamplesRun()
    {
        (string[] ids, string[] facets, string highlight) = MigrationExample.HappyPath();
        Assert.Equal(["p4", "p1"], ids);
        Assert.Equal(["audio=2"], facets);
        Assert.Contains("<em>", highlight, StringComparison.Ordinal);
        string[] low = MigrationExample.LowLevel();
        Assert.Equal(["p1"], low);   // the low-level schema indexes titles only
    }

    [Fact]
    public void TheReadsLikeExamplesRun()
    {
        Assert.Equal(3.5, ReadsLikeExamples.SumPrices("""[{"price":1.5},{"price":2}]"""u8));
        Assert.Equal(["head: 2 documents, 3 occurrences", "headphones: 1 documents, 1 occurrences", "headsets: 1 documents, 1 occurrences"], ReadsLikeExamples.WalkTitleTerms());
    }
}
