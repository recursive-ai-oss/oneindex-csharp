namespace OneIndex.Storage;

/// <summary>What a simulated power cut does to data and names that were not made durable.</summary>
public enum CrashPolicy
{
    /// <summary>Only fsynced bytes and directory-synced names survive; everything else is lost.</summary>
    LoseUnsynced,

    /// <summary>
    /// Unsynced writes reach the disk in order up to a point; the write at that point is torn (only a prefix of it survives) and
    /// later writes are lost. Unsynced namespace changes survive as a prefix of the order they were made in.
    /// </summary>
    Tear,

    /// <summary>
    /// Every unsynced write independently survives whole, survives torn or is lost, so a later write can persist while an earlier
    /// one does not (the gap reads as zeros). Every unsynced namespace change independently survives or not.
    /// </summary>
    Reorder,
}

/// <summary>The kinds of mutating operation the fault injector counts.</summary>
public enum StorageOperationKind
{
    /// <summary>A file was created.</summary>
    Create,

    /// <summary>Bytes were appended.</summary>
    Write,

    /// <summary>A file was fsynced.</summary>
    Sync,

    /// <summary>A file was renamed.</summary>
    Rename,

    /// <summary>A file was deleted.</summary>
    Delete,

    /// <summary>The directory was synced.</summary>
    SyncDirectory,
}

/// <summary>One mutating operation seen by <see cref="FaultInjectingStorage"/>.</summary>
/// <param name="Index">The zero-based position of the operation in the schedule.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Name">The file name (the source name for a rename).</param>
/// <param name="Offset">For a write, the file offset of the first byte; otherwise 0.</param>
/// <param name="Length">For a write, the byte count; otherwise 0.</param>
public readonly record struct StorageOperationRecord(long Index, StorageOperationKind Kind, string Name, long Offset, int Length);

/// <summary>The seeded schedule for a <see cref="FaultInjectingStorage"/>.</summary>
public sealed class FaultOptions
{
    /// <summary>Seeds every random choice made when a crash is applied. The same seed gives the same surviving bytes.</summary>
    public int Seed { get; init; }

    /// <summary>
    /// The zero-based index of the mutating operation at which the power fails, or -1 for never. The operation does not complete
    /// (a write is recorded as an unsynced write first, so it may survive torn); it throws <see cref="SimulatedCrashException"/>.
    /// </summary>
    public long CrashAtOperation { get; init; } = -1;

    /// <summary>Indexes of operations that fail with an <see cref="IOException"/> and have no effect.</summary>
    public IReadOnlyCollection<long> FailAtOperations { get; init; } = [];

    /// <summary>What a crash does to unsynced data. Default <see cref="CrashPolicy.LoseUnsynced"/>.</summary>
    public CrashPolicy Policy { get; init; } = CrashPolicy.LoseUnsynced;
}

/// <summary>The simulated power failed. Call <see cref="FaultInjectingStorage.Crash"/> to see what survived.</summary>
public sealed class SimulatedCrashException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="operationIndex">The operation at which the power failed.</param>
    public SimulatedCrashException(long operationIndex)
        : base($"Simulated crash at storage operation {operationIndex}")
    {
        OperationIndex = operationIndex;
    }

    /// <summary>The operation at which the power failed.</summary>
    public long OperationIndex { get; }
}

/// <summary>
/// Wraps a storage and models a disk with a volatile cache: it sees every write, rename, delete and sync, tracks which bytes and
/// names are durable, and on a (simulated) power cut leaves the wrapped storage holding only what a real disk might hold:
/// synced bytes, plus, by policy, torn or reordered unsynced ones. The wrapped storage must be writable. Reads pass through to
/// the live (volatile) state, as an operating system's cache would serve them.
/// <para>
/// The model: a file's bytes become durable at that file's <c>Sync</c>; a name (creation, rename, deletion) becomes durable at
/// <c>SyncDirectory</c>; a rename is atomic. This is deliberately strict: a protocol that survives it survives the common file
/// systems. Use: run the code under test until <see cref="SimulatedCrashException"/> (or until done), call <see cref="Crash"/>,
/// then open the wrapped storage directly and check the invariants.
/// </para>
/// </summary>
public sealed class FaultInjectingStorage : IStorage
{
    private readonly IStorage _inner;
    private readonly FaultOptions _options;
    private readonly Lock _gate = new();
    private readonly List<StorageOperationRecord> _log = [];
    private readonly List<NamespaceOp> _pending = [];
    private SortedDictionary<string, Inode> _live = new(StringComparer.Ordinal);
    private SortedDictionary<string, Inode> _durable = new(StringComparer.Ordinal);
    private long _operations;
    private bool _crashed;

    /// <summary>Wraps <paramref name="inner"/>. Files already in it are treated as durable.</summary>
    /// <param name="inner">The writable storage to wrap.</param>
    /// <param name="options">The schedule; a default schedule never faults.</param>
    public FaultInjectingStorage(IStorage inner, FaultOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (inner.IsReadOnly)
        {
            throw new ArgumentException("The wrapped storage must be writable.", nameof(inner));
        }

        _options = options ?? new FaultOptions();
        foreach (string name in inner.List())
        {
            using IRangeReader reader = inner.OpenRead(name);
            var data = new byte[checked((int)reader.Length)];
            reader.ReadExactly(0, data);
            _live[name] = new Inode { Data = data, Length = data.Length, Synced = data.Length };
        }

        _durable = new SortedDictionary<string, Inode>(_live, StringComparer.Ordinal);
    }

    /// <summary>The number of mutating operations seen since the last <see cref="Crash"/> (or since construction).</summary>
    public long OperationCount
    {
        get
        {
            lock (_gate)
            {
                return _operations;
            }
        }
    }

    /// <summary>A copy of every mutating operation seen so far, in order.</summary>
    public IReadOnlyList<StorageOperationRecord> Operations
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>Whether the simulated power has failed and <see cref="Crash"/> has not been called yet.</summary>
    public bool HasCrashed
    {
        get
        {
            lock (_gate)
            {
                return _crashed;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <summary>
    /// Applies a power cut now: rewrites the wrapped storage to what the policy says survived, makes that state durable, and
    /// revives the storage for further use with the operation counter reset (the schedule's crash index does not fire again
    /// unless the counter reaches it once more).
    /// </summary>
    /// <param name="policy">Overrides <see cref="FaultOptions.Policy"/> for this crash.</param>
    public void Crash(CrashPolicy? policy = null)
    {
        lock (_gate)
        {
            CrashPolicy p = policy ?? _options.Policy;
            var rng = new Random(_options.Seed);

            // Names: start from the last directory sync and replay the surviving share of the later changes.
            var names = new SortedDictionary<string, Inode>(_durable, StringComparer.Ordinal);
            if (p == CrashPolicy.Tear)
            {
                int prefix = rng.Next(_pending.Count + 1);
                for (int i = 0; i < prefix; i++)
                {
                    Replay(names, _pending[i]);
                }
            }
            else if (p == CrashPolicy.Reorder)
            {
                foreach (NamespaceOp op in _pending)
                {
                    if (rng.Next(2) == 0)
                    {
                        Replay(names, op);
                    }
                }
            }

            // Bytes: each surviving file keeps its synced prefix plus the share of its unsynced writes the policy lets through.
            var survivors = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            var cache = new Dictionary<Inode, byte[]>(ReferenceEqualityComparer.Instance);
            foreach ((string name, Inode inode) in names)
            {
                if (!cache.TryGetValue(inode, out byte[]? bytes))
                {
                    bytes = Materialize(inode, p, rng);
                    cache[inode] = bytes;
                }

                survivors[name] = bytes;
            }

            foreach (string existing in _inner.List())
            {
                _inner.Delete(existing);
            }

            var fresh = new SortedDictionary<string, Inode>(StringComparer.Ordinal);
            var inodes = new Dictionary<byte[], Inode>(ReferenceEqualityComparer.Instance);
            foreach ((string name, byte[] bytes) in survivors)
            {
                using (IStorageWriter writer = _inner.Create(name))
                {
                    writer.Write(bytes);
                    writer.Sync();
                }

                if (!inodes.TryGetValue(bytes, out Inode? inode))
                {
                    inode = new Inode { Data = bytes, Length = bytes.Length, Synced = bytes.Length };
                    inodes[bytes] = inode;
                }

                fresh[name] = inode;
            }

            _inner.SyncDirectory();
            _live = fresh;
            _durable = new SortedDictionary<string, Inode>(fresh, StringComparer.Ordinal);
            _pending.Clear();
            _log.Clear();
            _operations = 0;
            _crashed = false;
        }
    }

    /// <inheritdoc/>
    public bool Exists(string name, CancellationToken cancellationToken = default)
    {
        CheckAlive();
        return _inner.Exists(name, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        CheckAlive();
        return _inner.ExistsAsync(name, cancellationToken);
    }

    /// <inheritdoc/>
    public IRangeReader OpenRead(string name, CancellationToken cancellationToken = default)
    {
        CheckAlive();
        return _inner.OpenRead(name, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<IRangeReader> OpenReadAsync(string name, CancellationToken cancellationToken = default)
    {
        CheckAlive();
        return _inner.OpenReadAsync(name, cancellationToken);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> List(CancellationToken cancellationToken = default)
    {
        CheckAlive();
        return _inner.List(cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        CheckAlive();
        return _inner.ListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public IStorageWriter Create(string name, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(name);
        lock (_gate)
        {
            Begin(StorageOperationKind.Create, name, 0, 0, applyBeforeCrash: false);
            if (_live.ContainsKey(name))
            {
                throw new IOException($"File already exists: {name}");
            }

            IStorageWriter inner = _inner.Create(name, cancellationToken);
            var inode = new Inode();
            _live[name] = inode;
            _pending.Add(new NamespaceOp(NamespaceOpKind.Create, name, null, inode));
            return new Writer(this, inner, inode, name);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IStorageWriter> CreateAsync(string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Create(name, cancellationToken));

    /// <inheritdoc/>
    public void Rename(string source, string destination, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(source, nameof(source));
        StorageNames.Validate(destination, nameof(destination));
        lock (_gate)
        {
            Begin(StorageOperationKind.Rename, source, 0, 0, applyBeforeCrash: false);
            if (!_live.Remove(source, out Inode? inode))
            {
                throw new FileNotFoundException($"No such file: {source}", source);
            }

            _inner.Rename(source, destination, cancellationToken);
            _live[destination] = inode;
            _pending.Add(new NamespaceOp(NamespaceOpKind.Rename, source, destination, null));
        }
    }

    /// <inheritdoc/>
    public ValueTask RenameAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        Rename(source, destination, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Delete(string name, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(name);
        lock (_gate)
        {
            Begin(StorageOperationKind.Delete, name, 0, 0, applyBeforeCrash: false);
            _inner.Delete(name, cancellationToken);
            if (_live.Remove(name))
            {
                _pending.Add(new NamespaceOp(NamespaceOpKind.Delete, name, null, null));
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Delete(name, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void SyncDirectory(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Begin(StorageOperationKind.SyncDirectory, string.Empty, 0, 0, applyBeforeCrash: false);
            _inner.SyncDirectory(cancellationToken);
            _durable = new SortedDictionary<string, Inode>(_live, StringComparer.Ordinal);
            _pending.Clear();
        }
    }

    /// <inheritdoc/>
    public ValueTask SyncDirectoryAsync(CancellationToken cancellationToken = default)
    {
        SyncDirectory(cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static void Replay(SortedDictionary<string, Inode> names, NamespaceOp op)
    {
        switch (op.Kind)
        {
            case NamespaceOpKind.Create:
                names[op.Name] = op.Inode!;
                break;
            case NamespaceOpKind.Rename:
                // A rename whose source never became visible has nothing to move.
                if (names.Remove(op.Name, out Inode? moved))
                {
                    names[op.Target!] = moved;
                }

                break;
            default:
                names.Remove(op.Name);
                break;
        }
    }

    private static byte[] Materialize(Inode inode, CrashPolicy policy, Random rng)
    {
        var kept = new List<(int Start, int Length)>();
        List<(int Start, int Length)> chunks = inode.Unsynced;
        if (policy == CrashPolicy.Tear)
        {
            int whole = rng.Next(chunks.Count + 1);
            for (int i = 0; i < whole; i++)
            {
                kept.Add(chunks[i]);
            }

            if (whole < chunks.Count)
            {
                (int start, int length) = chunks[whole];
                kept.Add((start, rng.Next(length)));
            }
        }
        else if (policy == CrashPolicy.Reorder)
        {
            foreach ((int start, int length) in chunks)
            {
                switch (rng.Next(3))
                {
                    case 1:
                        kept.Add((start, length));
                        break;
                    case 2:
                        kept.Add((start, rng.Next(length)));
                        break;
                    default:
                        break;
                }
            }
        }

        int end = inode.Synced;
        foreach ((int start, int length) in kept)
        {
            end = Math.Max(end, start + length);
        }

        var bytes = new byte[end];
        inode.Data.AsSpan(0, inode.Synced).CopyTo(bytes);
        foreach ((int start, int length) in kept)
        {
            inode.Data.AsSpan(start, length).CopyTo(bytes.AsSpan(start));
        }

        return bytes;
    }

    private void CheckAlive()
    {
        lock (_gate)
        {
            if (_crashed)
            {
                throw new SimulatedCrashException(_options.CrashAtOperation);
            }
        }
    }

    // Counts one mutating operation and applies the schedule. A crash marks the storage dead and throws, except for a write
    // (applyBeforeCrash): the caller records the bytes as unsynced first and then throws, so the write may survive torn.
    private bool Begin(StorageOperationKind kind, string name, long offset, int length, bool applyBeforeCrash)
    {
        if (_crashed)
        {
            throw new SimulatedCrashException(_options.CrashAtOperation);
        }

        long index = _operations++;
        _log.Add(new StorageOperationRecord(index, kind, name, offset, length));
        if (index == _options.CrashAtOperation)
        {
            if (applyBeforeCrash)
            {
                return true;
            }

            _crashed = true;
            throw new SimulatedCrashException(index);
        }

        if (_options.FailAtOperations.Contains(index))
        {
            throw new IOException($"Injected failure at storage operation {index} ({kind} {name})");
        }

        return false;
    }

    private enum NamespaceOpKind
    {
        Create,
        Rename,
        Delete,
    }

    private sealed record NamespaceOp(NamespaceOpKind Kind, string Name, string? Target, Inode? Inode);

    private sealed class Inode
    {
        public byte[] Data = [];
        public int Length;
        public int Synced;
        public List<(int Start, int Length)> Unsynced { get; } = [];
    }

    private sealed class Writer(FaultInjectingStorage owner, IStorageWriter inner, Inode inode, string name) : IStorageWriter
    {
        public string Name => name;

        public long Position => inner.Position;

        public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
        {
            lock (owner._gate)
            {
                bool crash = owner.Begin(StorageOperationKind.Write, name, inode.Length, data.Length, applyBeforeCrash: true);
                int needed = checked(inode.Length + data.Length);
                if (needed > inode.Data.Length)
                {
                    Array.Resize(ref inode.Data, Math.Max(needed, inode.Data.Length * 2));
                }

                data.CopyTo(inode.Data.AsSpan(inode.Length));
                if (data.Length > 0)
                {
                    inode.Unsynced.Add((inode.Length, data.Length));
                }

                inode.Length = needed;
                if (crash)
                {
                    owner._crashed = true;
                    throw new SimulatedCrashException(owner._options.CrashAtOperation);
                }

                inner.Write(data, cancellationToken);
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Write(data.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public void Sync(CancellationToken cancellationToken = default)
        {
            lock (owner._gate)
            {
                owner.Begin(StorageOperationKind.Sync, name, 0, 0, applyBeforeCrash: false);
                inner.Sync(cancellationToken);
                inode.Synced = inode.Length;
                inode.Unsynced.Clear();
            }
        }

        public ValueTask SyncAsync(CancellationToken cancellationToken = default)
        {
            Sync(cancellationToken);
            return ValueTask.CompletedTask;
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
