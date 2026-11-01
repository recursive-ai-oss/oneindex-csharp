using System.Text;

namespace OneIndex.Models.Tokenization;

/// <summary>Canonical decomposition (Unicode Normalization Form D, UAX #15) as the tokenizer's accent stripping needs it.</summary>
public interface ICanonicalDecomposer
{
    /// <summary>Decomposes text into NFD.</summary>
    /// <param name="text">Well-formed UTF-16 (no unpaired surrogates).</param>
    /// <param name="destination">Receives the decomposed text.</param>
    /// <param name="written">The decomposed length when it fit.</param>
    /// <returns><see langword="false"/> if <paramref name="destination"/> was too small; the caller retries with more room.</returns>
    bool TryDecompose(ReadOnlySpan<char> text, Span<char> destination, out int written);
}

/// <summary>Decomposition by the runtime's own <see cref="NormalizationForm.FormD"/>.</summary>
internal sealed class RuntimeDecomposer : ICanonicalDecomposer
{
    internal static readonly RuntimeDecomposer Instance = new();

    public bool TryDecompose(ReadOnlySpan<char> text, Span<char> destination, out int written) =>
        text.TryNormalize(destination, out written, NormalizationForm.FormD);
}
