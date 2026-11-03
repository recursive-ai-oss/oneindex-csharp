using System.Buffers;
using System.Numerics;
using OneIndex.Models.Tokenization;
using OneIndex.Vectors;
using RecursiveAI.OneModel;
using RecursiveAI.OneModel.Graph;
using RecursiveAI.OneModel.Tensors;

namespace OneIndex.Models.Multilingual;

/// <summary>
/// A multilingual sentence embedder for the E5 family (multilingual-e5-small, MIT licence, 384 dimensions): SentencePiece tokens through
/// an XLM-RoBERTa encoder run by OneModel, the mean of the token vectors, scaled to unit length.
/// </summary>
/// <remarks>
/// <para>E5 models are trained with prefixes: queries get <c>query: </c> and documents <c>passage: </c> (the model card says to always
/// use them). Both are added here by role. Texts longer than 512 tokens are cut. The model needs the inputs <c>input_ids</c>,
/// <c>attention_mask</c> and <c>token_type_ids</c> (int64) and the output <c>last_hidden_state</c>. Thread-safe.</para>
/// <para>Token ids follow the model's own numbering: the sentence boundaries are 0 and 2, the padding id is 1, the unknown id is 3, and
/// every other SentencePiece piece id is shifted up by one.</para>
/// </remarks>
public sealed class MultilingualE5Embedder : IEmbedder, IDisposable
{
    /// <summary>The prefix of a query text.</summary>
    public const string QueryPrefix = "query: ";

    /// <summary>The prefix of a document text.</summary>
    public const string DocumentPrefix = "passage: ";

    private const int StartId = 0, PadId = 1, EndId = 2, UnknownId = 3;
    private const int MaxRowsPerRun = 64;
    private const int PadMultiple = 64;
    private static readonly string[] InputNames = ["input_ids", "attention_mask", "token_type_ids"];
    private static readonly string[] OutputNames = ["last_hidden_state"];

    private readonly Session _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly string _modelId;
    private readonly int _maxTokens;
    private readonly int _maxBatchTokens;

    private MultilingualE5Embedder(Session session, SentencePieceTokenizer tokenizer, string modelId, int dimension, int maxTokens, int maxBatchTokens)
    {
        _session = session;
        _tokenizer = tokenizer;
        _modelId = modelId;
        Dimension = dimension;
        _maxTokens = maxTokens;
        _maxBatchTokens = maxBatchTokens;
    }

    /// <inheritdoc />
    public string ModelId => _modelId;

    /// <inheritdoc />
    public int Dimension { get; }

    /// <inheritdoc />
    public bool IsNormalized => true;

    /// <summary>The kernel family the model runs on, as reported by OneModel.</summary>
    public string KernelPath => _session.KernelPath;

    /// <summary>Opens the embedder from model files.</summary>
    /// <param name="modelPath">An ONNX or OneModel file of multilingual-e5-small.</param>
    /// <param name="sentencePiecePath">The model's <c>sentencepiece.bpe.model</c>.</param>
    /// <param name="variant">A short name of the weights (for example <c>int8</c>) that becomes part of <see cref="ModelId"/>.</param>
    /// <param name="maxTokens">The most tokens per text, special tokens included. Default 512, the model's limit.</param>
    /// <param name="exactArithmetic">Exact arithmetic (identical bits on every machine, slower). Default <see langword="false"/>.</param>
    /// <returns>The embedder; dispose it to release the model.</returns>
    /// <exception cref="SentencePieceFormatException">The tokenizer model is malformed or unsupported.</exception>
    /// <exception cref="InvalidOperationException">The model lacks the expected inputs or output.</exception>
    /// <exception cref="RecursiveAI.OneModel.OneModelException">The model file is invalid or uses an unsupported operator.</exception>
    public static MultilingualE5Embedder Open(string modelPath, string sentencePiecePath, string variant, int maxTokens = 512, bool exactArithmetic = false)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        ArgumentNullException.ThrowIfNull(sentencePiecePath);
        using FileStream model = File.OpenRead(modelPath);
        return Open(model, File.ReadAllBytes(sentencePiecePath), variant, maxTokens, exactArithmetic);
    }

    /// <summary>Opens the embedder with the weights packed in this package (int8).</summary>
    /// <returns>The embedder; dispose it to release the model.</returns>
    /// <exception cref="FileNotFoundException">The assembly was built without the packed weights; use <see cref="Open(string, string, string, int, bool)"/> with model files.</exception>
    public static MultilingualE5Embedder Load()
    {
        System.Reflection.Assembly assembly = typeof(MultilingualE5Embedder).Assembly;
        using Stream model = assembly.GetManifestResourceStream("OneIndex.Models.Weights.multilingual-e5-small.onemodel")
            ?? throw new FileNotFoundException("This build of OneIndex.Models.Multilingual does not contain the multilingual-e5-small weights; use MultilingualE5Embedder.Open with model files.");
        using Stream spm = assembly.GetManifestResourceStream("OneIndex.Models.Weights.multilingual-e5-small.sentencepiece.bpe.model")
            ?? throw new FileNotFoundException("This build of OneIndex.Models.Multilingual does not contain the SentencePiece model.");
        using var bytes = new MemoryStream();
        spm.CopyTo(bytes);
        return Open(model, bytes.ToArray(), "int8");
    }

    private static MultilingualE5Embedder Open(Stream model, byte[] sentencePieceBytes, string variant, int maxTokens = 512, bool exactArithmetic = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(variant);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 3);
        SentencePieceTokenizer tokenizer = SentencePieceTokenizer.Create(SentencePieceModel.Load(sentencePieceBytes));
        var options = new ModelOptions { Mode = exactArithmetic ? ArithmeticMode.Exact : ArithmeticMode.Fast, MaxCachedPlans = 24 };
        Session session = Session.Load(model, options);
        try
        {
            foreach (string name in InputNames)
            {
                if (!session.Inputs.Any(i => i.Name == name))
                {
                    throw new InvalidOperationException($"The model has no input named '{name}'.");
                }
            }

            TensorInfo output = session.Outputs.FirstOrDefault(o => o.Name == OutputNames[0])
                ?? throw new InvalidOperationException($"The model has no output named '{OutputNames[0]}'.");
            if (!output.HasShape || output.Dims.Length != 3 || !output.Dims[2].IsFixed)
            {
                throw new InvalidOperationException("The model's output must have the shape [batch, tokens, dimension] with a fixed dimension.");
            }

            return new MultilingualE5Embedder(session, tokenizer, "multilingual-e5-small/" + variant, checked((int)output.Dims[2].Value), maxTokens, Math.Max(2048, maxTokens));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Token ids of a text as the model sees them: the start id, the pieces, the end id; cut to <paramref name="maxTokens"/>.</summary>
    /// <param name="text">The text, already prefixed if the caller wants the E5 prefix.</param>
    /// <param name="maxTokens">The most ids, boundaries included (at least 3).</param>
    /// <returns>The ids.</returns>
    public int[] Encode(string text, int maxTokens = 512) => Encode(_tokenizer, text, maxTokens);

    /// <summary>Token ids of a text as the model sees them, from a SentencePiece tokenizer built on the model's <c>sentencepiece.bpe.model</c>.</summary>
    /// <param name="tokenizer">The tokenizer.</param>
    /// <param name="text">The text, already prefixed if the caller wants the E5 prefix.</param>
    /// <param name="maxTokens">The most ids, boundaries included (at least 3).</param>
    /// <returns>The ids: the start id, the pieces (ids shifted by the model's numbering), the end id.</returns>
    public static int[] Encode(SentencePieceTokenizer tokenizer, string text, int maxTokens = 512)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 3);
        var pieces = new List<int>();
        tokenizer.Encode(text, pieces);
        int n = Math.Min(pieces.Count, maxTokens - 2);
        int[] ids = new int[n + 2];
        ids[0] = StartId;
        for (int i = 0; i < n; i++)
        {
            int p = pieces[i];
            ids[i + 1] = p switch { 0 => UnknownId, 1 => StartId, 2 => EndId, _ => p + 1 };
        }

        ids[n + 1] = EndId;
        return ids;
    }

    /// <inheritdoc />
    public void Embed(IReadOnlyList<string> texts, EmbeddingRole role, Span<float> destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (destination.Length != checked(texts.Count * Dimension))
        {
            throw new ArgumentException($"The destination must hold {texts.Count} x {Dimension} floats.", nameof(destination));
        }

        if (texts.Count == 0)
        {
            return;
        }

        string prefix = role == EmbeddingRole.Query ? QueryPrefix : DocumentPrefix;
        var rows = new int[texts.Count][];
        var order = new int[texts.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            string text = texts[i] ?? throw new ArgumentNullException(nameof(texts), $"Text {i} is null.");
            rows[i] = Encode(prefix + text, _maxTokens);
            order[i] = i;
        }

        Array.Sort(order, (a, b) => rows[b].Length != rows[a].Length ? rows[b].Length.CompareTo(rows[a].Length) : a.CompareTo(b));
        int start = 0;
        while (start < order.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min((rows[order[start]].Length + PadMultiple - 1) / PadMultiple * PadMultiple, _maxTokens);
            int count = Math.Clamp(_maxBatchTokens / length, 1, MaxRowsPerRun);
            count = Math.Min(count, order.Length - start);
            int runRows = (int)BitOperations.RoundUpToPowerOf2((uint)count);
            if (runRows * length > _maxBatchTokens)
            {
                runRows = count;
            }

            RunBatch(rows, order.AsSpan(start, count), length, destination, runRows);
            start += count;
        }
    }

    /// <inheritdoc />
    public ValueTask EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<float> destination, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsBrowser())
        {
            Embed(texts, role, destination.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Task.Run(() => Embed(texts, role, destination.Span, cancellationToken), cancellationToken));
    }

    /// <summary>Releases the model.</summary>
    public void Dispose() => _session.Dispose();

    private void RunBatch(int[][] rows, ReadOnlySpan<int> batch, int length, Span<float> destination, int runRows)
    {
        int padded = length;
        int n = runRows;
        long[] ids = ArrayPool<long>.Shared.Rent(n * padded);
        long[] mask = ArrayPool<long>.Shared.Rent(n * padded);
        long[] types = ArrayPool<long>.Shared.Rent(n * padded);
        try
        {
            Span<long> idSpan = ids.AsSpan(0, n * padded);
            Span<long> maskSpan = mask.AsSpan(0, n * padded);
            idSpan.Fill(PadId);
            maskSpan.Clear();
            types.AsSpan(0, n * padded).Clear();
            for (int r = 0; r < n; r++)
            {
                int[] row = r < batch.Length ? rows[batch[r]] : [StartId, EndId];
                for (int t = 0; t < row.Length; t++)
                {
                    idSpan[(r * padded) + t] = row[t];
                    maskSpan[(r * padded) + t] = 1;
                }
            }

            Shape shape = Shape.Create(n, padded);
            using Tensor idTensor = Tensor.FromSpan<long>(idSpan, shape);
            using Tensor maskTensor = Tensor.FromSpan<long>(maskSpan, shape);
            using Tensor typeTensor = Tensor.FromSpan<long>(types.AsSpan(0, n * padded), shape);
            Span<Tensor> outputs = [default];
            _session.Run(InputNames, [idTensor, maskTensor, typeTensor], OutputNames, outputs);
            try
            {
                ReadOnlySpan<float> hidden = outputs[0].Data<float>();
                int d = Dimension;
                for (int r = 0; r < batch.Length; r++)
                {
                    // Mean pooling over the real tokens (the model card's pooling), then L2 normalization.
                    int tokens = rows[batch[r]].Length;
                    Span<float> target = destination.Slice(batch[r] * d, d);
                    target.Clear();
                    for (int t = 0; t < tokens; t++)
                    {
                        ReadOnlySpan<float> v = hidden.Slice(((r * padded) + t) * d, d);
                        for (int i = 0; i < d; i++)
                        {
                            target[i] += v[i];
                        }
                    }

                    double sum = 0;
                    for (int i = 0; i < d; i++)
                    {
                        target[i] /= tokens;
                        sum += (double)target[i] * target[i];
                    }

                    float scale = (float)(1.0 / Math.Max(Math.Sqrt(sum), 1e-12));
                    for (int i = 0; i < d; i++)
                    {
                        target[i] *= scale;
                    }
                }
            }
            finally
            {
                outputs[0].Dispose();
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(ids);
            ArrayPool<long>.Shared.Return(mask);
            ArrayPool<long>.Shared.Return(types);
        }
    }
}
