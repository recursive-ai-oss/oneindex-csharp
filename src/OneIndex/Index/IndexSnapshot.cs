using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// A pinned, immutable view of one commit. While a snapshot is open, every file its commit lists stays on storage, however many
/// commits and merges the writer makes afterwards, even when the writer is another process (see docs/processes.md). Dispose it
/// to release the files. Safe for concurrent reads. A snapshot taken from a near-real-time reader is not a commit: see
/// <see cref="IsDurable"/>.
/// </summary>
public sealed class IndexSnapshot : IDisposable
{
    private readonly IStorage _storage;
    private readonly IReadOnlyList<ISegmentCodec> _codecs;
    private readonly IndexLimits _limits;
    private readonly Action? _release;
    private readonly SegmentReader?[] _readers;
    private readonly LiveDocs?[]? _liveOverrides;
    private int _references = 1;

    internal IndexSnapshot(IStorage storage, CommitInfo commit, Schema schema, IReadOnlyList<ISegmentCodec> codecs, IndexLimits limits, Action? release, LiveDocs?[]? liveOverrides = null, long stamp = 0)
    {
        _liveOverrides = liveOverrides;
        Stamp = stamp;
        _storage = storage;
        Commit = commit;
        Schema = schema;
        _codecs = codecs;
        _limits = limits;
        _release = release;
        _readers = new SegmentReader?[commit.Segments.Count];
    }

    /// <summary>
    /// Whether the snapshot is a committed state that survives a crash. <see langword="false"/> for a near-real-time snapshot, which
    /// also shows flushed and deleted-but-uncommitted changes; <see cref="Generation"/> is then the last commit it extends.
    /// </summary>
    public bool IsDurable => _liveOverrides is null;

    internal long Stamp { get; }

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

    /// <summary>
    /// Opens a snapshot of the newest commit of a storage, from any process, whether or not a writer is running. On a storage that
    /// supports locks the commit is pinned with a lease file for as long as the snapshot is open, so a writer in another process
    /// keeps its files; if this process is killed the operating system drops the lease and the files become reclaimable. A read-only
    /// storage cannot pin; opening a commit while a writer is active can then fail with <see cref="CorruptIndexException"/> naming a
    /// vanished file (open again).
    /// </summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="codecs">The codecs to open parts with; defaults to the text codec.</param>
    /// <param name="limits">The limits to enforce; defaults to <see cref="IndexLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="InvalidOperationException">The storage holds no commit.</exception>
    /// <exception cref="CorruptIndexException">The newest commit or a file it lists is damaged.</exception>
    public static IndexSnapshot Open(IStorage storage, IReadOnlyList<ISegmentCodec>? codecs = null, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        limits ??= IndexLimits.Default;
        if (!storage.SupportsLocks)
        {
            CommitInfo commit = CommitProtocol.OpenLatest(storage, limits, cancellationToken)
                ?? throw new InvalidOperationException("The storage holds no committed index.");
            return new IndexSnapshot(storage, commit, SchemaOf(commit, limits), codecs ?? OneIndex.Codecs.SegmentCodecs.CreateDefault(), limits, null);
        }

        ReaderLease lease = IndexLocks.AcquireLease(storage, cancellationToken, out long generation)
            ?? throw new InvalidOperationException("The storage holds no committed index.");
        try
        {
            CommitInfo commit = CommitProtocol.OpenNamed(storage, CommitFileNames.For(generation), limits, cancellationToken);
            return new IndexSnapshot(storage, commit, SchemaOf(commit, limits), codecs ?? OneIndex.Codecs.SegmentCodecs.CreateDefault(), limits, () => ReleaseLease(storage, lease, limits));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static Schema SchemaOf(CommitInfo commit, IndexLimits limits)
        => commit.SchemaBytes.IsEmpty ? Schema.Empty : Schema.Deserialize(commit.SchemaBytes.Span, limits);

    /// <summary>Lets go of a lease, then reclaims the files of superseded commits nobody pins any more (the writer may be gone).</summary>
    internal static void ReleaseLease(IStorage storage, ReaderLease lease, IndexLimits limits)
    {
        lease.Dispose();
        try
        {
            IndexMaintenance.CollectGarbage(storage, limits);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CorruptIndexException)
        {
            // Housekeeping only: the writer's next commit or open reclaims whatever this missed.
        }
    }

    /// <summary>Opens a snapshot of the newest commit without a writer.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="codecs">The codecs to open parts with; defaults to the text codec.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The snapshot.</returns>
    public static async ValueTask<IndexSnapshot> OpenAsync(IStorage storage, IReadOnlyList<ISegmentCodec>? codecs = null, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        limits ??= IndexLimits.Default;
        if (storage.SupportsLocks)
        {
            // Locks and leases are local file operations; there is nothing to await.
            return Open(storage, codecs, limits, cancellationToken);
        }

        CommitInfo commit = await CommitProtocol.OpenLatestAsync(storage, limits, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The storage holds no committed index.");
        Schema schema = commit.SchemaBytes.IsEmpty ? Schema.Empty : Schema.Deserialize(commit.SchemaBytes.Span, limits);
        return new IndexSnapshot(storage, commit, schema, codecs ?? OneIndex.Codecs.SegmentCodecs.CreateDefault(), limits, null);
    }

    /// <summary>Gets (opening on first use) one segment's reader. The reader belongs to the snapshot; do not dispose it.</summary>
    /// <param name="index">The segment's position in the commit.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="ObjectDisposedException">The snapshot was disposed.</exception>
    /// <exception cref="CorruptIndexException">A file of the segment is damaged.</exception>
    public SegmentReader GetSegment(int index, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _references) <= 0, this);
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
                r = SegmentReader.Open(_storage, Schema, Commit.Segments[index], _codecs, _limits, _liveOverrides?[index], cancellationToken);
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

            if (_liveOverrides is null)
            {
                _ = SegmentReader.LoadLiveDocs(_storage, seg, _limits, cancellationToken);
            }
        }
    }

    /// <summary>Takes another reference to the snapshot; each reference is released by one <see cref="Dispose"/>.</summary>
    /// <returns><see langword="true"/> if the snapshot was still open.</returns>
    internal bool TryAddReference()
    {
        while (true)
        {
            int current = Volatile.Read(ref _references);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Closes the segment readers and releases the pinned files once the last reference is released.</summary>
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) != 0)
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
