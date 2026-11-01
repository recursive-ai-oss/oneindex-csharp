using System.Reflection;
using OneIndex.Index;
using OneIndex.Vectors;

namespace OneIndex.Models;

/// <summary>
/// The default English learned sparse encoder: opensearch-neural-sparse-encoding-doc-v3-distill (Apache-2.0), an inference-free
/// model. Documents run through its 6-layer masked language model; queries are only tokenized and weighted from the published table.
/// </summary>
/// <remarks>
/// <para>The packed weights are the published ones converted to the OneModel format with the matrix-multiply weights stored as int8.
/// The model was trained by its authors on public sentence-pair and retrieval sets that include MS MARCO; this package did not train
/// it (see THIRD-PARTY-NOTICES.md). The same model can be loaded from files with <see cref="Open"/>.</para>
/// <para>Texts longer than 512 tokens are cut when encoded as documents.</para>
/// </remarks>
public sealed class OpenSearchSparseEncoder : ISparseEncoder, IDisposable
{
    private const string WeightsResource = "OneIndex.Models.Weights.neural-sparse.onemodel";
    private const string VocabularyResource = "OneIndex.Models.Weights.neural-sparse.vocab.txt";
    private const string WeightTableResource = "OneIndex.Models.Weights.neural-sparse.idf.json";
    private const string PackedVariant = "int8";

    private readonly NeuralSparseEncoder _inner;

    private OpenSearchSparseEncoder(NeuralSparseEncoder inner) => _inner = inner;

    /// <inheritdoc />
    public string ModelId => _inner.ModelId;

    /// <summary>The underlying encoder.</summary>
    public NeuralSparseEncoder Inner => _inner;

    /// <summary>Opens the encoder with the weights packed in this package.</summary>
    /// <returns>The encoder; dispose it to release the model.</returns>
    /// <exception cref="FileNotFoundException">The assembly was built without the packed weights; use <see cref="Open"/>.</exception>
    public static OpenSearchSparseEncoder Load()
    {
        Assembly assembly = typeof(OpenSearchSparseEncoder).Assembly;
        const string Missing = "This build of OneIndex.Models does not contain the neural sparse weights; use OpenSearchSparseEncoder.Open with model files.";
        using Stream model = assembly.GetManifestResourceStream(WeightsResource) ?? throw new FileNotFoundException(Missing);
        using Stream vocabulary = assembly.GetManifestResourceStream(VocabularyResource) ?? throw new FileNotFoundException(Missing);
        using Stream table = assembly.GetManifestResourceStream(WeightTableResource) ?? throw new FileNotFoundException(Missing);
        return new OpenSearchSparseEncoder(NeuralSparseEncoder.Open(model, vocabulary, table, Options(PackedVariant)));
    }

    /// <summary>Opens the encoder from a directory holding <c>model.onemodel</c> (or <c>model.onnx</c>), <c>vocab.txt</c> and <c>idf.json</c>, for example another quantization of the same model.</summary>
    /// <param name="directory">The directory.</param>
    /// <param name="variant">A short name of the weights (for example <c>fp32</c>) that becomes part of <see cref="ModelId"/>.</param>
    /// <returns>The encoder.</returns>
    public static OpenSearchSparseEncoder Open(string directory, string variant)
    {
        ArgumentException.ThrowIfNullOrEmpty(variant);
        return new OpenSearchSparseEncoder(NeuralSparseEncoder.OpenDirectory(directory, Options(variant)));
    }

    private static NeuralSparseEncoderOptions Options(string variant) => new()
    {
        ModelId = "opensearch-neural-sparse-encoding-doc-v3-distill/" + variant,
        ExtraLog1p = 1, // the published pipeline applies the activation a second time; see docs/sparse.md
    };

    /// <inheritdoc />
    public void Encode(IReadOnlyList<string> texts, EmbeddingRole role, Span<SparseVector> destination, CancellationToken cancellationToken = default) =>
        _inner.Encode(texts, role, destination, cancellationToken);

    /// <inheritdoc />
    public ValueTask EncodeAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<SparseVector> destination, CancellationToken cancellationToken = default) =>
        _inner.EncodeAsync(texts, role, destination, cancellationToken);

    /// <summary>Releases the model.</summary>
    public void Dispose() => _inner.Dispose();
}
