using System.Buffers;
using System.Text.Json;
using OneIndex.Index;
using OneIndex.Models.Tokenization;
using OneIndex.Vectors;
using RecursiveAI.OneModel;
using RecursiveAI.OneModel.Graph;
using RecursiveAI.OneModel.Tensors;

namespace OneIndex.Models;

/// <summary>Settings of a <see cref="NeuralSparseEncoder"/>.</summary>
public sealed class NeuralSparseEncoderOptions
{
    /// <summary>The identity reported as <see cref="ISparseEncoder.ModelId"/>; it must change whenever the weights, the query weight table
    /// or any setting that changes the output change. Required.</summary>
    public required string ModelId { get; init; }

    /// <summary>The most tokens per document text, special tokens included; longer texts are cut. 512 by default, the model's position limit.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>The most padded tokens (rows times the longest row) in one model run. 1024 by default; every row carries a vocabulary-sized result.</summary>
    public int MaxBatchTokens { get; init; } = 1024;

    /// <summary>Weights below this are dropped from a document vector. 0 keeps everything the model outputs above zero.</summary>
    public float MinWeight { get; init; }

    /// <summary>How many times <c>log(1 + w)</c> is applied to the pooled document weights after the model's own activation. 0 by default.
    /// The published sentence-transformers pipeline of this model family was observed to apply the activation twice (docs/sparse.md).</summary>
    public int ExtraLog1p { get; init; }

    /// <summary>Exact arithmetic (identical bits on every machine), slower. <see langword="false"/> by default.</summary>
    public bool ExactArithmetic { get; init; }

    /// <summary>The most threads one model run uses; 0, the default, means one per core.</summary>
    public int MaxThreads { get; init; }
}

/// <summary>
/// An inference-free learned sparse encoder in the style of the OpenSearch neural sparse "doc" models: documents run through a masked
/// language model whose output is max-pooled over the tokens as <c>log(1 + relu(logit))</c> per vocabulary entry (SPLADE pooling,
/// Formal et al., SIGIR 2021 and arXiv:2109.10086), while a query is only tokenized and each token takes its weight from a published
/// table, so searching never runs the model.
/// </summary>
/// <remarks>
/// <para>The model files are supplied by the caller: a OneModel (or ONNX) file whose inputs are <c>input_ids</c> and <c>attention_mask</c>
/// (int64, [batch, tokens]) and whose output <c>sparse</c> is float [batch, vocabulary] holding the pooled weights, the vocabulary
/// (<c>vocab.txt</c>) and the query weight table (<c>idf.json</c>, token to weight). <c>tools/models/prepare_neural_sparse.py</c> builds
/// them from the published checkpoint. Thread-safe.</para>
/// </remarks>
public sealed class NeuralSparseEncoder : ISparseEncoder, IDisposable
{
    private const int MaxRowsPerRun = 16;
    private const int PadMultiple = 32;
    private static readonly string[] InputNames = ["input_ids", "attention_mask"];
    private static readonly string[] OutputNames = ["sparse"];

    private readonly Session _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly NeuralSparseEncoderOptions _options;
    private readonly float[] _queryWeights;
    private readonly string[] _tokens;

    private NeuralSparseEncoder(Session session, WordPieceTokenizer tokenizer, NeuralSparseEncoderOptions options, float[] queryWeights)
    {
        _session = session;
        _tokenizer = tokenizer;
        _options = options;
        _queryWeights = queryWeights;
        _tokens = new string[tokenizer.Vocabulary.Count];
        for (int i = 0; i < _tokens.Length; i++)
        {
            _tokens[i] = tokenizer.Vocabulary[i];
        }
    }

    /// <inheritdoc />
    public string ModelId => _options.ModelId;

    /// <summary>The tokenizer, for callers that need token ids.</summary>
    public WordPieceTokenizer Tokenizer => _tokenizer;

    /// <summary>Opens an encoder from a directory holding <c>model.onemodel</c> (or <c>model.onnx</c>), <c>vocab.txt</c> and <c>idf.json</c>.</summary>
    /// <param name="directory">The directory.</param>
    /// <param name="options">Settings; <see cref="NeuralSparseEncoderOptions.ModelId"/> is required.</param>
    /// <returns>The encoder; dispose it to release the model.</returns>
    /// <exception cref="FileNotFoundException">A file is missing.</exception>
    public static NeuralSparseEncoder OpenDirectory(string directory, NeuralSparseEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string model = Path.Combine(directory, "model.onemodel");
        if (!File.Exists(model))
        {
            model = Path.Combine(directory, "model.onnx");
        }

        return Open(model, Path.Combine(directory, "vocab.txt"), Path.Combine(directory, "idf.json"), options);
    }

    /// <summary>Opens an encoder from files.</summary>
    /// <param name="modelPath">A OneModel or ONNX file with the inputs and output described on the class.</param>
    /// <param name="vocabularyPath">The model's <c>vocab.txt</c>.</param>
    /// <param name="queryWeightsPath">A JSON object from token to query weight.</param>
    /// <param name="options">Settings.</param>
    /// <returns>The encoder.</returns>
    /// <exception cref="FormatException">The vocabulary or the weight table is malformed.</exception>
    /// <exception cref="InvalidOperationException">The model lacks the expected inputs or output.</exception>
    public static NeuralSparseEncoder Open(string modelPath, string vocabularyPath, string queryWeightsPath, NeuralSparseEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        ArgumentNullException.ThrowIfNull(vocabularyPath);
        ArgumentNullException.ThrowIfNull(queryWeightsPath);
        using FileStream model = File.OpenRead(modelPath);
        using FileStream vocabulary = File.OpenRead(vocabularyPath);
        using FileStream weights = File.OpenRead(queryWeightsPath);
        return Open(model, vocabulary, weights, options);
    }

    /// <summary>Opens an encoder from streams, each read to its end during the call.</summary>
    /// <param name="model">The model bytes (OneModel or ONNX).</param>
    /// <param name="vocabulary">The vocabulary text.</param>
    /// <param name="queryWeights">The query weight table (JSON object from token to weight).</param>
    /// <param name="options">Settings.</param>
    /// <returns>The encoder.</returns>
    /// <exception cref="FormatException">The vocabulary or the weight table is malformed.</exception>
    /// <exception cref="InvalidOperationException">The model lacks the expected inputs or output.</exception>
    public static NeuralSparseEncoder Open(Stream model, Stream vocabulary, Stream queryWeights, NeuralSparseEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(queryWeights);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ModelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTokens, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBatchTokens, options.MaxTokens);
        var tokenizer = new WordPieceTokenizer(WordPieceVocabulary.Load(vocabulary), null, UnicodeDecomposer.Instance);
        using var table = new MemoryStream();
        queryWeights.CopyTo(table);
        float[] weights = LoadWeights(table.ToArray(), tokenizer.Vocabulary);
        Session session = Session.Load(model, new ModelOptions
        {
            Mode = options.ExactArithmetic ? ArithmeticMode.Exact : ArithmeticMode.Fast,
            IntraOpThreads = options.MaxThreads,
            MaxCachedPlans = 24,
        });
        try
        {
            foreach (string name in InputNames)
            {
                if (!session.Inputs.Any(i => i.Name == name))
                {
                    throw new InvalidOperationException($"The model has no input named '{name}'.");
                }
            }

            if (!session.Outputs.Any(o => o.Name == OutputNames[0]))
            {
                throw new InvalidOperationException($"The model has no output named '{OutputNames[0]}'.");
            }

            return new NeuralSparseEncoder(session, tokenizer, options, weights);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Encode(IReadOnlyList<string> texts, EmbeddingRole role, Span<SparseVector> destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (destination.Length != texts.Count)
        {
            throw new ArgumentException($"The destination must hold {texts.Count} vectors.", nameof(destination));
        }

        if (role == EmbeddingRole.Query)
        {
            for (int i = 0; i < texts.Count; i++)
            {
                destination[i] = EncodeQuery(texts[i] ?? throw new ArgumentNullException(nameof(texts), $"Text {i} is null."));
            }

            return;
        }

        var rows = new int[texts.Count][];
        var order = new int[texts.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = _tokenizer.Encode(texts[i] ?? throw new ArgumentNullException(nameof(texts), $"Text {i} is null."), _options.MaxTokens);
            order[i] = i;
        }

        Array.Sort(order, (a, b) => rows[b].Length != rows[a].Length ? rows[b].Length.CompareTo(rows[a].Length) : a.CompareTo(b));
        int start = 0;
        while (start < order.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min((rows[order[start]].Length + PadMultiple - 1) / PadMultiple * PadMultiple, _options.MaxTokens);
            int count = Math.Min(Math.Clamp(_options.MaxBatchTokens / length, 1, MaxRowsPerRun), order.Length - start);
            RunBatch(rows, order.AsSpan(start, count), length, destination);
            start += count;
        }
    }

    /// <inheritdoc />
    public ValueTask EncodeAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<SparseVector> destination, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsBrowser() || role == EmbeddingRole.Query)
        {
            // No model runs for a query, and none can run on a thread in the browser: complete on the caller's turn with the same result.
            Encode(texts, role, destination.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Task.Run(() => Encode(texts, role, destination.Span, cancellationToken), cancellationToken));
    }

    /// <summary>Releases the model.</summary>
    public void Dispose() => _session.Dispose();

    // Query side: tokenize without [CLS]/[SEP], and give each distinct token its table weight (the model card: "for queries, it just use a
    // tokenizer and a weight look-up table"). A special token written in the text keeps its table weight, as the reference does.
    private SparseVector EncodeQuery(string text)
    {
        int[] ids = _tokenizer.Encode(text, _options.MaxTokens);
        var entries = new List<KeyValuePair<string, float>>(ids.Length);
        foreach (int id in ids)
        {
            float w = _queryWeights[id];
            if (w > 0f)
            {
                entries.Add(new(_tokens[id], w));
            }
        }

        return new SparseVector(entries);
    }

    private void RunBatch(int[][] rows, ReadOnlySpan<int> batch, int padded, Span<SparseVector> destination)
    {
        int n = batch.Length;
        long[] ids = ArrayPool<long>.Shared.Rent(n * padded);
        long[] mask = ArrayPool<long>.Shared.Rent(n * padded);
        try
        {
            Span<long> idSpan = ids.AsSpan(0, n * padded);
            Span<long> maskSpan = mask.AsSpan(0, n * padded);
            idSpan.Fill(_tokenizer.PadId);
            maskSpan.Clear();
            for (int r = 0; r < n; r++)
            {
                int[] row = rows[batch[r]];
                for (int t = 0; t < row.Length; t++)
                {
                    idSpan[(r * padded) + t] = row[t];
                    maskSpan[(r * padded) + t] = 1;
                }
            }

            Shape shape = Shape.Create(n, padded);
            using Tensor idTensor = Tensor.FromSpan<long>(idSpan, shape);
            using Tensor maskTensor = Tensor.FromSpan<long>(maskSpan, shape);
            Span<Tensor> outputs = [default];
            _session.Run(InputNames, [idTensor, maskTensor], OutputNames, outputs);
            try
            {
                ReadOnlySpan<float> pooled = outputs[0].Data<float>();
                int v = _tokens.Length;
                for (int r = 0; r < n; r++)
                {
                    ReadOnlySpan<float> row = pooled.Slice(r * v, v);
                    var entries = new List<KeyValuePair<string, float>>();
                    for (int i = 0; i < v; i++)
                    {
                        float w = row[i];
                        for (int e = 0; e < _options.ExtraLog1p && w > 0f; e++)
                        {
                            w = MathF.Log(1f + w);
                        }

                        if (w > _options.MinWeight)
                        {
                            entries.Add(new(_tokens[i], w));
                        }
                    }

                    destination[batch[r]] = new SparseVector(entries);
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
        }
    }

    private static float[] LoadWeights(byte[] json, WordPieceVocabulary vocabulary)
    {
        var weights = new float[vocabulary.Count];
        var reader = new Utf8JsonReader(json);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new FormatException("The query weight table must be a JSON object.");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string token = reader.GetString()!;
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                {
                    throw new FormatException($"The weight of '{token}' is not a number.");
                }

                float w = reader.GetSingle();
                if (!float.IsFinite(w) || w < 0f)
                {
                    throw new FormatException($"The weight of '{token}' must be finite and not negative.");
                }

                if (vocabulary.TryGetId(token, out int id))
                {
                    weights[id] = w;
                }
            }
        }
        catch (JsonException e)
        {
            throw new FormatException("The query weight table is not valid JSON.", e);
        }

        return weights;
    }
}
