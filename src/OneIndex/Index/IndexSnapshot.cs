using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// A pinned, immutable view of one commit. While a snapshot is open, every file its commit lists stays on storage, however many
/// commits and merges the writer makes afterwards. Dispose it to release the files. Safe for concurrent reads.
/// </summary>
public sealed class IndexSnapshot : IDisposable
{
    private readonly IStorage _storage;
    private readonly IReadOnlyList<ISegmentCodec> _codecs;
    private readonly IndexLimits _limits;
    private readonly Action? _release;
    private readonly SegmentReader?[] _readers;
    private int _disposed;

    internal IndexSnapshot(IStorage storage, CommitInfo commit, Schema schema, IReadOnlyList<ISegmentCodec> codecs, IndexLimits limits, Action? release)
    {
        _storage = storage;
        Commit = commit;
        Schema = schema;
        _codecs = codecs;
        _limits = limits;
        _release = release;
        _readers = new SegmentReader?[commit.Segments.Count];
    }

    /// <summary>The commit this snapshot pins.</summary>
    public CommitInfo Commit { get; }

    /// <summary>The commit's generation.</summary>
    public long Generation => Commit.Generation;

    /// <summary>The schema recorded in the commit.</summary>
    public Schema Schema { get; }

    /// <summary>The number of segments.</summary>
    public int SegmentCount => _readers.Length;

    /// <summary>The number of live documents across all segments.</summary>
    public long LiveDocCount => Commit.LiveDocCount;

    /// <summary>Opens a snapshot of the newest commit of a storage without a writer. Nothing is ever deleted on behalf of such a snapshot.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="codecs">The codecs to open parts with; defaults to the text codec.</param>
    /// <param name="limits">The limits to enforce; defaults to <see cref="IndexLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="InvalidOperationException">The storage holds no commit.</exception>
    /// <exception cref="CorruptIndexException">The newest commit or a file it lists is damaged.</exception>
    public static IndexSnapshot Open(IStorage storage, IReadOnlyList<ISegmentCodec>? codecs = null, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= IndexLimits.Default;
        CommitInfo commit = CommitProtocol.OpenLatest(storage, limits, cancellationToken)
            ?? throw new InvalidOperationException("The storage holds no committed index.");
        Schema schema = commit.SchemaBytes.IsEmpty ? Schema.Empty : Schema.Deserialize(commit.SchemaBytes.Span, limits);
        return new IndexSnapshot(storage, commit, schema, codecs ?? [new OneIndex.Codecs.TextSegmentCodec()], limits, null);
    }

    /// <summary>Opens a snapshot of the newest commit without a writer.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="codecs">The codecs to open parts with; defaults to the text codec.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The snapshot.</returns>
    public static async ValueTask<IndexSnapshot> OpenAsync(IStorage storage, IReadOnlyList<ISegmentCodec>? codecs = null, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= IndexLimits.Default;
        CommitInfo commit = await CommitProtocol.OpenLatestAsync(storage, limits, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The storage holds no committed index.");
        Schema schema = commit.SchemaBytes.IsEmpty ? Schema.Empty : Schema.Deserialize(commit.SchemaBytes.Span, limits);
        return new IndexSnapshot(storage, commit, schema, codecs ?? [new OneIndex.Codecs.TextSegmentCodec()], limits, null);
    }

    /// <summary>Gets (opening on first use) one segment's reader. The reader belongs to the snapshot; do not dispose it.</summary>
    /// <param name="index">The segment's position in the commit.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="ObjectDisposedException">The snapshot was disposed.</exception>
    /// <exception cref="CorruptIndexException">A file of the segment is damaged.</exception>
    public SegmentReader GetSegment(int index, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SegmentReader? r = Volatile.Read(ref _readers[index]);
        if (r is not null)
        {
            return r;
        }

        lock (_readers)
        {
            r = _readers[index];
            if (r is null)
            {
                r = SegmentReader.Open(_storage, Schema, Commit.Segments[index], _codecs, _limits, null, cancellationToken);
                Volatile.Write(ref _readers[index], r);
            }

            return r;
        }
    }

    /// <summary>Finds the live document with an identifier, newest segment first. Allocates nothing once the segment readers are open and the identifier is short.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="segment">The segment's position on success.</param>
    /// <param name="docId">The document id within the segment on success.</param>
    /// <returns><see langword="true"/> if found.</returns>
    public bool TryFindById(ReadOnlySpan<char> id, out int segment, out int docId)
    {
        for (int i = _readers.Length - 1; i >= 0; i--)
        {
            if (GetSegment(i).TryFindId(id, out docId))
            {
                segment = i;
                return true;
            }
        }

        segment = -1;
        docId = -1;
        return false;
    }

    /// <summary>Reads every byte of every file the commit lists and checks the checksums.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="CorruptIndexException">A file is damaged; the exception names it.</exception>
    public void Verify(CancellationToken cancellationToken = default)
    {
        CommitProtocol.VerifyFiles(_storage, Commit, cancellationToken);
        foreach (SegmentInfo seg in Commit.Segments)
        {
            SegmentFile? ids = seg.FindPart(SegmentParts.Ids);
            if (ids is not null)
            {
                using IRangeReader reader = _storage.OpenRead(ids.FileName, cancellationToken);
                var bytes = new byte[(int)reader.Length];
                reader.ReadExactly(0, bytes, cancellationToken);
                IndexFile.Validate(ids.FileName, bytes, IdsFormat.Magic, IdsFormat.FormatVersion);
                if (!IdsFormat.TryValidate(ids.FileName, bytes, seg.DocCount, _limits, out string? error))
                {
                    throw new CorruptIndexException(ids.FileName, error!);
                }
            }

            _ = SegmentReader.LoadLiveDocs(_storage, seg, _limits, cancellationToken);
        }
    }

    /// <summary>Closes the segment readers and releases the pinned files.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (SegmentReader? r in _readers)
        {
            r?.Dispose();
        }

        _release?.Invoke();
    }
}
