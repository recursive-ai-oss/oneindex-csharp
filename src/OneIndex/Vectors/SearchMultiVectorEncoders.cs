namespace OneIndex.Vectors;

/// <summary>
/// The multi-vector encoder that indexes open with when none is passed to them, the sibling of <see cref="SearchEmbedders"/>. Automatic encoding
/// (<c>[MultiVector(From = ...)]</c>) and the <c>MaxSim</c> search stage use it. There is no discovery: the application sets it explicitly.
/// </summary>
public static class SearchMultiVectorEncoders
{
    private static IMultiVectorEncoder? s_default;

    /// <summary>The encoder new indexes use, or <see langword="null"/> when none is set (documents then need their token vectors supplied). Setting it does not affect indexes already open.</summary>
    public static IMultiVectorEncoder? Default
    {
        get => Volatile.Read(ref s_default);
        set => Volatile.Write(ref s_default, value);
    }
}
