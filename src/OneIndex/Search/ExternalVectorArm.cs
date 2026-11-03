namespace OneIndex;

/// <summary>A source of nearest-neighbour candidates that is not the segments' vector parts (the IVF lists of a static index).</summary>
internal interface IExternalVectorArm
{
    /// <summary>Whether this source answers for the vector field.</summary>
    bool Handles(string field);

    /// <summary>The best <paramref name="count"/> documents for the query vector, nearest first, as global document ids with scores.</summary>
    IReadOnlyList<(int Doc, float Score)> Nearest(string field, float[] vector, int count);
}
