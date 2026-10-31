namespace OneIndex;

/// <summary>Describes a search over documents of a type as a tool an LLM can call (a JSON schema). Not implemented yet.</summary>
public static class SearchTool
{
    /// <summary>Builds the tool definition for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <returns>The definition.</returns>
    /// <exception cref="NotImplementedException">Always, for now.</exception>
    public static string For<T>()
        where T : class
        => throw new NotImplementedException("SearchTool.For is not implemented yet.");
}
