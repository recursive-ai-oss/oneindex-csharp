using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// A long-lived reader of an index that can move to newer commits. It pins one snapshot at a time; <see cref="Refresh(CancellationToken)"/>
/// pins the newest commit first and only then lets go of the old one, so there is never a moment without a pinned commit. Open it in
/// any process, with or without a writer running; or get one from <see cref="IndexWriter.OpenNearRealTimeReader"/> to see the
/// writer's uncommitted changes. Safe for concurrent use.
/// </summary>
public sealed class IndexReader : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<IndexSnapshot, CancellationToken, IndexSnapshot?> _refresh;
    private IndexSnapshot _current;
    private bool _disposed;

    private IndexReader(IndexSnapshot first, Func<IndexSnapshot, CancellationToken, IndexSnapshot?> refresh)
    {
        _current = first;
        _refresh = refresh;
    }

    /// <summary>The generation of the commit the current snapshot shows (for a near-real-time reader, the last commit it extends).</summary>
    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _current.Generation;
            }
        }
    }

    /// <summary>Whether the current snapshot is a commit. <see langword="false"/> for a near-real-time reader.</summary>
    public bool IsDurable
    {
        get
        {
            lock (_gate)
            {
                return _current.IsDurable;
            }
        }
    }

    /// <summary>Opens a reader on the newest commit of a storage (see <see cref="IndexSnapshot.Open(IStorage, IReadOnlyList{ISegmentCodec}?, IndexLimits?, CancellationToken)"/>).</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="codecs">The codecs to open parts with.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="InvalidOperationException">The storage holds no commit.</exception>
    /// <exception cref="CorruptIndexException">The newest commit or a file it lists is damaged.</exception>
    public static IndexReader Open(IStorage storage, IReadOnlyList<ISegmentCodec>? codecs = null, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        IndexSnapshot first = IndexSnapshot.Open(storage, codecs, limits, cancellationToken);
        return new IndexReader(first, (current, ct) =>
        {
            // A listing is the whole cost of finding out that nothing changed.
            if (CommitProtocol.LatestGeneration(storage.List(ct)) <= current.Generation)
            {
                return null;
            }

            return IndexSnapshot.Open(storage, codecs, limits, ct);
        });
    }

    /// <summary>Opens a reader on the newest commit of a storage (see <see cref="Open(IStorage, IReadOnlyList{ISegmentCodec}?, IndexLimits?, CancellationToken)"/>).</summary>
    /// <param name="storage">The storage.</param>
    /// <param name="codecs">The segment codecs; default the built-in set.</param>
    /// <param name="limits">Limits; default <see cref="IndexLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The reader.</returns>
    public static ValueTask<IndexReader> OpenAsync(IStorage storage, IReadOnlyList<ISegmentCodec>? codecs = null, IndexLimits? limits = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Open(storage, codecs, limits, cancellationToken));

    internal static IndexReader Create(IndexSnapshot first, Func<IndexSnapshot, CancellationToken, IndexSnapshot?> refresh) => new(first, refresh);

    /// <summary>Gets the current snapshot with an extra reference. It stays valid, whatever <see cref="Refresh(CancellationToken)"/> does, until you dispose it.</summary>
    /// <returns>The snapshot; dispose it when done.</returns>
    /// <exception cref="ObjectDisposedException">The reader was disposed.</exception>
    public IndexSnapshot Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(!_current.TryAddReference(), this);
            return _current;
        }
    }

    /// <summary>Moves to the newest state if there is one: the newest commit, or for a near-real-time reader the writer's current state.</summary>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns><see langword="true"/> if the reader now shows something newer than before.</returns>
    /// <exception cref="ObjectDisposedException">The reader was disposed.</exception>
    public bool Refresh(CancellationToken cancellationToken = default)
    {
        IndexSnapshot current;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            current = _current;
        }

        IndexSnapshot? next = _refresh(current, cancellationToken);
        if (next is null)
        {
            return false;
        }

        IndexSnapshot old;
        lock (_gate)
        {
            if (_disposed)
            {
                next.Dispose();
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            old = _current;
            _current = next;
        }

        old.Dispose();
        return true;
    }

    /// <summary>The asynchronous twin of <see cref="Refresh(CancellationToken)"/>.</summary>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns><see langword="true"/> if the reader now shows something newer.</returns>
    public ValueTask<bool> RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Refresh(cancellationToken));

    /// <summary>Releases the reader's own reference; snapshots obtained from <see cref="Acquire"/> stay valid until disposed.</summary>
    public void Dispose()
    {
        IndexSnapshot current;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            current = _current;
        }

        current.Dispose();
    }
}
