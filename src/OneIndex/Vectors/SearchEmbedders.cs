namespace OneIndex.Vectors;

/// <summary>
/// The embedder that indexes open with when none is passed to them. Automatic embedding (<c>[Vector(From = ...)]</c>) uses it, so
/// <c>SearchIndex.Open&lt;T&gt;(path)</c> keeps its shape: an application, or a package such as <c>RecursiveAI.OneIndex.Models</c> through its
/// <c>UseAsDefault</c> method, sets it once at startup. There is no discovery by reflection, so nothing is registered until someone calls this.
/// </summary>
public static class SearchEmbedders
{
    private static IEmbedder? s_default;

    /// <summary>The embedder new indexes use, or <see langword="null"/> when none is set (documents then need their vectors supplied, and text searches stay text-only). Setting it does not affect indexes already open.</summary>
    public static IEmbedder? Default
    {
        get => Volatile.Read(ref s_default);
        set => Volatile.Write(ref s_default, value);
    }
}

/// <summary>An <see cref="IEmbedder"/> failed or returned something unusable while an index was embedding text. Nothing was written to the index.</summary>
public sealed class EmbeddingException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong, naming the model and the field.</param>
    /// <param name="inner">The embedder's own exception, if any.</param>
    public EmbeddingException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
