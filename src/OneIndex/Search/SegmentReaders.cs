using System.Buffers;
using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>Where a term's postings are and how common it is. The offset is opaque to everyone but the postings reader.</summary>
/// <param name="DocFreq">The number of documents in the segment that contain the term.</param>
/// <param name="TotalTermFreq">The number of occurrences of the term in the segment.</param>
/// <param name="PostingsOffset">The postings reader's handle for the term.</param>
public readonly record struct TermInfo(int DocFreq, long TotalTermFreq, long PostingsOffset);

/// <summary>Per-field counts that scoring needs.</summary>
/// <param name="DocCount">Documents that have at least one term in the field.</param>
/// <param name="SumTotalTermFreq">The total number of terms in the field across documents (average field length is this over <paramref name="DocCount"/>).</param>
/// <param name="SumDocFreq">The sum of document frequencies over the field's terms.</param>
public readonly record struct FieldStatistics(int DocCount, long SumTotalTermFreq, long SumDocFreq);

/// <summary>What a postings enumeration must provide beyond documents.</summary>
[Flags]
public enum PostingsFeatures
{
    /// <summary>Documents only.</summary>
    None = 0,

    /// <summary>Term frequencies.</summary>
    Frequencies = 1,

    /// <summary>Positions (implies frequencies).</summary>
    Positions = 2 | 1,

    /// <summary>Character offsets with positions.</summary>
    Offsets = 4 | 2 | 1,
}

/// <summary>
/// An upper bound over a run of documents, used to skip them: no document in the run, from the current one up to
/// <see cref="LastDocId"/>, has a frequency above <see cref="MaxFrequency"/> or a field shorter than <see cref="MinFieldLength"/>.
/// Because BM25-style scores grow with frequency and shrink with length, those two values bound the run's best score (Ding and Suel,
/// block-max WAND).
/// </summary>
/// <param name="LastDocId">The last document the bound covers.</param>
/// <param name="MaxFrequency">The largest term frequency in the run.</param>
/// <param name="MinFieldLength">The smallest field length of any document in the run.</param>
public readonly record struct BlockImpact(int LastDocId, int MaxFrequency, int MinFieldLength);

/// <summary>Iterates a term's postings in increasing document order. Document ids are local to the segment.</summary>
public abstract class PostingsIterator : DocIdSetIterator
{
    /// <summary>The term frequency in the current document; valid when frequencies were requested.</summary>
    public abstract int Frequency { get; }

    /// <summary>Moves to the next position of the term in the current document.</summary>
    /// <returns>The position, counted as <see cref="OneIndex.Analysis.Token"/> positions are. Call at most <see cref="Frequency"/> times per document; valid when positions were requested.</returns>
    public abstract int NextPosition();

    /// <summary>The start offset of the position last returned by <see cref="NextPosition"/>; valid when offsets were requested.</summary>
    public abstract int StartOffset { get; }

    /// <summary>The end offset of the position last returned by <see cref="NextPosition"/>; valid when offsets were requested.</summary>
    public abstract int EndOffset { get; }

    /// <summary>
    /// Looks ahead to the block that holds <paramref name="target"/> without decoding its documents. A later
    /// <see cref="DocIdSetIterator.Advance"/> to a target at or past this call's target stays valid.
    /// </summary>
    /// <param name="target">The document to look ahead to; at least the current document.</param>
    /// <returns>The bound for the block, or one with <c>LastDocId</c> of <see cref="DocIdSetIterator.NoMoreDocs"/> when there is no block.</returns>
    public abstract BlockImpact ShallowAdvance(int target);

    /// <summary>The bound over the whole list: its last document, its largest frequency and its shortest field. Valid from the moment the iterator is created.</summary>
    public abstract BlockImpact TermImpact { get; }
}

/// <summary>Walks terms in byte order. Implemented by the term dictionary.</summary>
public interface ITermEnumerator : IDisposable
{
    /// <summary>The current term in UTF-8; valid until the next <see cref="MoveNext"/>.</summary>
    ReadOnlySpan<byte> Term { get; }

    /// <summary>The current term's statistics.</summary>
    TermInfo Info { get; }

    /// <summary>Advances to the next term.</summary>
    /// <returns><see langword="false"/> at the end.</returns>
    bool MoveNext();
}

/// <summary>
/// The searchable text of one field in one segment: its term dictionary, postings and field lengths. Implemented by the
/// term-dictionary and postings code; consumed by scorers.
/// </summary>
public interface ITextFieldReader
{
    /// <summary>The field's counts in this segment.</summary>
    FieldStatistics Statistics { get; }

    /// <summary>Looks up an exact term.</summary>
    /// <param name="term">The term in UTF-8.</param>
    /// <param name="info">Its statistics and postings handle when found.</param>
    /// <returns><see langword="true"/> when the term exists.</returns>
    bool TryGetTerm(ReadOnlySpan<byte> term, out TermInfo info);

    /// <summary>Walks the terms that start with <paramref name="prefix"/>, in byte order.</summary>
    /// <param name="prefix">The prefix in UTF-8; empty for every term.</param>
    /// <returns>An enumerator the caller disposes.</returns>
    ITermEnumerator GetTerms(ReadOnlySpan<byte> prefix);

    /// <summary>What the field was indexed with: frequencies always, plus positions and offsets when the schema asked for them. <see cref="GetPostings"/> cannot return more than this.</summary>
    PostingsFeatures IndexedFeatures { get; }

    /// <summary>Walks the terms that a code-point automaton (fuzzy or regular expression) accepts, in byte order, without visiting the dictionary branches it rules out.</summary>
    /// <param name="automaton">The automaton; not shared with another search.</param>
    /// <param name="prefix">UTF-8 that every returned term must start with, matched exactly before the automaton takes over (for example a typo-free first letter); empty for none.</param>
    /// <returns>An enumerator the caller disposes.</returns>
    ITermEnumerator Intersect(OneIndex.Codecs.TermAutomaton automaton, ReadOnlySpan<byte> prefix = default);

    /// <summary>Opens a term's postings.</summary>
    /// <param name="term">The term as returned by <see cref="TryGetTerm"/> or an enumerator.</param>
    /// <param name="features">What the caller will read.</param>
    /// <returns>A new enumeration positioned before the first document.</returns>
    PostingsIterator GetPostings(in TermInfo term, PostingsFeatures features);

    /// <summary>The number of terms the field has in a document (its length for scoring).</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <returns>The length; 0 when the document has no value.</returns>
    int GetFieldLength(int docId);
}

/// <summary>The text fields of one segment. Implemented by the text codec's part reader; the segment reader finds it by type.</summary>
public interface ITextFieldSource
{
    /// <summary>The names of the fields that have terms, in ordinal order.</summary>
    IReadOnlyList<string> FieldNames { get; }

    /// <summary>Gets a field's reader.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The reader, or <see langword="null"/> when the segment has no terms for the field.</returns>
    ITextFieldReader? GetTextField(string field);
}

/// <summary>Per-document values of one field, kept for filtering, sorting and aggregation. Implemented by the column code.</summary>
public interface IColumnReader
{
    /// <summary>The field type the column holds.</summary>
    FieldType Type { get; }

    /// <summary>Whether the document has a value.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    bool HasValue(int docId);

    /// <summary>How many values the document has: 0 if none, 1 for a single-valued field.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <returns>The count.</returns>
    int ValueCount(int docId);

    /// <summary>An integer, date (milliseconds) or bool (0 or 1) value.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <param name="index">Which of the document's values; values keep a fixed order.</param>
    /// <returns>The value.</returns>
    long GetInt64(int docId, int index = 0);

    /// <summary>A floating-point value.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <param name="index">Which of the document's values.</param>
    /// <returns>The value.</returns>
    double GetFloat64(int docId, int index = 0);

    /// <summary>A keyword value as UTF-8, valid until the next call on this reader.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <param name="index">Which of the document's values.</param>
    /// <returns>The bytes.</returns>
    ReadOnlySpan<byte> GetKeyword(int docId, int index = 0);

    /// <summary>A geo point value.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <param name="index">Which of the document's values.</param>
    /// <returns>Latitude and longitude in degrees.</returns>
    (double Latitude, double Longitude) GetGeoPoint(int docId, int index = 0);
}

/// <summary>Range lookups over a numeric or date field without scanning every document. Implemented by the point index code.</summary>
public interface IPointIndexReader
{
    /// <summary>Iterates the documents with a value in the range, in increasing document order.</summary>
    /// <param name="lower">The inclusive lower bound, as an ordered 64-bit integer (see <see cref="SortableBits"/> for doubles).</param>
    /// <param name="upper">The inclusive upper bound.</param>
    /// <returns>A new iterator.</returns>
    DocIdSetIterator GetRange(long lower, long upper);
}

/// <summary>The point index of a geo point field: a lookup of the documents with a point in a latitude and longitude rectangle.</summary>
public interface IGeoPointIndexReader : IPointIndexReader
{
    /// <summary>
    /// Iterates the documents that may have a point in a rectangle, in increasing document order. The answer is a superset that never misses a
    /// point inside the rectangle and may include points within a few millimetres outside it, so the caller confirms each candidate against the exact
    /// coordinates in the column.
    /// </summary>
    /// <param name="south">The southern edge in degrees.</param>
    /// <param name="north">The northern edge in degrees.</param>
    /// <param name="west">The western edge in degrees; greater than <paramref name="east"/> when the rectangle crosses the antimeridian.</param>
    /// <param name="east">The eastern edge in degrees.</param>
    /// <returns>A new iterator.</returns>
    DocIdSetIterator GetCandidates(double south, double north, double west, double east);
}

/// <summary>Maps doubles to 64-bit integers that compare in the same order, so one integer index serves both kinds.</summary>
public static class SortableBits
{
    /// <summary>Maps a double to a long such that <c>a &lt; b</c> exactly when <c>Encode(a) &lt; Encode(b)</c>. -0.0 sorts below +0.0; NaN sorts above every number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The ordered integer.</returns>
    public static long Encode(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return bits ^ ((bits >> 63) & long.MaxValue);
    }

    /// <summary>The inverse of <see cref="Encode"/>.</summary>
    /// <param name="encoded">The ordered integer.</param>
    /// <returns>The number.</returns>
    public static double Decode(long encoded) => BitConverter.Int64BitsToDouble(encoded ^ ((encoded >> 63) & long.MaxValue));
}

/// <summary>The stored source of documents. Implemented by the stored-source code.</summary>
public interface IStoredFieldsReader
{
    /// <summary>Rebuilds the stored fields of a document.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <returns>The document with its stored fields.</returns>
    Document GetDocument(int docId);

    /// <summary>Copies a document's stored source bytes (the form it was indexed from).</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns><see langword="false"/> when the segment keeps no source.</returns>
    bool TryGetSource(int docId, IBufferWriter<byte> destination);
}

/// <summary>Which documents of a segment are deleted. Implemented by the delete-bitmap code.</summary>
public interface ILiveDocs
{
    /// <summary>The number of deleted documents.</summary>
    int DeletedCount { get; }

    /// <summary>Whether a document is still live.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <returns><see langword="false"/> for a deleted document.</returns>
    bool IsLive(int docId);
}

/// <summary>
/// A committed segment opened for reading: the one place queries get everything about a segment. Each part is implemented by
/// its own codec; a part the segment lacks is <see langword="null"/>. Document ids are dense, local to the segment, from 0 to
/// <see cref="MaxDoc"/> - 1, and include deleted documents.
/// </summary>
public interface ISegmentReader : IDisposable
{
    /// <summary>The segment's committed description.</summary>
    SegmentInfo Info { get; }

    /// <summary>One more than the largest document id.</summary>
    int MaxDoc { get; }

    /// <summary>The deletes, or <see langword="null"/> if nothing is deleted.</summary>
    ILiveDocs? LiveDocs { get; }

    /// <summary>The stored source, or <see langword="null"/> if the segment has none.</summary>
    IStoredFieldsReader? StoredFields { get; }

    /// <summary>The searchable text of a field.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The reader, or <see langword="null"/> if the segment has no terms for the field.</returns>
    ITextFieldReader? GetTextField(string field);

    /// <summary>The column of a field.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The reader, or <see langword="null"/> if the segment has no column for the field.</returns>
    IColumnReader? GetColumn(string field);

    /// <summary>The point index of a field.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The reader, or <see langword="null"/> if the segment has no points for the field.</returns>
    IPointIndexReader? GetPoints(string field);
}
