namespace OneIndex.Vectors;

/// <summary>
/// Scores how well passages answer a query, reading the two together (a cross-encoder). It is slower than comparing stored vectors, so a
/// search runs it only over the best hits of a first stage (<c>Rerank</c> on a search; docs/rerank.md).
/// </summary>
/// <remarks>
/// <para>Implementations are thread-safe. Scoring is deterministic for a given <see cref="ModelId"/>. Scores are only comparable within one
/// call (and between calls with the same query): they are relevance logits or similar, not probabilities, and they have no fixed range.
/// Higher is more relevant.</para>
/// <para>Scores are written in caller-provided memory, one per passage in input order; the destination must hold exactly
/// <c>passages.Count</c> floats. Pairs longer than the model's input limit are truncated, never rejected. The empty string is a valid
/// query or passage. A <see langword="null"/> argument throws <see cref="ArgumentNullException"/>.</para>
/// <para>It is the sibling of <see cref="IEmbedder"/>: same shape, same rules.</para>
/// </remarks>
public interface IReranker
{
    /// <summary>A stable identity of the model and every setting that changes its scores (weights, quantization, truncation length),
    /// for example <c>ms-marco-MiniLM-L6-v2/int8</c>. Two rerankers with the same identity score the same pairs the same. Never empty.</summary>
    string ModelId { get; }

    /// <summary>Scores each passage against the query.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="passages">The passages; none may be <see langword="null"/>. An empty list does nothing.</param>
    /// <param name="scores">Receives <c>passages.Count</c> scores.</param>
    /// <param name="cancellationToken">Observed between model runs; a cancelled call may leave the scores partly written.</param>
    /// <exception cref="ArgumentException"><paramref name="scores"/> is not exactly the required length.</exception>
    /// <exception cref="ArgumentNullException">The query or a passage is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    void Score(string query, IReadOnlyList<string> passages, Span<float> scores, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Score"/>. The model runs on a pooled thread where threads exist and cooperatively where they do not; results are identical.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="passages">The passages.</param>
    /// <param name="scores">Receives <c>passages.Count</c> scores; it must stay valid until the task completes.</param>
    /// <param name="cancellationToken">Observed between model runs.</param>
    /// <returns>A task that completes when the scores are written.</returns>
    ValueTask ScoreAsync(string query, IReadOnlyList<string> passages, Memory<float> scores, CancellationToken cancellationToken = default);
}

/// <summary>Convenience overloads for <see cref="IReranker"/>.</summary>
public static class RerankerExtensions
{
    /// <summary>Scores the passages and returns the scores as a new array.</summary>
    /// <param name="reranker">The reranker.</param>
    /// <param name="query">The query.</param>
    /// <param name="passages">The passages.</param>
    /// <param name="cancellationToken">Observed while the model runs.</param>
    /// <returns>One score per passage.</returns>
    public static float[] Score(this IReranker reranker, string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(passages);
        var scores = new float[passages.Count];
        reranker.Score(query, passages, scores, cancellationToken);
        return scores;
    }

    /// <summary>The asynchronous twin of <see cref="Score(IReranker, string, IReadOnlyList{string}, CancellationToken)"/>.</summary>
    /// <param name="reranker">The reranker.</param>
    /// <param name="query">The query.</param>
    /// <param name="passages">The passages.</param>
    /// <param name="cancellationToken">Observed while the model runs.</param>
    /// <returns>One score per passage.</returns>
    public static async ValueTask<float[]> ScoreAsync(this IReranker reranker, string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(passages);
        var scores = new float[passages.Count];
        await reranker.ScoreAsync(query, passages, scores, cancellationToken).ConfigureAwait(false);
        return scores;
    }
}

/// <summary>
/// The reranker that indexes open with when none is passed to them, set once at startup (or by a package such as
/// <c>RecursiveAI.OneIndex.Models</c> through its <c>UseAsDefault</c> method). There is no discovery, so nothing is set until someone calls this.
/// </summary>
public static class SearchRerankers
{
    private static IReranker? s_default;

    /// <summary>The reranker new indexes use, or <see langword="null"/> when none is set (<c>Rerank()</c> then throws and automatic reranking is off). Setting it does not affect indexes already open.</summary>
    public static IReranker? Default
    {
        get => Volatile.Read(ref s_default);
        set => Volatile.Write(ref s_default, value);
    }
}

/// <summary>An <see cref="IReranker"/> failed or returned something unusable while a search was reranking. The search has no results.</summary>
public sealed class RerankException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong, naming the model.</param>
    /// <param name="inner">The reranker's own exception, if any.</param>
    public RerankException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
