using OneIndex.Index;

namespace OneIndex.Vectors;

/// <summary>
/// Turns text into learned sparse vectors (token to weight), the sibling of <see cref="IEmbedder"/> for SPLADE-style models
/// (Formal et al., SIGIR 2021) and inference-free encoders whose query side is a tokenizer plus a weight table.
/// </summary>
/// <remarks>
/// <para>Implementations are thread-safe and deterministic for a given <see cref="ModelId"/> and role, so a result may be cached under
/// (<see cref="ModelId"/>, role, text). Texts longer than the model's limit are truncated, never rejected; the empty string is valid
/// and gives an empty vector; a <see langword="null"/> text throws <see cref="ArgumentNullException"/>.</para>
/// <para>The document role may run a model; the query role may be much cheaper (an inference-free encoder only tokenizes and looks up
/// weights), which is why the caller always says which side a text is.</para>
/// </remarks>
public interface ISparseEncoder
{
    /// <summary>A stable identity of the model and every setting that changes its output (weights, quantization, truncation, weight table), safe as part of a cache key. Never empty.</summary>
    string ModelId { get; }

    /// <summary>Encodes a batch of texts.</summary>
    /// <param name="texts">The texts; none may be <see langword="null"/>.</param>
    /// <param name="role">Whether the texts are documents or queries.</param>
    /// <param name="destination">Receives one vector per text, in order; its length must equal <c>texts.Count</c>.</param>
    /// <param name="cancellationToken">Observed between model runs.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> has the wrong length.</exception>
    /// <exception cref="ArgumentNullException">A text is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    void Encode(IReadOnlyList<string> texts, EmbeddingRole role, Span<SparseVector> destination, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Encode"/>; results are identical.</summary>
    /// <param name="texts">The texts.</param>
    /// <param name="role">Whether the texts are documents or queries.</param>
    /// <param name="destination">Receives one vector per text; it must stay valid until the task completes.</param>
    /// <param name="cancellationToken">Observed between model runs.</param>
    ValueTask EncodeAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<SparseVector> destination, CancellationToken cancellationToken = default);
}

/// <summary>Convenience overloads for <see cref="ISparseEncoder"/>.</summary>
public static class SparseEncoderExtensions
{
    /// <summary>Encodes one text.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <param name="text">The text.</param>
    /// <param name="role">Document by default.</param>
    /// <param name="cancellationToken">Observed while the model runs.</param>
    /// <returns>The vector.</returns>
    public static SparseVector Encode(this ISparseEncoder encoder, string text, EmbeddingRole role = EmbeddingRole.Document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(text);
        var one = new SparseVector[1];
        encoder.Encode([text], role, one, cancellationToken);
        return one[0];
    }
}
