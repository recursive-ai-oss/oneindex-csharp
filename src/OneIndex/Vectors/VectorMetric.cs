namespace OneIndex.Vectors;

/// <summary>How the closeness of two vectors is measured. Chosen per vector field and stored in the schema.</summary>
public enum VectorMetric : byte
{
    /// <summary>
    /// Cosine similarity, the default. Vectors are scaled to length 1 when they are indexed and the query is scaled the same way, so
    /// the comparison is a dot product. The stored source keeps the original vector. A vector of length 0 stays zero and is
    /// equally (un)similar to everything.
    /// </summary>
    Cosine = 0,

    /// <summary>
    /// The raw dot product (maximum inner product). Longer vectors score higher. Use it for embeddings the model already
    /// normalized when the cost of scaling should be skipped, or when length carries meaning.
    /// </summary>
    DotProduct = 1,

    /// <summary>Straight-line (Euclidean) distance; nearer is better.</summary>
    Euclidean = 2,
}

/// <summary>One nearest-neighbour result inside a segment.</summary>
/// <param name="Doc">The segment-local document id.</param>
/// <param name="Score">The similarity score, larger is better, between 0 and infinity for <see cref="VectorMetric.DotProduct"/> and between 0 and 1 otherwise (see <see cref="VectorSimilarity"/>).</param>
public readonly record struct VectorHit(int Doc, float Score);

/// <summary>Turns the distances the index works in into the similarity scores a search returns.</summary>
/// <remarks>
/// Scores are never negative, so they can be added to or fused with text scores. Cosine and Euclidean scores lie in [0, 1]; the
/// dot-product score is unbounded above. The formulas are the usual monotone maps: <c>(1 + cos) / 2</c>, <c>1 / (1 + d²)</c>, and
/// for the dot product <c>1 + dot</c> when it is not negative and <c>1 / (1 - dot)</c> when it is.
/// </remarks>
public static class VectorSimilarity
{
    /// <summary>Converts the internal distance (negated dot product, or squared Euclidean distance) to a score.</summary>
    /// <param name="metric">The field's metric.</param>
    /// <param name="distance">The internal distance; smaller is closer.</param>
    /// <returns>The score; larger is closer.</returns>
    public static float ToScore(VectorMetric metric, float distance)
    {
        switch (metric)
        {
            case VectorMetric.Euclidean:
                return 1f / (1f + distance);
            case VectorMetric.DotProduct:
                float dot = -distance;
                return dot >= 0f ? 1f + dot : 1f / (1f - dot);
            default:
                float cosine = -distance;
                return Math.Clamp((1f + cosine) * 0.5f, 0f, 1f);
        }
    }
}
