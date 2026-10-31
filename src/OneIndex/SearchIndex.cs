namespace OneIndex;

/// <summary>
/// Entry points of the happy path: open an index of attributed documents in a directory or in memory. The
/// behaviour is not built yet; the members exist so that the API shape can be reviewed and later layers compile against it.
/// </summary>
public static class SearchIndex
{
    /// <summary>Opens (or creates) an index in a directory.</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="path">The directory.</param>
    /// <returns>The index.</returns>
    /// <exception cref="NotImplementedException">Always, for now.</exception>
    public static SearchIndex<T> Open<T>(string path)
        where T : class
        => throw new NotImplementedException("SearchIndex.Open is not implemented yet.");

    /// <summary>Creates an index that lives in memory only.</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <returns>The index.</returns>
    /// <exception cref="NotImplementedException">Always, for now.</exception>
    public static SearchIndex<T> InMemory<T>()
        where T : class
        => throw new NotImplementedException("SearchIndex.InMemory is not implemented yet.");
}

/// <summary>An open index of <typeparamref name="T"/> documents. Not implemented yet.</summary>
/// <typeparam name="T">The attributed document type.</typeparam>
public sealed class SearchIndex<T> : IDisposable
    where T : class
{
    internal SearchIndex()
    {
    }

    /// <summary>Releases the index.</summary>
    public void Dispose()
    {
    }
}
