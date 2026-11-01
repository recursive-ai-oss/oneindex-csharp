using System.Buffers;
using System.Numerics;
using OneIndex.Models.Tokenization;
using OneIndex.Vectors;
using RecursiveAI.OneModel;
using RecursiveAI.OneModel.Graph;
using RecursiveAI.OneModel.Tensors;

namespace OneIndex.Models;

/// <summary>Settings of a <see cref="BertEmbedder"/>.</summary>
public sealed class BertEmbedderOptions
{
    /// <summary>The identity reported as <see cref="IEmbedder.ModelId"/>; it must change whenever the weights or any setting that
    /// changes the output change. Required.</summary>
    public required string ModelId { get; init; }

    /// <summary>Text put in front of every query before tokenizing (documents get none). Empty by default. The BGE models document
    /// <c>Represent this sentence for searching relevant passages: </c> for short queries searching passages.</summary>
    public string QueryPrefix { get; init; } = "";

    /// <summary>The most tokens per text, special tokens included; longer texts are cut. 512 by default, the model's position limit.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>The most padded tokens (rows times the longest row) in one model run. 2048 by default. Bigger runs use more memory
    /// and keep more cores busy.</summary>
    public int MaxBatchTokens { get; init; } = 2048;

    /// <summary>Exact arithmetic: identical bits on every machine and thread count, slower. <see langword="false"/> by default
    /// (the fast kernels, whose results may differ in the last bits between machines).</summary>
    public bool ExactArithmetic { get; init; }

    /// <summary>The most threads one model run uses; 0, the default, means one per core.</summary>
    public int MaxThreads { get; init; }

    /// <summary>The accent, case and CJK handling of the tokenizer; the uncased BERT defaults when <see langword="null"/>.</summary>
    public WordPieceOptions? Tokenizer { get; init; }
}

/// <summary>
/// A BERT-family sentence embedder: WordPiece tokens through a transformer encoder run by OneModel, the vector of the first
/// ([CLS]) token, scaled to unit length. This is the pooling and normalization of the BGE models.
/// </summary>
/// <remarks>
/// <para>The model must be an encoder with the inputs <c>input_ids</c>, <c>attention_mask</c> and <c>token_type_ids</c> (all int64,
/// shape [batch, tokens]) and an output <c>last_hidden_state</c> of shape [batch, tokens, dimension], in ONNX or the OneModel
/// format. Thread-safe; concurrent calls share the model's thread pool.</para>
/// <para>Texts are tokenized, sorted by length so that padding stays small, and run in batches of at most
/// <see cref="BertEmbedderOptions.MaxBatchTokens"/> padded tokens. Results do not depend on how texts are batched, apart from
/// rounding in the last bits.</para>
/// </remarks>
public sealed class BertEmbedder : IEmbedder, IDisposable
{
    private const int MaxRowsPerRun = 64;
    private const int PadMultiple = 64;
    private static readonly string[] InputNames = ["input_ids", "attention_mask", "token_type_ids"];
    private static readonly string[] OutputNames = ["last_hidden_state"];

    private readonly Session _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly BertEmbedderOptions _options;

    private BertEmbedder(Session session, WordPieceTokenizer tokenizer, BertEmbedderOptions options, int dimension)
    {
        _session = session;
        _tokenizer = tokenizer;
        _options = options;
        Dimension = dimension;
    }

    /// <inheritdoc />
    public string ModelId => _options.ModelId;

    /// <inheritdoc />
    public int Dimension { get; }

    /// <inheritdoc />
    public bool IsNormalized => true;

    /// <summary>The tokenizer, for callers that need token counts or ids.</summary>
    public WordPieceTokenizer Tokenizer => _tokenizer;

    /// <summary>The kernel family the model runs on (for example <c>Avx2</c> or <c>Neon</c>), as reported by OneModel.</summary>
    public string KernelPath => _session.KernelPath;

    /// <summary>Opens an embedder from model and vocabulary files.</summary>
    /// <param name="modelPath">An ONNX or OneModel file.</param>
    /// <param name="vocabularyPath">The model's <c>vocab.txt</c>.</param>
    /// <param name="options">Settings; <see cref="BertEmbedderOptions.ModelId"/> is required.</param>
    /// <returns>The embedder; dispose it to release the model.</returns>
    /// <exception cref="FormatException">The vocabulary is malformed.</exception>
    /// <exception cref="InvalidOperationException">The model lacks the expected inputs or output.</exception>
    /// <exception cref="RecursiveAI.OneModel.OneModelException">The model file is invalid or uses an unsupported operator.</exception>
    public static BertEmbedder Open(string modelPath, string vocabularyPath, BertEmbedderOptions options)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        ArgumentNullException.ThrowIfNull(vocabularyPath);
        using FileStream model = File.OpenRead(modelPath);
        using FileStream vocabulary = File.OpenRead(vocabularyPath);
        return Open(model, vocabulary, options);
    }

    /// <summary>Opens an embedder from streams.</summary>
    /// <param name="model">The model bytes (ONNX or OneModel format). Read to its end during the call.</param>
    /// <param name="vocabulary">The vocabulary text. Read to its end during the call.</param>
    /// <param name="options">Settings.</param>
    /// <returns>The embedder.</returns>
    /// <exception cref="FormatException">The vocabulary is malformed.</exception>
    /// <exception cref="InvalidOperationException">The model lacks the expected inputs or output.</exception>
    /// <exception cref="RecursiveAI.OneModel.OneModelException">The model is invalid or uses an unsupported operator.</exception>
    public static BertEmbedder Open(Stream model, Stream vocabulary, BertEmbedderOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ModelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTokens, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBatchTokens, options.MaxTokens);
        var tokenizer = new WordPieceTokenizer(WordPieceVocabulary.Load(vocabulary), options.Tokenizer, UnicodeDecomposer.Instance);
        var modelOptions = new ModelOptions
        {
            Mode = options.ExactArithmetic ? ArithmeticMode.Exact : ArithmeticMode.Fast,
            IntraOpThreads = options.MaxThreads,
            MaxCachedPlans = 24,
        };
        Session session = Session.Load(model, modelOptions);
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

            int dimension = checked((int)output.Dims[2].Value);

            return new BertEmbedder(session, tokenizer, options, dimension);
        }
        catch
        {
            session.Dispose();
            throw;
        }
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

        string prefix = role == EmbeddingRole.Query ? _options.QueryPrefix : "";
        var rows = new int[texts.Count][];
        var order = new int[texts.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            string text = texts[i] ?? throw new ArgumentNullException(nameof(texts), $"Text {i} is null.");
            rows[i] = _tokenizer.Encode(prefix.Length == 0 ? text : prefix + text, _options.MaxTokens);
            order[i] = i;
        }

        // Longest first: the first run is the largest, and neighbours in the order have similar lengths, so padding is small.
        Array.Sort(order, (a, b) => rows[b].Length != rows[a].Length ? rows[b].Length.CompareTo(rows[a].Length) : a.CompareTo(b));
        int start = 0;
        while (start < order.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The padded length is fixed by the longest row of the run (rounded up), and the row count by the token budget, so
            // that only a few (rows, length) shapes ever occur and the model's plan cache stays small.
            int length = Math.Min((rows[order[start]].Length + PadMultiple - 1) / PadMultiple * PadMultiple, _options.MaxTokens);
            int count = Math.Clamp(_options.MaxBatchTokens / length, 1, MaxRowsPerRun);
            count = Math.Min(count, order.Length - start);
            int runRows = (int)BitOperations.RoundUpToPowerOf2((uint)count);
            if (runRows * length > _options.MaxBatchTokens)
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
            // No threads in the browser: run to completion on the caller's turn; the result is the same.
            Embed(texts, role, destination.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Task.Run(() => Embed(texts, role, destination.Span, cancellationToken), cancellationToken));
    }

    /// <summary>Releases the model.</summary>
    public void Dispose() => _session.Dispose();

    private void RunBatch(int[][] rows, ReadOnlySpan<int> batch, int length, Span<float> destination, int runRows)
    {
        // The run has runRows rows: the batch plus unused rows ([CLS] [SEP] only) that round the count up to a power of two, so that
        // the model sees few distinct shapes. Their outputs are ignored.
        int padded = length;
        int n = runRows;
        long[] ids = ArrayPool<long>.Shared.Rent(n * padded);
        long[] mask = ArrayPool<long>.Shared.Rent(n * padded);
        long[] types = ArrayPool<long>.Shared.Rent(n * padded);
        try
        {
            Span<long> idSpan = ids.AsSpan(0, n * padded);
            Span<long> maskSpan = mask.AsSpan(0, n * padded);
            idSpan.Fill(_tokenizer.PadId);
            maskSpan.Clear();
            types.AsSpan(0, n * padded).Clear();
            for (int r = 0; r < n; r++)
            {
                int[] row = r < batch.Length ? rows[batch[r]] : [_tokenizer.ClassId, _tokenizer.SeparatorId];
                for (int t = 0; t < row.Length; t++)
                {
                    idSpan[(r * padded) + t] = row[t];
                    maskSpan[(r * padded) + t] = 1;
                }

                // Padding positions keep id 0, the [PAD] id of BERT vocabularies, and mask 0.
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
                    // CLS pooling: the first token's hidden state; then L2 normalization with the 1e-12 floor that the
                    // reference sentence-embedding pipeline uses.
                    ReadOnlySpan<float> cls = hidden.Slice(r * padded * d, d);
                    Span<float> target = destination.Slice(batch[r] * d, d);
                    double sum = 0;
                    for (int i = 0; i < d; i++)
                    {
                        sum += (double)cls[i] * cls[i];
                    }

                    float scale = (float)(1.0 / Math.Max(Math.Sqrt(sum), 1e-12));
                    for (int i = 0; i < d; i++)
                    {
                        target[i] = cls[i] * scale;
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
