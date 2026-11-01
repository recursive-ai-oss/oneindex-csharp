using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Search;

/// <summary>The column part of a segment: per-document values of the fields that keep a column. Implemented by the column codec's part reader.</summary>
public interface IColumnSource
{
    /// <summary>Gets a field's column.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The reader, or <see langword="null"/> when the segment has no column for the field.</returns>
    IColumnReader? GetColumn(string field);
}

/// <summary>The point-index part of a segment: range lookups over numeric and date fields. Implemented by the point codec's part reader.</summary>
public interface IPointSource
{
    /// <summary>Gets a field's point index.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The reader, or <see langword="null"/> when the segment has no points for the field.</returns>
    IPointIndexReader? GetPoints(string field);
}

/// <summary>The stored-source part of a segment. Implemented by the stored-source codec's part reader.</summary>
public interface IStoredFieldsSource
{
    /// <summary>The stored source reader.</summary>
    IStoredFieldsReader StoredFields { get; }
}

/// <summary>
/// Presents an opened <see cref="SegmentReader"/> as the <see cref="ISegmentReader"/> queries run on. It only finds each part by the
/// contract its codec's part reader implements, so a codec added later (columns, points, stored source) needs no change here.
/// It does not own the segment reader: the snapshot does, and disposing the adapter does nothing.
/// </summary>
public sealed class SegmentSearchReader : ISegmentReader
{
    private readonly SegmentReader _segment;
    private readonly ITextFieldSource? _text;
    private readonly IColumnSource? _columns;
    private readonly IPointSource? _points;
    private readonly IVectorSource? _vectors;

    /// <summary>Wraps a segment reader.</summary>
    /// <param name="segment">The reader; it must outlive this adapter.</param>
    public SegmentSearchReader(SegmentReader segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _text = segment.GetPart<ITextFieldSource>();
        _columns = segment.GetPart<IColumnSource>();
        _points = segment.GetPart<IPointSource>();
        _vectors = segment.GetPart<IVectorSource>();
        StoredFields = segment.GetPart<IStoredFieldsSource>()?.StoredFields;
    }

    /// <inheritdoc/>
    public SegmentInfo Info => _segment.Info;

    /// <inheritdoc/>
    public int MaxDoc => _segment.MaxDoc;

    /// <inheritdoc/>
    public ILiveDocs? LiveDocs => _segment.Live.DeletedCount == 0 ? null : _segment.Live;

    /// <inheritdoc/>
    public IStoredFieldsReader? StoredFields { get; }

    /// <inheritdoc/>
    public ITextFieldReader? GetTextField(string field) => _text?.GetTextField(field);

    /// <inheritdoc/>
    public IColumnReader? GetColumn(string field) => _columns?.GetColumn(field);

    /// <inheritdoc/>
    public IPointIndexReader? GetPoints(string field) => _points?.GetPoints(field);

    /// <inheritdoc/>
    public IVectorFieldReader? GetVectors(string field) => _vectors?.GetVectors(field);

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}

/// <summary>Builds searchers over committed snapshots.</summary>
public static class SnapshotSearch
{
    /// <summary>Opens every segment of a snapshot and makes the context queries run on. The snapshot keeps ownership of the segment readers and must outlive the context.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="cancellationToken">Cancels opening segments.</param>
    /// <returns>The context, with statistics summed across the snapshot's segments.</returns>
    /// <exception cref="CorruptIndexException">A segment file is damaged.</exception>
    public static SearchContext CreateContext(IndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var readers = new List<ISegmentReader>(snapshot.SegmentCount);
        for (int i = 0; i < snapshot.SegmentCount; i++)
        {
            readers.Add(new SegmentSearchReader(snapshot.GetSegment(i, cancellationToken)));
        }

        return new SearchContext(snapshot.Schema, readers);
    }

    /// <summary>Creates a searcher over a snapshot with the BM25F query engine.</summary>
    /// <param name="snapshot">The snapshot; it must outlive the searcher.</param>
    /// <param name="options">Scoring and expansion settings; the defaults when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels opening segments.</param>
    /// <returns>The searcher.</returns>
    public static IndexSearcher CreateSearcher(IndexSnapshot snapshot, QueryEngineOptions? options = null, CancellationToken cancellationToken = default)
        => new(CreateContext(snapshot, cancellationToken), new QueryEngine(options));
}
