using System.Buffers.Binary;
using OneIndex.Kernels;

namespace OneIndex.Vectors;

/// <summary>
/// Turns text into one vector per token, for late-interaction (multi-vector) search as in ColBERT (Khattab and Zaharia, SIGIR 2020).
/// OneIndex defines only the interface; the application supplies the model.
/// </summary>
public interface IMultiVectorEncoder
{
    /// <summary>Names the model and its settings; stored with the vectors so a changed model can be noticed.</summary>
    string ModelId { get; }

    /// <summary>The length of every token vector.</summary>
    int Dimension { get; }

    /// <summary>Encodes a text into its token vectors.</summary>
    /// <param name="text">The text.</param>
    /// <param name="role">Whether the text is a document or a query (models often treat them differently).</param>
    /// <param name="cancellationToken">Cancels the encoding.</param>
    /// <returns>One vector of <see cref="Dimension"/> floats per token, in text order; may be empty.</returns>
    float[][] Encode(string text, EmbeddingRole role, CancellationToken cancellationToken = default);
}

/// <summary>
/// The stored form of a multi-vector value and MaxSim scoring. A multi-vector is kept in a <c>byte[]</c> (a Bytes field):
/// a little-endian <c>uint</c> token count, a <c>uint</c> dimension, then the vectors as little-endian floats.
/// </summary>
public static class MultiVector
{
    /// <summary>Packs token vectors for storage in a Bytes field.</summary>
    /// <param name="vectors">The token vectors, all of the same length, finite.</param>
    /// <returns>The packed bytes.</returns>
    /// <exception cref="ArgumentException">The vectors differ in length, are empty of dimensions, or hold a NaN or infinity.</exception>
    public static byte[] Pack(IReadOnlyList<float[]> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        int dim = vectors.Count == 0 ? 0 : vectors[0].Length;
        var bytes = new byte[8 + (vectors.Count * dim * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)vectors.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)dim);
        int at = 8;
        foreach (float[] v in vectors)
        {
            if (v.Length != dim)
            {
                throw new ArgumentException("All token vectors must have the same length.", nameof(vectors));
            }

            foreach (float x in v)
            {
                if (!float.IsFinite(x))
                {
                    throw new ArgumentException("A token vector holds a NaN or an infinity.", nameof(vectors));
                }

                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), x);
                at += 4;
            }
        }

        return bytes;
    }

    /// <summary>Reads packed token vectors.</summary>
    /// <param name="packed">Bytes written by <see cref="Pack"/>.</param>
    /// <param name="count">The number of token vectors.</param>
    /// <param name="dimension">The vector length.</param>
    /// <returns>The vectors, row after row.</returns>
    /// <exception cref="FormatException">The bytes are not a packed multi-vector.</exception>
    public static float[] Unpack(ReadOnlySpan<byte> packed, out int count, out int dimension)
    {
        if (packed.Length < 8)
        {
            throw new FormatException("A packed multi-vector needs an 8-byte header.");
        }

        uint c = BinaryPrimitives.ReadUInt32LittleEndian(packed);
        uint d = BinaryPrimitives.ReadUInt32LittleEndian(packed[4..]);
        if ((long)c * d * 4 != packed.Length - 8L)
        {
            throw new FormatException("The packed multi-vector's size does not match its header.");
        }

        count = (int)c;
        dimension = (int)d;
        var values = new float[(int)(c * d)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(packed[(8 + (i * 4))..]);
        }

        return values;
    }
}

/// <summary>A document and its MaxSim score.</summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="Hit">The hit from the first stage or the scan.</param>
/// <param name="Score">The MaxSim score: the sum over query tokens of the best similarity among the document's tokens.</param>
public readonly record struct MaxSimHit<T>(SearchHit<T> Hit, double Score)
    where T : class;

/// <summary>
/// Late-interaction scoring: <c>score(q, d) = sum over query tokens i of max over document tokens j of sim(q_i, d_j)</c>
/// (Khattab and Zaharia, "ColBERT: Efficient and Effective Passage Search via Contextualized Late Interaction over BERT", SIGIR 2020,
/// section 3.5, equation 3). The similarity is the dot product of vectors scaled to length 1 (cosine).
/// </summary>
public static class MaxSim
{
    /// <summary>Scores one document.</summary>
    /// <param name="query">Query token vectors, row after row.</param>
    /// <param name="document">Document token vectors, row after row.</param>
    /// <param name="dimension">The vector length.</param>
    /// <returns>The MaxSim score; 0 when either side has no tokens.</returns>
    /// <exception cref="ArgumentException">A span is not a whole number of rows.</exception>
    public static double Score(ReadOnlySpan<float> query, ReadOnlySpan<float> document, int dimension)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimension, 1);
        if (query.Length % dimension != 0 || document.Length % dimension != 0)
        {
            throw new ArgumentException("The vectors must be whole rows of the given dimension.");
        }

        int nq = query.Length / dimension;
        int nd = document.Length / dimension;
        double total = 0;
        for (int i = 0; i < nq; i++)
        {
            ReadOnlySpan<float> q = query.Slice(i * dimension, dimension);
            float best = float.NegativeInfinity;
            for (int j = 0; j < nd; j++)
            {
                float s = Cosine(q, document.Slice(j * dimension, dimension));
                if (s > best)
                {
                    best = s;
                }
            }

            if (nd > 0)
            {
                total += best;
            }
        }

        return total;
    }

    private static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float na = VectorKernels.SumSquares(a);
        float nb = VectorKernels.SumSquares(b);
        return na == 0 || nb == 0 ? 0 : VectorKernels.Dot(a, b) / MathF.Sqrt(na * nb);
    }

    /// <summary>
    /// Re-orders first-stage hits by MaxSim: the late-interaction rerank stage. The multi-vector of each document is read from the
    /// document (a Bytes property written with <see cref="MultiVector.Pack"/>). Documents without one score 0 and sort last.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="hits">The first-stage hits, for example <c>index.Search("text").Take(100).Execute()</c>.</param>
    /// <param name="multiVector">Reads a document's packed multi-vector.</param>
    /// <param name="queryVectors">The query's token vectors, from an <see cref="IMultiVectorEncoder"/>.</param>
    /// <param name="take">How many to return; null for all.</param>
    /// <returns>The hits, best MaxSim first; ties keep the first-stage order.</returns>
    public static List<MaxSimHit<T>> Rerank<T>(IEnumerable<SearchHit<T>> hits, Func<T, byte[]?> multiVector, IReadOnlyList<float[]> queryVectors, int? take = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(multiVector);
        ArgumentNullException.ThrowIfNull(queryVectors);
        int dim = queryVectors.Count == 0 ? 0 : queryVectors[0].Length;
        var flatQuery = new float[queryVectors.Count * dim];
        for (int i = 0; i < queryVectors.Count; i++)
        {
            if (queryVectors[i].Length != dim)
            {
                throw new ArgumentException("All query token vectors must have the same length.", nameof(queryVectors));
            }

            queryVectors[i].CopyTo(flatQuery, i * dim);
        }

        var scored = new List<(MaxSimHit<T> Hit, int Order)>();
        int order = 0;
        foreach (SearchHit<T> hit in hits)
        {
            double score = 0;
            if (dim > 0 && multiVector(hit.Document) is { } packed)
            {
                float[] doc = MultiVector.Unpack(packed, out _, out int docDim);
                if (docDim != dim && doc.Length > 0)
                {
                    throw new ArgumentException($"A document's token vectors have {docDim} values, the query's have {dim}.", nameof(multiVector));
                }

                score = Score(flatQuery, doc, dim);
            }

            scored.Add((new MaxSimHit<T>(hit, score), order++));
        }

        scored.Sort(static (a, b) => b.Hit.Score != a.Hit.Score ? b.Hit.Score.CompareTo(a.Hit.Score) : a.Order.CompareTo(b.Order));
        IEnumerable<MaxSimHit<T>> result = scored.Select(static s => s.Hit);
        return take is { } n ? [.. result.Take(n)] : [.. result];
    }

    /// <summary>
    /// Searches by MaxSim alone: scores every document that passes the optional filter and returns the best. This is an exact scan and
    /// reads at most <see cref="SearchQuery{T}.MaxResultWindow"/> candidates, so narrow large collections first with a filter or use
    /// <see cref="Rerank"/> on a first-stage search.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="candidates">The documents to scan, for example <c>index.Search().Where(...)</c>.</param>
    /// <param name="multiVector">Reads a document's packed multi-vector.</param>
    /// <param name="queryVectors">The query's token vectors.</param>
    /// <param name="k">How many to return.</param>
    /// <returns>The best <paramref name="k"/> documents, best first.</returns>
    public static List<MaxSimHit<T>> Search<T>(SearchQuery<T> candidates, Func<T, byte[]?> multiVector, IReadOnlyList<float[]> queryVectors, int k)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        SearchResults<T> all = candidates.Take(SearchQuery<T>.MaxResultWindow).Execute();
        return Rerank(all, multiVector, queryVectors, k);
    }
}
