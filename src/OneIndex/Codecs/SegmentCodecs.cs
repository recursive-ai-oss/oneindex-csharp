using OneIndex.Index;

namespace OneIndex.Codecs;

/// <summary>The codecs a plain index uses.</summary>
public static class SegmentCodecs
{
    /// <summary>
    /// The default set: the text index, columns, the point index, stored source and vectors. Pass your own list to <see cref="IndexWriterOptions.Codecs"/>
    /// to leave parts out; a segment written without a codec simply has none of its parts.
    /// </summary>
    /// <returns>A new list of codec instances.</returns>
    public static IReadOnlyList<ISegmentCodec> CreateDefault() => [new TextSegmentCodec(), new ColumnSegmentCodec(), new PointSegmentCodec(), new StoredSegmentCodec(), new VectorSegmentCodec()];
}
