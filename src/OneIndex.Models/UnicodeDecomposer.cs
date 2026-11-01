using OneIndex.Analysis.Unicode;
using OneIndex.Models.Tokenization;

namespace OneIndex.Models;

/// <summary>
/// Canonical decomposition (NFD) from the Unicode tables built into OneIndex, so that accent stripping gives the same result on
/// every platform, including the browser and builds with invariant globalization where the runtime's own normalization does nothing.
/// Thread-safe.
/// </summary>
public sealed class UnicodeDecomposer : ICanonicalDecomposer
{
    [ThreadStatic]
    private static Normalizer? s_normalizer;

    /// <summary>The shared instance.</summary>
    public static UnicodeDecomposer Instance { get; } = new();

    /// <inheritdoc />
    public bool TryDecompose(ReadOnlySpan<char> text, Span<char> destination, out int written) =>
        (s_normalizer ??= new Normalizer()).TryNormalize(text, UnicodeForm.Nfd, destination, out written);
}
