using System.Buffers;
using System.Numerics;
using OneIndex.Models.Tokenization;
using OneIndex.Vectors;
using RecursiveAI.OneModel;
using RecursiveAI.OneModel.Graph;
using RecursiveAI.OneModel.Tensors;

namespace OneIndex.Models;

/// <summary>Settings of a <see cref="CrossEncoderReranker"/>.</summary>
public sealed class CrossEncoderOptions
{
    /// <summary>The identity reported as <see cref="IReranker.ModelId"/>; it must change whenever the weights or any setting that
    /// changes the scores change. Required.</summary>
    public required string ModelId { get; init; }

    /// <summary>The most tokens per pair, special tokens included; the longer text is cut first. 512 by default, the model's position limit.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>The most padded tokens (rows times the longest row) in one model run. 4096 by default.</summary>
    public int MaxBatchTokens { get; init; } = 4096;

    /// <summary>Exact arithmetic: identical bits on every machine and thread count, slower. <see langword="false"/> by default.</summary>
    public bool ExactArithmetic { get; init; }

    /// <summary>The most threads one model run uses; 0, the default, means one per core.</summary>
    public int MaxThreads { get; init; }

    /// <summary>The accent, case and CJK handling of the tokenizer; the uncased BERT defaults when <see langword="null"/>.</summary>
    public WordPieceOptions? Tokenizer { get; init; }
}

/// <summary>
/// A BERT cross-encoder: the query and a passage are tokenized as one pair (<c>[CLS] query [SEP] passage [SEP]</c>), run through a
/// transformer encoder by OneModel, and the single output logit is the relevance score. The raw logit is returned, not squashed by a
/// sigmoid, so scores keep their spread; the order is the same.
/// </summary>
/// <remarks>
/// <para>The model must have the inputs <c>input_ids</c>, <c>attention_mask</c> and <c>token_type_ids</c> (int64, shape [batch, tokens]) and one
/// output of shape [batch, 1], in ONNX or the OneModel format. Thread-safe.</para>
/// <para>Pairs are tokenized, sorted by length so that padding stays small, and run in batches of at most
/// <see cref="CrossEncoderOptions.MaxBatchTokens"/> padded tokens. A pair's score does not depend on how pairs are batched, apart from rounding in the last bits.</para>
/// </remarks>
public sealed class CrossEncoderReranker : IReranker, IDisposable
{
    private const int MaxRowsPerRun = 64;
    private const int PadMultiple = 32;
    private static readonly string[] InputNames = ["input_ids", "attention_mask", "token_type_ids"];

    private readonly Session _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly CrossEncoderOptions _options;
    private readonly string _outputName;

    private CrossEncoderReranker(Session session, WordPieceTokenizer tokenizer, CrossEncoderOptions options, string outputName)
    {
        _session = session;
        _tokenizer = tokenizer;
        _options = options;
        _outputName = outputName;
    }

    /// <inheritdoc />
    public string ModelId => _options.ModelId;

    /// <summary>The tokenizer, for callers that need token counts or ids.</summary>
    public WordPieceTokenizer Tokenizer => _tokenizer;

    /// <summary>The kernel family the model runs on (for example <c>Avx2</c> or <c>Neon</c>), as reported by OneModel.</summary>
    public string KernelPath => _session.KernelPath;

    /// <summary>Opens a cross-encoder from model and vocabulary files.</summary>
    /// <param name="modelPath">An ONNX or OneModel file.</param>
    /// <param name="vocabularyPath">The model's <c>vocab.txt</c>.</param>
    /// <param name="options">Settings; <see cref="CrossEncoderOptions.ModelId"/> is required.</param>
    /// <returns>The reranker; dispose it to release the model.</returns>
    /// <exception cref="FormatException">The vocabulary is malformed.</exception>
    /// <exception cref="InvalidOperationException">The model lacks the expected inputs or has no single-score output.</exception>
    /// <exception cref="RecursiveAI.OneModel.OneModelException">The model file is invalid or uses an unsupported operator.</exception>
    public static CrossEncoderReranker Open(string modelPath, string vocabularyPath, CrossEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        ArgumentNullException.ThrowIfNull(vocabularyPath);
        using FileStream model = File.OpenRead(modelPath);
        using FileStream vocabulary = File.OpenRead(vocabularyPath);
        return Open(model, vocabulary, options);
    }

    /// <summary>Opens a cross-encoder from streams.</summary>
    /// <param name="model">The model bytes (ONNX or OneModel format). Read to its end during the call.</param>
    /// <param name="vocabulary">The vocabulary text. Read to its end during the call.</param>
    /// <param name="options">Settings.</param>
    /// <returns>The reranker.</returns>
    /// <exception cref="FormatException">The vocabulary is malformed.</exception>
    /// <exception cref="InvalidOperationException">The model lacks the expected inputs or has no single-score output.</exception>
    /// <exception cref="RecursiveAI.OneModel.OneModelException">The model is invalid or uses an unsupported operator.</exception>
    public static CrossEncoderReranker Open(Stream model, Stream vocabulary, CrossEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ModelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTokens, 4);
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

            TensorInfo[] outputs = [.. session.Outputs];
            if (outputs.Length != 1)
            {
                throw new InvalidOperationException("A cross-encoder has one output, the relevance logit.");
            }

            TensorInfo output = outputs[0];
            if (!output.HasShape || output.Dims.Length != 2 || !output.Dims[1].IsFixed || output.Dims[1].Value != 1)
            {
                throw new InvalidOperationException("The model's output must have the shape [batch, 1].");
            }

            return new CrossEncoderReranker(session, tokenizer, options, output.Name);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Score(string query, IReadOnlyList<string> passages, Span<float> scores, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(passages);
        if (scores.Length != passages.Count)
        {
            throw new ArgumentException($"The scores must hold {passages.Count} floats.", nameof(scores));
        }

        if (passages.Count == 0)
        {
            return;
        }

        var rows = new int[passages.Count][];
        var segments = new int[passages.Count][];
        var order = new int[passages.Count];
        int[] idBuffer = ArrayPool<int>.Shared.Rent(_options.MaxTokens);
        int[] segmentBuffer = ArrayPool<int>.Shared.Rent(_options.MaxTokens);
        try
        {
            for (int i = 0; i < rows.Length; i++)
            {
                string passage = passages[i] ?? throw new ArgumentNullException(nameof(passages), $"Passage {i} is null.");
                int n = _tokenizer.EncodePair(query, passage, idBuffer.AsSpan(0, _options.MaxTokens), segmentBuffer);
                rows[i] = idBuffer.AsSpan(0, n).ToArray();
                segments[i] = segmentBuffer.AsSpan(0, n).ToArray();
                order[i] = i;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(idBuffer);
            ArrayPool<int>.Shared.Return(segmentBuffer);
        }

        // Longest first: the first run is the largest, and neighbours in the order have similar lengths, so padding is small.
        Array.Sort(order, (a, b) => rows[b].Length != rows[a].Length ? rows[b].Length.CompareTo(rows[a].Length) : a.CompareTo(b));
        int start = 0;
        while (start < order.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The padded length comes from the longest row (rounded up) and the row count from the token budget, so only a few
            // (rows, length) shapes occur and the model's plan cache stays small.
            int length = Math.Min((rows[order[start]].Length + PadMultiple - 1) / PadMultiple * PadMultiple, _options.MaxTokens);
            int count = Math.Clamp(_options.MaxBatchTokens / length, 1, MaxRowsPerRun);
            count = Math.Min(count, order.Length - start);
            int runRows = (int)BitOperations.RoundUpToPowerOf2((uint)count);
            if (runRows * length > _options.MaxBatchTokens)
            {
                runRows = count;
            }

            RunBatch(rows, segments, order.AsSpan(start, count), length, scores, runRows);
            start += count;
        }
    }

    /// <inheritdoc />
    public ValueTask ScoreAsync(string query, IReadOnlyList<string> passages, Memory<float> scores, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsBrowser())
        {
            // No threads in the browser: run to completion on the caller's turn; the result is the same.
            Score(query, passages, scores.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Task.Run(() => Score(query, passages, scores.Span, cancellationToken), cancellationToken));
    }

    /// <summary>Releases the model.</summary>
    public void Dispose() => _session.Dispose();

    private void RunBatch(int[][] rows, int[][] segments, ReadOnlySpan<int> batch, int length, Span<float> scores, int runRows)
    {
        // The run has runRows rows: the batch plus unused rows that round the count up to a power of two, so that the model sees few
        // distinct shapes. Their outputs are ignored.
        int n = runRows;
        long[] ids = ArrayPool<long>.Shared.Rent(n * length);
        long[] mask = ArrayPool<long>.Shared.Rent(n * length);
        long[] types = ArrayPool<long>.Shared.Rent(n * length);
        try
        {
            Span<long> idSpan = ids.AsSpan(0, n * length);
            Span<long> maskSpan = mask.AsSpan(0, n * length);
            Span<long> typeSpan = types.AsSpan(0, n * length);
            idSpan.Fill(_tokenizer.PadId);
            maskSpan.Clear();
            typeSpan.Clear();
            for (int r = 0; r < batch.Length; r++)
            {
                int[] row = rows[batch[r]];
                int[] segment = segments[batch[r]];
                for (int t = 0; t < row.Length; t++)
                {
                    idSpan[(r * length) + t] = row[t];
                    maskSpan[(r * length) + t] = 1;
                    typeSpan[(r * length) + t] = segment[t];
                }
            }

            // Unused rows hold [CLS] [SEP] only.
            for (int r = batch.Length; r < n; r++)
            {
                idSpan[r * length] = _tokenizer.ClassId;
                idSpan[(r * length) + 1] = _tokenizer.SeparatorId;
                maskSpan[r * length] = 1;
                maskSpan[(r * length) + 1] = 1;
            }

            Shape shape = Shape.Create(n, length);
            using Tensor idTensor = Tensor.FromSpan<long>(idSpan, shape);
            using Tensor maskTensor = Tensor.FromSpan<long>(maskSpan, shape);
            using Tensor typeTensor = Tensor.FromSpan<long>(typeSpan, shape);
            Span<Tensor> outputs = [default];
            _session.Run(InputNames, [idTensor, maskTensor, typeTensor], [_outputName], outputs);
            try
            {
                ReadOnlySpan<float> logits = outputs[0].Data<float>();
                for (int r = 0; r < batch.Length; r++)
                {
                    scores[batch[r]] = logits[r];
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
