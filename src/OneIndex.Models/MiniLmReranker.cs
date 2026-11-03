using System.Reflection;
using OneIndex.Vectors;

namespace OneIndex.Models;

/// <summary>
/// The default English reranker: cross-encoder/ms-marco-MiniLM-L6-v2 (Apache-2.0 licence), a 6-layer MiniLM cross-encoder trained on MS MARCO
/// passage ranking, with its published weights packed in this package.
/// </summary>
/// <remarks>
/// <para>Scores are the model's raw logits: higher is more relevant, there is no fixed range, and they are only comparable within one query. The
/// pair is cut to 512 tokens, the longer of query and passage first.</para>
/// <para>The packed weights are the published ones converted to the OneModel format, with the matrix-multiply weights quantized to int8 (the
/// measured effect on ranking quality is in <c>docs/models.md</c>). Their licence notice is packed with the package under
/// <c>models/ms-marco-MiniLM-L6-v2/</c>. The model was trained on MS MARCO; that provenance matters if you rely on it commercially, so read the
/// model card.</para>
/// </remarks>
public sealed class MiniLmReranker : IReranker, IDisposable
{
    private const string WeightsResource = "OneIndex.Models.Weights.ms-marco-MiniLM-L6-v2.onemodel";
    private const string VocabularyResource = "OneIndex.Models.Weights.ms-marco-MiniLM-L6-v2.vocab.txt";
    private const string PackedVariant = "int8";

    private readonly CrossEncoderReranker _inner;

    private MiniLmReranker(CrossEncoderReranker inner) => _inner = inner;

    /// <inheritdoc />
    public string ModelId => _inner.ModelId;

    /// <summary>The underlying cross-encoder, for its tokenizer and kernel information.</summary>
    public CrossEncoderReranker Inner => _inner;

    /// <summary>Opens the reranker with the weights packed in this package.</summary>
    /// <returns>The reranker; dispose it to release the model.</returns>
    /// <exception cref="FileNotFoundException">The assembly was built without the packed weights (a development build that did not run
    /// the model preparation step).</exception>
    public static MiniLmReranker Load()
    {
        Assembly assembly = typeof(MiniLmReranker).Assembly;
        using Stream model = assembly.GetManifestResourceStream(WeightsResource)
            ?? throw new FileNotFoundException("This build of OneIndex.Models does not contain the ms-marco-MiniLM-L6-v2 weights; use MiniLmReranker.Open with model files.");
        using Stream vocabulary = assembly.GetManifestResourceStream(VocabularyResource)
            ?? throw new FileNotFoundException("This build of OneIndex.Models does not contain the ms-marco-MiniLM-L6-v2 vocabulary.");
        return new MiniLmReranker(CrossEncoderReranker.Open(model, vocabulary, Options(PackedVariant)));
    }

    /// <summary>
    /// Loads the packed reranker and makes it the one new indexes use (<see cref="SearchRerankers.Default"/>). Call it once at startup, before opening
    /// indexes; there is no discovery, so referencing this package alone does nothing.
    /// </summary>
    /// <returns>The reranker, which stays loaded for the life of the process (dispose it only after every index using it is closed).</returns>
    /// <exception cref="FileNotFoundException">The assembly was built without the packed weights.</exception>
    public static MiniLmReranker UseAsDefault()
    {
        MiniLmReranker reranker = Load();
        SearchRerankers.Default = reranker;
        return reranker;
    }

    /// <summary>Opens the reranker from model files, for example a different quantization of the same model.</summary>
    /// <param name="modelPath">An ONNX or OneModel file of ms-marco-MiniLM-L6-v2.</param>
    /// <param name="vocabularyPath">The model's <c>vocab.txt</c>.</param>
    /// <param name="variant">A short name of the weights (for example <c>fp32</c>) that becomes part of <see cref="ModelId"/>.</param>
    /// <returns>The reranker.</returns>
    public static MiniLmReranker Open(string modelPath, string vocabularyPath, string variant)
    {
        ArgumentException.ThrowIfNullOrEmpty(variant);
        return new MiniLmReranker(CrossEncoderReranker.Open(modelPath, vocabularyPath, Options(variant)));
    }

    /// <inheritdoc />
    public void Score(string query, IReadOnlyList<string> passages, Span<float> scores, CancellationToken cancellationToken = default) =>
        _inner.Score(query, passages, scores, cancellationToken);

    /// <inheritdoc />
    public ValueTask ScoreAsync(string query, IReadOnlyList<string> passages, Memory<float> scores, CancellationToken cancellationToken = default) =>
        _inner.ScoreAsync(query, passages, scores, cancellationToken);

    /// <summary>Releases the model.</summary>
    public void Dispose() => _inner.Dispose();

    private static CrossEncoderOptions Options(string variant) => new() { ModelId = "ms-marco-MiniLM-L6-v2/" + variant };
}
