using OneIndex.Index;
using OneIndex.Vectors;

namespace OneIndex.Search;

/// <summary>
/// One vector field of one segment, opened for reading: full-precision vectors by document and approximate or exact nearest-neighbour
/// search over them. Safe to use from several threads.
/// </summary>
public interface IVectorFieldReader
{
    /// <summary>The vector length.</summary>
    int Dimensions { get; }

    /// <summary>How closeness is measured.</summary>
    VectorMetric Metric { get; }

    /// <summary>The number of documents of the segment that have a vector (deleted ones included).</summary>
    int Count { get; }

    /// <summary>Whether the segment has a graph for this field. Segments smaller than the field's flat threshold have none and are searched exactly.</summary>
    bool HasGraph { get; }

    /// <summary>
    /// Copies the stored vector of a document. For a cosine field this is the vector scaled to length 1, as the index holds it; the
    /// original is in the stored source.
    /// </summary>
    /// <param name="doc">The segment-local document id.</param>
    /// <param name="destination">At least <see cref="Dimensions"/> long.</param>
    /// <returns><see langword="false"/> if the document has no vector.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    bool TryGetVector(int doc, Span<float> destination);

    /// <summary>Finds the nearest documents to a query vector.</summary>
    /// <param name="query">The query vector, <see cref="Dimensions"/> long and finite. It is scaled to length 1 first when the metric is cosine.</param>
    /// <param name="k">How many hits to return.</param>
    /// <param name="ef">The breadth of the graph search (the candidate list size), at least <paramref name="k"/>; 0 uses the default for <paramref name="k"/>. Ignored for exact search.</param>
    /// <param name="live">Which documents are not deleted; <see langword="null"/> means all.</param>
    /// <param name="acceptedDocs">A bit set over the segment's document ids (bit <c>d</c> of word <c>d / 64</c>) of the documents allowed in the result, or <see langword="null"/> for all. A selective filter switches the search to an exact scan of the accepted documents.</param>
    /// <param name="exact"><see langword="true"/> to compare against every accepted vector instead of walking the graph.</param>
    /// <param name="hits">Receives the hits, best first; ties break towards the smaller document id. At least <paramref name="k"/> long.</param>
    /// <returns>How many hits were written.</returns>
    /// <exception cref="ArgumentException">The query has the wrong length or holds a NaN or infinity.</exception>
    int Search(ReadOnlySpan<float> query, int k, int ef, ILiveDocs? live, ulong[]? acceptedDocs, bool exact, Span<VectorHit> hits);
}

/// <summary>The vector part of a segment. Implemented by the vector codec's part reader.</summary>
public interface IVectorSource
{
    /// <summary>Gets a field's vectors.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The reader, or <see langword="null"/> when the segment has no vectors for the field.</returns>
    IVectorFieldReader? GetVectors(string field);
}
