using System.Reflection;
using OneIndex.Vectors;

namespace OneIndex.Models;

/// <summary>
/// The default English dense embedder: BAAI/bge-small-en-v1.5 (MIT licence), 384 dimensions, with its published weights packed in
/// this package.
/// </summary>
/// <remarks>
/// <para>Queries get the instruction the model's authors document for searching passages with short queries (<see cref="QueryInstruction"/>);
/// documents get none. Vectors have unit length, so the dot product is cosine similarity. Texts longer than 512 tokens are cut.</para>
/// <para>The packed weights are the published ones converted to the OneModel format, with the matrix-multiply weights quantized to int8
/// (the choice and its measured effect on retrieval quality are in <c>docs/models.md</c>). Their licence notice is packed with the
/// package under <c>models/bge-small-en-v1.5/</c>.</para>
/// </remarks>
public sealed class BgeSmallEmbedder : IEmbedder, IDisposable
{
    /// <summary>The query instruction documented on the model card.</summary>
    public const string QueryInstruction = "Represent this sentence for searching relevant passages: ";

    private const string WeightsResource = "OneIndex.Models.Weights.bge-small-en-v1.5.onemodel";
    private const string VocabularyResource = "OneIndex.Models.Weights.bge-small-en-v1.5.vocab.txt";
    private const string PackedVariant = "int8";

    private readonly BertEmbedder _inner;

    private BgeSmallEmbedder(BertEmbedder inner) => _inner = inner;

    /// <inheritdoc />
    public string ModelId => _inner.ModelId;

    /// <inheritdoc />
    public int Dimension => _inner.Dimension;

    /// <inheritdoc />
    public bool IsNormalized => true;

    /// <summary>The underlying embedder, for its tokenizer and kernel information.</summary>
    public BertEmbedder Inner => _inner;

    /// <summary>Opens the embedder with the weights packed in this package.</summary>
    /// <returns>The embedder; dispose it to release the model.</returns>
    /// <exception cref="FileNotFoundException">The assembly was built without the packed weights (a development build that did not run
    /// the model preparation step).</exception>
    public static BgeSmallEmbedder Load()
    {
        Assembly assembly = typeof(BgeSmallEmbedder).Assembly;
        using Stream model = assembly.GetManifestResourceStream(WeightsResource)
            ?? throw new FileNotFoundException("This build of OneIndex.Models does not contain the bge-small-en-v1.5 weights; use BgeSmallEmbedder.Open with model files.");
        using Stream vocabulary = assembly.GetManifestResourceStream(VocabularyResource)
            ?? throw new FileNotFoundException("This build of OneIndex.Models does not contain the bge-small-en-v1.5 vocabulary.");
        return new BgeSmallEmbedder(BertEmbedder.Open(model, vocabulary, Options(PackedVariant)));
    }

    /// <summary>Opens the embedder from model files, for example a different quantization of the same model.</summary>
    /// <param name="modelPath">An ONNX or OneModel file of bge-small-en-v1.5.</param>
    /// <param name="vocabularyPath">The model's <c>vocab.txt</c>.</param>
    /// <param name="variant">A short name of the weights (for example <c>fp32</c>) that becomes part of <see cref="ModelId"/>.</param>
    /// <returns>The embedder.</returns>
    public static BgeSmallEmbedder Open(string modelPath, string vocabularyPath, string variant)
    {
        ArgumentException.ThrowIfNullOrEmpty(variant);
        return new BgeSmallEmbedder(BertEmbedder.Open(modelPath, vocabularyPath, Options(variant)));
    }

    /// <inheritdoc />
    public void Embed(IReadOnlyList<string> texts, EmbeddingRole role, Span<float> destination, CancellationToken cancellationToken = default) =>
        _inner.Embed(texts, role, destination, cancellationToken);

    /// <inheritdoc />
    public ValueTask EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<float> destination, CancellationToken cancellationToken = default) =>
        _inner.EmbedAsync(texts, role, destination, cancellationToken);

    /// <summary>Releases the model.</summary>
    public void Dispose() => _inner.Dispose();

    private static BertEmbedderOptions Options(string variant) => new()
    {
        ModelId = "bge-small-en-v1.5/" + variant,
        QueryPrefix = QueryInstruction,
    };
}
