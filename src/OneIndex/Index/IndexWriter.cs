using System.Text;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>Settings of an <see cref="IndexWriter"/>. Every property has a working default.</summary>
public sealed class IndexWriterOptions
{
    /// <summary>The limits every reader enforces. Default <see cref="IndexLimits.Default"/>.</summary>
    public IndexLimits Limits { get; init; } = IndexLimits.Default;

    /// <summary>The codecs that write and read segment parts, registered explicitly. Default: <see cref="OneIndex.Codecs.SegmentCodecs.CreateDefault"/> (text index, columns, points, stored source). An empty list leaves a segment holding only identifiers.</summary>
    public IReadOnlyList<ISegmentCodec> Codecs { get; init; } = OneIndex.Codecs.SegmentCodecs.CreateDefault();

    /// <summary>Buffered documents that trigger a flush into a new segment. Default 10,000.</summary>
    public int MaxBufferedDocuments { get; init; } = 10_000;

    /// <summary>Chooses merges. Default <see cref="TieredMergePolicy"/> with its defaults.</summary>
    public TieredMergePolicy MergePolicy { get; init; } = new();

    /// <summary>Runs merges. Default a <see cref="CooperativeMergeScheduler"/>, which needs no threads. The writer disposes it.</summary>
    public IMergeScheduler? MergeScheduler { get; init; }

    /// <summary>Whether a commit asks the merge policy for merges afterwards. Default <see langword="true"/>.</summary>
    public bool AutoMerge { get; init; } = true;

    /// <summary>Limits merge output to this many bytes per second (0 = unlimited, the default). A cooperative scheduler cannot slow itself down and ignores it.</summary>
    public long MaxMergeBytesPerSecond { get; init; }

    /// <summary>The clock for the merge throttle. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// The single writer of an index: it buffers documents, flushes them into immutable segments, records deletes per segment,
/// merges segments and publishes everything atomically with <see cref="Commit(CancellationToken)"/>. All members are safe to call
/// from several threads; they take turns. Only one writer may have a storage open.
/// </summary>
/// <remarks>
/// <para>Nothing is visible to readers before a commit, and a commit that returned is durable (see docs/commit-format.md). A
/// crash or an exception leaves the previous commit intact; <see cref="Open(IStorage, Schema?, IndexWriterOptions?, CancellationToken)"/> removes the files such an interruption leaves behind. If an
/// I/O operation of a commit fails, the writer refuses further work (<see cref="InvalidOperationException"/>) because it cannot
/// know whether the commit became durable; open the index again.</para>
/// <para>Merges run through the <see cref="IMergeScheduler"/>. A finished merge is installed by the next commit, never before, so
/// a commit publishes exactly the changes the caller made plus whole merges. Use <see cref="WaitForMerges(CancellationToken)"/> before a
/// commit to make the layout independent of timing.</para>
/// <para>The async twins finish synchronously except for the commit file protocol: codecs write through the synchronous storage calls.</para>
/// </remarks>
public sealed partial class IndexWriter : IDisposable, IAsyncDisposable
{
    /// <summary>Whether a file name is one the engine itself creates: <c>commit-&lt;16 hex&gt;.oic|tmp</c> or <c>s&lt;16 hex&gt;.&lt;anything&gt;</c>.</summary>
    internal static bool IsOwnedFile(string name)
    {
        static bool Hex(ReadOnlySpan<char> text)
        {
            foreach (char c in text)
            {
                if (!(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        if (name.StartsWith("commit-", StringComparison.Ordinal))
        {
            return name.Length == 7 + 16 + 4 && Hex(name.AsSpan(7, 16)) && (name.EndsWith(".oic", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal));
        }

        return name.Length > 18 && name[0] == 's' && name[17] == '.' && Hex(name.AsSpan(1, 16));
    }

    private readonly object _gate = new();
    private readonly IStorage _storage;
    private readonly IndexWriterOptions _options;
    private readonly IndexLimits _limits;
    private volatile Schema _schema;
    private long _schemaEpoch;
    private volatile Schema? _target;
    private readonly List<Shadow> _shadows = [];
    private readonly IReadOnlyList<ISegmentCodec> _codecs;
    private readonly IMergeScheduler _scheduler;
    private readonly FileReferences _refs;
    private readonly IStorageLock? _writerLock;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<WorkSegment> _segments = [];
    private readonly List<Document?> _buffer = [];
    private readonly Dictionary<string, int> _bufferIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly List<MergeResult> _completedMerges = [];
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private int _bufferLive;
    private ulong _nextSegmentId = 1;
    private CommitInfo? _current;
    private byte[] _appliedPosition = [];
    private SortedDictionary<string, string> _userData = new(StringComparer.Ordinal);
    private bool _metaDirty;
    private Exception? _failed;
    private Exception? _mergeError;
    private bool _disposed;
    private long _changeStamp;

    private IndexWriter(IStorage storage, Schema schema, long schemaEpoch, Schema? target, IndexWriterOptions options, IStorageLock? writerLock)
    {
        _target = target;
        _schemaEpoch = schemaEpoch;
        _writerLock = writerLock;
        _storage = storage;
        _schema = schema;
        _options = options;
        _limits = options.Limits;
        _codecs = options.Codecs;
        _scheduler = options.MergeScheduler ?? new CooperativeMergeScheduler();
        _refs = new FileReferences(storage, storage.SupportsLocks ? () => IndexLocks.LivePinnedFiles(storage, options.Limits, default) : null);
    }

    /// <summary>The schema queries are answered under and documents are validated against. While a schema change is being rebuilt it is the old schema (<see cref="SchemaEvolution"/>); a write completes the rebuild first. It grows as the paths of indexed JSON fields appear (docs/dynamic.md).</summary>
    public Schema Schema => _schema;

    /// <summary>Where the background rebuild after a schema change stands (docs/schema-evolution.md).</summary>
    public SchemaEvolutionStatus SchemaEvolution
    {
        get
        {
            lock (_gate)
            {
                return new SchemaEvolutionStatus(_schemaEpoch, _segments.Count, _target is null ? 0 : _segments.Count(x => ShadowOf(x) is null), _target is not null);
            }
        }
    }

    /// <summary>The generation of the newest commit, or 0 before the first.</summary>
    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _current?.Generation ?? 0;
            }
        }
    }

    /// <summary>The documents waiting in memory for a flush.</summary>
    public int BufferedDocumentCount
    {
        get
        {
            lock (_gate)
            {
                return _bufferLive;
            }
        }
    }

    /// <summary>The segments in the working set (flushed, merged or committed), not counting the buffer.</summary>
    public int SegmentCount
    {
        get
        {
            lock (_gate)
            {
                return _segments.Count;
            }
        }
    }

    /// <summary>The live documents in the working set, buffer included.</summary>
    public long LiveDocumentCount
    {
        get
        {
            lock (_gate)
            {
                long n = _bufferLive;
                foreach (WorkSegment s in _segments)
                {
                    n += s.Info.DocCount - s.DeletedCount;
                }

                return n;
            }
        }
    }

    /// <summary>The error of the most recent merge that failed, or <see langword="null"/>. A failed merge changes nothing.</summary>
    public Exception? LastMergeError
    {
        get
        {
            lock (_gate)
            {
                return _mergeError;
            }
        }
    }

    /// <summary>Opens the writer, recovering from any interrupted work.</summary>
    /// <param name="storage">A writable storage holding a new or existing index.</param>
    /// <param name="schema">The schema. Required for a new index. For an existing one, <see langword="null"/> uses the committed schema and a different schema evolves the index: it takes effect at once and segments that need it are rebuilt in the background (docs/schema-evolution.md).</param>
    /// <param name="options">Settings; defaults apply when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The writer.</returns>
    /// <exception cref="IndexLockedException">Another writer, in this or another process, has the index open. The lock is released if that process dies.</exception>
    /// <exception cref="CorruptIndexException">The newest commit or a file it lists is damaged. The writer never falls back to an older commit.</exception>
    /// <exception cref="ArgumentException">The codecs are inconsistent, or a new index has no schema.</exception>
    /// <exception cref="SchemaEvolutionException">The schema cannot replace the committed one; the message names the field.</exception>
    /// <remarks>
    /// Recovery deletes leftover temporary commit files, commit files older than the newest, and segment files no commit lists,
    /// except files that a live reader in another process still pins (they go when that reader lets go). Other files in the storage
    /// are left alone.
    /// </remarks>
    public static IndexWriter Open(IStorage storage, Schema? schema = null, IndexWriterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.IsReadOnly)
        {
            throw new ArgumentException("The storage is read-only.", nameof(storage));
        }

        options ??= new IndexWriterOptions();
        ValidateCodecs(options.Codecs);
        IStorageLock? writerLock = IndexLocks.AcquireWriterLock(storage, cancellationToken);
        try
        {
            return OpenLocked(storage, schema, options, writerLock, cancellationToken);
        }
        catch
        {
            writerLock?.Dispose();
            throw;
        }
    }

    private static IndexWriter OpenLocked(IStorage storage, Schema? schema, IndexWriterOptions options, IStorageLock? writerLock, CancellationToken cancellationToken)
    {
        CommitInfo? commit = CommitProtocol.OpenLatest(storage, options.Limits, cancellationToken);
        Schema effective;
        Schema? target = null;
        long epoch = commit?.SchemaEpoch ?? 0;
        bool adopt = false;
        bool resumePending = false;
        if (commit is not null && !commit.SchemaBytes.IsEmpty)
        {
            Schema committed = Schema.Deserialize(commit.SchemaBytes.Span, options.Limits);
            effective = committed;
            Schema? pending = commit.Pending is { } p ? Schema.Deserialize(p.SchemaBytes.Span, options.Limits) : null;
            if (schema is not null && pending is not null && pending.WithoutDynamicFields().Equals(schema.WithoutDynamicFields()))
            {
                resumePending = true;
                // The same change as the interrupted run: keep the segments it already rebuilt and go on.
                target = pending;
            }
            else if (schema is not null && !schema.Equals(committed))
            {
                SchemaDiff.Plan plan = SchemaDiff.Compare(committed, schema.WithoutDynamicFields(), options.Limits);
                if (plan.Rebuild && commit.Segments.Count > 0)
                {
                    target = plan.Effective;
                    adopt = pending is not null;
                }
                else
                {
                    effective = plan.Effective;
                    adopt = true;
                }
            }
            else
            {
                // The application is back on the schema being served: the rebuild is abandoned.
                adopt = pending is not null;
            }

            if (target is null && commit.Pending is not null)
            {
                adopt = true;
            }

            if (target is not null && target != pending)
            {
                adopt = true;
            }
        }
        else
        {
            effective = schema ?? throw new ArgumentException("A new index needs a schema.", nameof(schema));
        }

        var writer = new IndexWriter(storage, effective, epoch, target, options, writerLock);
        try
        {
            writer.Recover(commit, resumePending, cancellationToken);
            if (adopt || (target is not null && commit!.Pending is null))
            {
                // Make the new schema (and the epoch that marks the old segments for rebuild) durable before anything else happens.
                writer._metaDirty = true;
                writer.Commit(cancellationToken);
            }

            return writer;
        }
        catch
        {
            // The caller releases the lock it handed in, so the writer must not.
            writer.DisposeCore(releaseLock: false);
            throw;
        }
    }

    /// <summary>Opens the writer (see <see cref="Open(IStorage, Schema?, IndexWriterOptions?, CancellationToken)"/>).</summary>
    /// <param name="storage">A writable storage.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="options">Settings.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The writer.</returns>
    public static ValueTask<IndexWriter> OpenAsync(IStorage storage, Schema? schema = null, IndexWriterOptions? options = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Open(storage, schema, options, cancellationToken));

    /// <summary>Adds a document to a schema that has no identifier field.</summary>
    /// <param name="document">The document; it is validated against the schema.</param>
    /// <exception cref="ArgumentException">The document does not fit the schema (the message names the field).</exception>
    /// <exception cref="InvalidOperationException">The schema has an identifier field; use <see cref="Upsert(Document, CancellationToken)"/>.</exception>
    public void Add(Document document)
    {
        if (_schema.IdField is not null)
        {
            throw new InvalidOperationException("The schema has an identifier field; use Upsert so that identifiers stay unique.");
        }

        Upsert(document);
    }

    /// <summary>Adds a document, replacing the live document with the same identifier if there is one.</summary>
    /// <param name="document">The document; it is validated against the schema.</param>
    /// <param name="cancellationToken">Cancels a flush this call triggers.</param>
    /// <exception cref="ArgumentException">The document does not fit the schema.</exception>
    public void Upsert(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_target is not null)
        {
            // New documents are written under one schema only, so the rebuild ends first (docs/schema-evolution.md).
            FinishRebuildBeforeWrite(cancellationToken);
        }

        _schema.Validate(document);
        string? id = _schema.IdField is { } idField ? document.Get(idField).AsText() : null;
        lock (_gate)
        {
            ThrowIfUnusable();
            if (id is not null)
            {
                DeleteByIdLocked(id);
                _bufferIds[id] = _buffer.Count;
            }

            _buffer.Add(document);
            _bufferLive++;
            _changeStamp++;
            if (_bufferLive >= _options.MaxBufferedDocuments)
            {
                FlushLocked(cancellationToken);
            }
        }
    }

    /// <summary>Deletes the live document with an identifier.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns><see langword="true"/> if a document was deleted.</returns>
    /// <exception cref="InvalidOperationException">The schema has no identifier field.</exception>
    public bool DeleteById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_schema.IdField is null)
        {
            throw new InvalidOperationException("The schema has no identifier field.");
        }

        lock (_gate)
        {
            ThrowIfUnusable();
            return DeleteByIdLocked(id);
        }
    }

    /// <summary>Deletes every live document whose keyword field has exactly this value.</summary>
    /// <param name="field">A keyword field of the schema.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The number of documents deleted.</returns>
    /// <exception cref="ArgumentException">The field is not a keyword field.</exception>
    /// <exception cref="NotSupportedException">A segment has no codec that can look up keyword values (<see cref="ITermDocLookup"/>).</exception>
    public int DeleteByTerm(string field, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        if (!_schema.TryGetField(field, out FieldDefinition? def) || def.Type != FieldType.Keyword)
        {
            throw new ArgumentException($"'{field}' is not a keyword field of the schema.", nameof(field));
        }

        lock (_gate)
        {
            ThrowIfUnusable();
            if (string.Equals(field, _schema.IdField, StringComparison.Ordinal))
            {
                return DeleteByIdLocked(value) ? 1 : 0;
            }

            int deleted = 0;
            for (int i = 0; i < _buffer.Count; i++)
            {
                Document? d = _buffer[i];
                if (d is not null && HasValue(d, field, value))
                {
                    RemoveFromBuffer(i);
                    deleted++;
                }
            }

            byte[] term = Encoding.UTF8.GetBytes(value);
            var docs = new List<int>();
            foreach (WorkSegment seg in _segments)
            {
                if (seg.Info.DocCount == seg.DeletedCount)
                {
                    continue;
                }

                SegmentReader reader = EnsureReader(seg, cancellationToken);
                docs.Clear();
                bool any = false;
                foreach (ISegmentCodec codec in _codecs)
                {
                    if (reader.GetPartOf(codec) is ITermDocLookup lookup)
                    {
                        lookup.CollectDocs(field, term, docs);
                        any = true;
                    }
                }

                if (!any)
                {
                    throw new NotSupportedException("No codec of the segment can look up keyword values.");
                }

                foreach (int d in docs)
                {
                    if (MarkDeleted(seg, d))
                    {
                        deleted++;
                    }
                }
            }

            return deleted;
        }
    }

    /// <summary>Sets the application's marker stored in the next commit (for example how far an external change log is applied).</summary>
    /// <param name="position">At most <see cref="IndexLimits.MaxAppliedPositionBytes"/> bytes.</param>
    public void SetAppliedPosition(ReadOnlySpan<byte> position)
    {
        if (position.Length > _limits.MaxAppliedPositionBytes)
        {
            throw new ArgumentException("The applied position is too long.", nameof(position));
        }

        lock (_gate)
        {
            ThrowIfUnusable();
            if (!position.SequenceEqual(_appliedPosition))
            {
                _appliedPosition = position.ToArray();
                _metaDirty = true;
            }
        }
    }

    /// <summary>Sets a user data entry stored in the next commit.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, or <see langword="null"/> to remove the key.</param>
    public void SetUserData(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            ThrowIfUnusable();
            bool changed = value is null ? _userData.Remove(key) : !_userData.TryGetValue(key, out string? old) || old != value;
            if (value is not null)
            {
                _userData[key] = value;
            }

            _metaDirty |= changed;
        }
    }

    /// <summary>Writes buffered documents into a new segment (not yet committed).</summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    public void Flush(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            FlushLocked(cancellationToken);
        }
    }

    /// <summary>Writes buffered documents into a new segment.</summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>A completed task.</returns>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        Flush(cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Publishes every change since the last commit atomically and durably: buffered documents, deletes and finished merges. If
    /// nothing changed, nothing is written.
    /// </summary>
    /// <param name="cancellationToken">Cancels before the commit file is written; after that the commit completes.</param>
    /// <returns>The generation now durable.</returns>
    /// <exception cref="IOException">Storage failed. The writer is then unusable; open the index again to learn which commit stands.</exception>
    public long Commit(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            _scheduler.Poll(cancellationToken);
            InstallMergesLocked();
            FlushLocked(cancellationToken);
            DropEmptySegmentsLocked();
            PruneShadowsLocked();
            TrySwapLocked();
            bool dirty = _metaDirty || _current is null || _current.SchemaEpoch != _schemaEpoch || _segments.Any(s => s.Dirty || s.IsNew) || SegmentSetChanged() || !_schema.Serialize().AsSpan().SequenceEqual(_current.SchemaBytes.Span);
            if (!dirty)
            {
                return _current!.Generation;
            }

            CommitInfo next;
            var written = new List<string>();
            try
            {
                var infos = new List<SegmentInfo>(_segments.Count);
                foreach (WorkSegment s in _segments)
                {
                    infos.Add(s.Dirty ? WriteDeletes(s, written, cancellationToken) : s.Info);
                }

                PendingEvolution? pendingEvolution = _target is { } t
                    ? new PendingEvolution(t.Serialize(), _shadows.Select(x => new RebuiltSegment(x.Info, x.Source, x.SourceDeleted)).ToList())
                    : null;
                next = new CommitInfo((_current?.Generation ?? 0) + 1, _nextSegmentId, infos, _schema.Serialize(), _appliedPosition, _userData, _schemaEpoch, pendingEvolution);
            }
            catch
            {
                foreach (string n in written)
                {
                    _refs.TryDelete(n);
                }

                throw;
            }

            foreach (string n in written)
            {
                _pending.Add(n);
                _refs.Acquire([n]);
            }

            try
            {
                CommitProtocol.Commit(_storage, next, _limits, cancellationToken);
            }
            catch (Exception e)
            {
                _failed = e;
                throw;
            }

            AfterCommit(next);
            foreach (Shadow x in _shadows)
            {
                x.IsNew = false;
            }

            MaybeBackfillLocked();
            if (_options.AutoMerge)
            {
                MaybeMergeLocked();
            }

            return next.Generation;
        }
    }

    /// <summary>Publishes every change since the last commit (see <see cref="Commit(CancellationToken)"/>).</summary>
    /// <param name="cancellationToken">Cancels before the commit file is written.</param>
    /// <returns>The generation now durable.</returns>
    public ValueTask<long> CommitAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Commit(cancellationToken));

    /// <summary>Opens a snapshot of the newest commit; its files stay until it is disposed.</summary>
    /// <returns>The snapshot.</returns>
    /// <exception cref="InvalidOperationException">Nothing has been committed yet.</exception>
    public IndexSnapshot OpenSnapshot()
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            CommitInfo commit = _current ?? throw new InvalidOperationException("Nothing has been committed yet.");
            List<string> names = FilesOf(commit);
            // A lease as well as the in-process count: a reader process reclaiming superseded files looks only at leases.
            ReaderLease? lease = _storage.SupportsLocks ? IndexLocks.AcquireLease(_storage, default, out _) : null;
            _refs.Acquire(names);
            return new IndexSnapshot(_storage, commit, _schema, _codecs, _limits, () =>
            {
                lease?.Dispose();
                _refs.Release(names);
            });
        }
    }

    /// <summary>Opens a snapshot of the newest commit.</summary>
    /// <returns>The snapshot.</returns>
    public ValueTask<IndexSnapshot> OpenSnapshotAsync() => ValueTask.FromResult(OpenSnapshot());

    /// <summary>
    /// Deletes files that were kept only because a reader in another process pinned them and that reader has since gone. The writer
    /// does this at every commit and when it closes; call it to reclaim sooner.
    /// </summary>
    public void ReclaimFiles()
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            _refs.ReclaimDeferred();
        }
    }

    /// <summary>Asks the merge policy for merges now and queues them.</summary>
    public void MaybeMerge()
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            MaybeMergeLocked();
        }
    }

    /// <summary>Waits for queued merges (a cooperative scheduler runs them now).</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="IOException">A merge failed; the index is unchanged by it.</exception>
    public void WaitForMerges(CancellationToken cancellationToken = default)
    {
        _scheduler.WaitForIdle(cancellationToken);
        lock (_gate)
        {
            if (_mergeError is { } e)
            {
                _mergeError = null;
                throw new IOException("A merge failed.", e);
            }
        }
    }

    /// <summary>Waits for queued merges.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A completed task.</returns>
    public ValueTask WaitForMergesAsync(CancellationToken cancellationToken = default)
    {
        WaitForMerges(cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Merges until at most <paramref name="maxSegments"/> segments remain in the working set, installing the results at once (not
    /// committed; call <see cref="Commit(CancellationToken)"/> to publish).
    /// </summary>
    /// <param name="maxSegments">The target, at least 1.</param>
    /// <param name="cancellationToken">Cancels between merges.</param>
    public void ForceMerge(int maxSegments = 1, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSegments, 1);
        CompleteSchemaEvolution(cancellationToken);
        Flush(cancellationToken);
        WaitForMerges(cancellationToken);
        while (true)
        {
            lock (_gate)
            {
                ThrowIfUnusable();
                InstallMergesLocked();
                DropEmptySegmentsLocked();
                var idle = _segments.Where(s => !_busy.Contains(s.Info.Name)).OrderBy(s => s.Info.DocCount - s.DeletedCount).ThenBy(s => s.Info.Name, StringComparer.Ordinal).ToList();
                bool rewrite = idle.Count == 1 && maxSegments == 1 && idle[0].DeletedCount > 0 && _segments.Count == 1;
                if (_segments.Count <= maxSegments && !rewrite)
                {
                    return;
                }

                int take = Math.Min(Math.Max(2, _segments.Count - maxSegments + 1), _options.MergePolicy.MaxMergeAtOnce);
                if (rewrite)
                {
                    take = 1;
                }

                if (idle.Count < take)
                {
                    return;
                }

                StartMergeLocked(idle.Take(take).OrderBy(s => s.Info.Name, StringComparer.Ordinal).ToList());
            }

            WaitForMerges(cancellationToken);
        }
    }

    /// <summary>Discards uncommitted work, cancels merges and releases handles. The last commit is untouched.</summary>
    public void Dispose() => DisposeCore(releaseLock: true);

    private void DisposeCore(bool releaseLock)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _closing.Cancel();
        _scheduler.Dispose();
        lock (_gate)
        {
            if (_failed is null)
            {
                // Uncommitted outputs (flushed segments, finished merges, delete files) are garbage no commit names.
                _refs.Release(_pending.ToList());
                _refs.ReclaimDeferred();
            }

            _pending.Clear();
            foreach (WorkSegment s in _segments)
            {
                s.Reader?.Dispose();
                s.Ids?.Dispose();
            }

            _segments.Clear();
        }

        _closing.Dispose();
        if (releaseLock)
        {
            _writerLock?.Dispose();
        }
    }

    /// <summary>Disposes the writer.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static void ValidateCodecs(IReadOnlyList<ISegmentCodec> codecs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ISegmentCodec codec in codecs)
        {
            foreach (string part in codec.Parts)
            {
                if (CodecRegistry.NewestVersion(part) == 0 || part is SegmentParts.Ids or SegmentParts.Deletes)
                {
                    throw new ArgumentException($"Codec '{codec.Name}' claims part '{part}', which is not a codec part.", nameof(codecs));
                }

                if (!seen.Add(part))
                {
                    throw new ArgumentException($"Part '{part}' is claimed by more than one codec.", nameof(codecs));
                }
            }
        }
    }

    internal static List<string> FilesOf(CommitInfo commit)
    {
        var names = new List<string> { CommitFileNames.For(commit.Generation) };
        foreach (SegmentInfo s in commit.Segments)
        {
            foreach (SegmentFile f in s.Files)
            {
                names.Add(f.FileName);
            }
        }

        if (commit.Pending is { } pending)
        {
            foreach (RebuiltSegment r in pending.Rebuilt)
            {
                foreach (SegmentFile f in r.Info.Files)
                {
                    names.Add(f.FileName);
                }
            }
        }

        return names;
    }

    private static bool HasValue(Document d, string field, string value)
    {
        foreach (DocumentField f in d.Fields)
        {
            if (string.Equals(f.Name, field, StringComparison.Ordinal) && f.Value.Kind == FieldValueKind.Text && string.Equals(f.Value.AsText(), value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed is not null)
        {
            throw new InvalidOperationException("The writer stopped after a storage failure; open the index again.", _failed);
        }
    }

    private string NewSegmentName() => $"s{_nextSegmentId++:x16}";

    private void Recover(CommitInfo? commit, bool resumePending, CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        if (commit is not null)
        {
            referenced.UnionWith(FilesOf(commit));
        }

        // Always look at the leases: it also deletes those of readers that died, so a quiet index does not collect them.
        HashSet<string>? pinned = IndexLocks.LivePinnedFiles(_storage, _limits, cancellationToken);
        foreach (string name in _storage.List(cancellationToken))
        {
            // A segment name is never reused, even when its files are leftovers about to be deleted: a deleted name that a
            // crash brings back must not collide with a new segment of the same name.
            if (name.Length > 17 && name[0] == 's' && name[17] == '.'
                && ulong.TryParse(name.AsSpan(1, 16), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out ulong used))
            {
                _nextSegmentId = Math.Max(_nextSegmentId, used + 1);
            }

            if (!referenced.Contains(name) && IsOwnedFile(name))
            {
                // A reader in another process may still be reading an older commit; its files go later (ReclaimFiles).
                if (pinned is not null && pinned.Contains(name))
                {
                    _refs.Defer(name);
                    continue;
                }

                _storage.Delete(name, cancellationToken);
            }
        }

        if (commit is null)
        {
            return;
        }

        _current = commit;
        _nextSegmentId = Math.Max(Math.Max(commit.NextSegmentId, _nextSegmentId), 1);
        _appliedPosition = commit.AppliedPosition.ToArray();
        _userData = new SortedDictionary<string, string>(commit.UserData.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        _refs.Acquire(FilesOf(commit));
        foreach (SegmentInfo info in commit.Segments)
        {
            _segments.Add(new WorkSegment(info));
        }

        if (commit.Pending is { } pending && resumePending)
        {
            foreach (RebuiltSegment r in pending.Rebuilt)
            {
                _shadows.Add(new Shadow(r.Info, r.Source, r.SourceDeleted, isNew: false));
            }

            PruneShadowsLocked();
        }
    }

    private bool SegmentSetChanged()
    {
        if (_current is null || _current.Segments.Count != _segments.Count)
        {
            return true;
        }

        for (int i = 0; i < _segments.Count; i++)
        {
            if (!ReferenceEquals(_current.Segments[i], _segments[i].Info))
            {
                return true;
            }
        }

        return false;
    }

    private void AfterCommit(CommitInfo next)
    {
        List<string> nextFiles = FilesOf(next);
        _refs.Acquire(nextFiles);
        _refs.Release(_pending.ToList());
        _pending.Clear();
        if (_current is not null)
        {
            _refs.Release(FilesOf(_current));
        }

        _current = next;
        for (int i = 0; i < _segments.Count; i++)
        {
            _segments[i].Info = next.Segments[i];
            _segments[i].Dirty = false;
            _segments[i].IsNew = false;
        }

        _metaDirty = false;
        _refs.ReclaimDeferred();
    }

    private SegmentInfo WriteDeletes(WorkSegment seg, List<string> written, CancellationToken cancellationToken)
    {
        long generation = seg.Info.DeleteGeneration + 1;
        string segmentName = seg.Info.Name;
        var sink = new SegmentFileSink(_storage, segmentName, null, _ => $"{segmentName}.del{generation}");
        try
        {
            byte[] body = DeleteFileFormat.SerializeBody(seg.Info.DocCount, seg.Words!);
            using SegmentFileBuilder file = sink.Create(SegmentParts.Deletes, "OIXD", DeleteFileFormat.FormatVersion, cancellationToken);
            file.Write(body, cancellationToken);
            file.Complete(cancellationToken);
        }
        catch
        {
            foreach (string n in sink.CreatedNames)
            {
                _refs.TryDelete(n);
            }

            throw;
        }

        written.AddRange(sink.CreatedNames);
        var files = new List<SegmentFile>(seg.Info.Files.Where(f => f.Part != SegmentParts.Deletes)) { sink.Files[0] };
        return new SegmentInfo(segmentName, seg.Info.DocCount, seg.DeletedCount, generation, files, seg.Info.SchemaEpoch);
    }

    private void DropEmptySegmentsLocked()
    {
        for (int i = _segments.Count - 1; i >= 0; i--)
        {
            WorkSegment s = _segments[i];
            if (s.Info.DocCount == s.DeletedCount && !_busy.Contains(s.Info.Name))
            {
                RemoveSegment(i);
            }
        }
    }

    private void RemoveSegment(int index)
    {
        WorkSegment s = _segments[index];
        s.Reader?.Dispose();
        s.Ids?.Dispose();
        _segments.RemoveAt(index);
        if (s.IsNew)
        {
            // Never committed: its only reference is the pending one.
            List<string> names = s.Info.Files.Select(f => f.FileName).Where(_pending.Contains).ToList();
            foreach (string n in names)
            {
                _pending.Remove(n);
            }

            _refs.Release(names);
        }
    }

    private void FlushLocked(CancellationToken cancellationToken)
    {
        if (_bufferLive == 0)
        {
            _buffer.Clear();
            _bufferIds.Clear();
            return;
        }

        var docs = new List<Document>(_bufferLive);
        foreach (Document? d in _buffer)
        {
            if (d is not null)
            {
                docs.Add(d);
            }
        }

        string name = NewSegmentName();
        var sink = new SegmentFileSink(_storage, name);
        try
        {
            IReadOnlyList<Document> expanded = DynamicFields.Expand(_schema, docs, _limits, out Schema flushSchema);
            _schema = flushSchema;
            var context = new SegmentFlushContext(flushSchema, name, expanded, _limits);
            foreach (ISegmentCodec codec in _codecs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                codec.Flush(context, sink, cancellationToken);
            }

            if (_schema.IdField is { } idField)
            {
                var entries = new List<KeyValuePair<byte[], int>>(docs.Count);
                for (int i = 0; i < docs.Count; i++)
                {
                    entries.Add(new KeyValuePair<byte[], int>(Encoding.UTF8.GetBytes(docs[i].Get(idField).AsText()), i));
                }

                IdsFormat.Write(sink, entries, cancellationToken);
            }
        }
        catch
        {
            foreach (string n in sink.CreatedNames)
            {
                _refs.TryDelete(n);
            }

            throw;
        }

        var info = new SegmentInfo(name, docs.Count, 0, 0, sink.Files, _schemaEpoch);
        AddNewSegment(info, _segments.Count);
        _buffer.Clear();
        _bufferIds.Clear();
        _bufferLive = 0;
    }

    private void AddNewSegment(SegmentInfo info, int position)
    {
        var seg = new WorkSegment(info) { IsNew = true };
        List<string> names = info.Files.Select(f => f.FileName).ToList();
        foreach (string n in names)
        {
            _pending.Add(n);
        }

        _refs.Acquire(names);
        _segments.Insert(position, seg);
    }

    private bool DeleteByIdLocked(string id)
    {
        if (_bufferIds.TryGetValue(id, out int at))
        {
            RemoveFromBuffer(at);
            return true;
        }

        for (int i = _segments.Count - 1; i >= 0; i--)
        {
            WorkSegment seg = _segments[i];
            IdsReader? ids = EnsureIds(seg);
            if (ids is null || seg.Info.DocCount == seg.DeletedCount)
            {
                continue;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(id);
            if (ids.TryFind(bytes, out int doc) && MarkDeleted(seg, doc))
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveFromBuffer(int index)
    {
        Document d = _buffer[index]!;
        _changeStamp++;
        _buffer[index] = null;
        _bufferLive--;
        if (_schema.IdField is { } idField)
        {
            string id = d.Get(idField).AsText();
            if (_bufferIds.TryGetValue(id, out int at) && at == index)
            {
                _bufferIds.Remove(id);
            }
        }
    }

    private bool MarkDeleted(WorkSegment seg, int doc)
    {
        if (seg.Words is null)
        {
            seg.Words = seg.Info.DeleteGeneration > 0
                ? SegmentReader.LoadLiveDocs(_storage, seg.Info, _limits).ToWords()
                : new ulong[(seg.Info.DocCount + 63) >> 6];
            seg.DeletedCount = seg.Info.DeletedCount;
        }

        ulong bit = 1UL << (doc & 63);
        if ((seg.Words[doc >> 6] & bit) != 0)
        {
            return false;
        }

        _changeStamp++;
        seg.Words[doc >> 6] |= bit;
        seg.DeletedCount++;
        seg.Dirty = true;
        return true;
    }

    private IdsReader? EnsureIds(WorkSegment seg)
    {
        if (seg.Ids is not null)
        {
            return seg.Ids;
        }

        var context = new SegmentReaderContext(_storage, _schema, seg.Info, _limits);
        IRangeReader? file = context.OpenPart(SegmentParts.Ids, out _);
        if (file is null)
        {
            return null;
        }

        seg.Ids = new IdsReader(file, seg.Info.DocCount, _limits);
        return seg.Ids;
    }

    private SegmentReader EnsureReader(WorkSegment seg, CancellationToken cancellationToken)
        => seg.Reader ??= SegmentReader.Open(_storage, _schema, seg.Info, _codecs, _limits, LiveDocs.AllLive(seg.Info.DocCount), cancellationToken);

    private sealed class WorkSegment(SegmentInfo info)
    {
        public SegmentInfo Info { get; set; } = info;

        public bool IsNew { get; set; }

        public bool Dirty { get; set; }

        public ulong[]? Words { get; set; }

        public int DeletedCount { get; set; } = info.DeletedCount;

        public IdsReader? Ids { get; set; }

        public SegmentReader? Reader { get; set; }
    }
}
