namespace OneIndex.Vectors;

/// <summary>The structure a segment builds over the vectors of a field to answer nearest-neighbour searches without comparing with every vector.</summary>
public enum VectorIndexKind
{
    /// <summary>A hierarchical navigable small-world graph (docs/vectors.md). The default: high recall at low latency for any query.</summary>
    Hnsw = 0,

    /// <summary>
    /// A clustered inverted file (docs/ivf.md): vectors grouped around k-means centres, a search reads only the lists whose centres are nearest.
    /// Builds faster and reads contiguous ranges (good for storage that is slow to read at random), but needs more lists probed than a graph needs
    /// hops to reach the same recall.
    /// </summary>
    Ivf = 1,
}
