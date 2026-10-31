namespace OneIndex.Harness;

/// <summary>
/// OneIndex behind the harness. It reports itself unavailable until the engine's index writer, BM25F scoring and query
/// execution exist; the harness, the relevance runner and the benchmark rows then pick it up unchanged once <see cref="IsAvailable"/>
/// returns true and the members below are filled in.
/// </summary>
public sealed class OneIndexEngine : ITextEngine
{
    private const string NotYet = "OneIndex query execution has not landed: no index writer, BM25F scorer or query runner to drive.";

    /// <inheritdoc/>
    public string Name => "OneIndex";

    /// <inheritdoc/>
    public string Version => typeof(OneIndex.Storage.IStorage).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <inheritdoc/>
    public bool IsAvailable(out string reason)
    {
        reason = NotYet;
        return false;
    }

    /// <inheritdoc/>
    public bool Supports(Type queryType) => false;

    /// <inheritdoc/>
    public void Create(EngineSettings settings) => throw new NotSupportedException(NotYet);

    /// <inheritdoc/>
    public void Add(Doc doc) => throw new NotSupportedException(NotYet);

    /// <inheritdoc/>
    public void Commit() => throw new NotSupportedException(NotYet);

    /// <inheritdoc/>
    public void OpenSearcher() => throw new NotSupportedException(NotYet);

    /// <inheritdoc/>
    public void SetBm25(float k1, float b) => throw new NotSupportedException(NotYet);

    /// <inheritdoc/>
    public SearchResult Search(HarnessQuery query, int k) => throw new NotSupportedException(NotYet);

    /// <inheritdoc/>
    public long IndexSizeBytes() => 0;

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}

/// <summary>Names the engines the harness can drive.</summary>
public static class Engines
{
    /// <summary>All known engine names.</summary>
    public static readonly string[] Names = ["lucene", "oneindex"];

    /// <summary>Creates an engine by name.</summary>
    public static ITextEngine Create(string name) => name.ToLowerInvariant() switch
    {
        "lucene" => new LuceneEngine(),
        "oneindex" => new OneIndexEngine(),
        _ => throw new ArgumentException($"Unknown engine '{name}'. Known: {string.Join(", ", Names)}."),
    };

    /// <summary>The engines that can run now.</summary>
    public static IEnumerable<string> Available() => Names.Where(n =>
    {
        using var e = Create(n);
        return e.IsAvailable(out _);
    });
}
