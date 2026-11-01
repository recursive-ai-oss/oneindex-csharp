namespace OneIndex.Vectors;

/// <summary>
/// The sparse encoder that indexes open with when none is passed to them, the sibling of <see cref="SearchEmbedders"/>. Automatic encoding
/// (<c>[Sparse(From = ...)]</c>) uses it. There is no discovery: an application, or a package such as <c>RecursiveAI.OneIndex.Models</c>
/// through its <c>UseAsDefault</c> method, sets it explicitly.
/// </summary>
public static class SearchSparseEncoders
{
    private static ISparseEncoder? s_default;

    /// <summary>The encoder new indexes use, or <see langword="null"/> when none is set (documents then need their sparse vectors supplied). Setting it does not affect indexes already open.</summary>
    public static ISparseEncoder? Default
    {
        get => Volatile.Read(ref s_default);
        set => Volatile.Write(ref s_default, value);
    }
}
