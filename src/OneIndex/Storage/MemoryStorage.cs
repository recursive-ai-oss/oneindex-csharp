namespace OneIndex.Storage;

/// <summary>
/// Files held in memory: for in-memory indexes, tests and the browser. Thread-safe. Nothing is ever durable beyond the process,
/// so <see cref="SyncDirectory"/> and writer syncs do nothing. A reader keeps seeing the bytes of the file it opened even if the
/// file is renamed over or deleted afterwards.
/// </summary>
public sealed class MemoryStorage : StorageBase
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MemoryFile> _files = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override bool IsReadOnly => false;

    /// <inheritdoc/>
    public override bool Exists(string name, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(name);
        lock (_gate)
        {
            return _files.ContainsKey(name);
        }
    }

    /// <inheritdoc/>
    public override IRangeReader OpenRead(string name, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(name);
        cancellationToken.ThrowIfCancellationRequested();
        MemoryFile file;
        lock (_gate)
        {
            if (!_files.TryGetValue(name, out file!))
            {
                throw new FileNotFoundException($"No such file: {name}", name);
            }
        }

        byte[] data;
        int length;
        lock (file)
        {
            data = file.Data;
            length = file.Length;
        }

        return new MemoryReader(name, data, length);
    }

    /// <inheritdoc/>
    public override IStorageWriter Create(string name, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(name);
        cancellationToken.ThrowIfCancellationRequested();
        var file = new MemoryFile();
        lock (_gate)
        {
            if (!_files.TryAdd(name, file))
            {
                throw new IOException($"File already exists: {name}");
            }
        }

        return new MemoryWriter(name, file);
    }

    /// <inheritdoc/>
    public override void Rename(string source, string destination, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(source, nameof(source));
        StorageNames.Validate(destination, nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_files.Remove(source, out MemoryFile? file))
            {
                throw new FileNotFoundException($"No such file: {source}", source);
            }

            _files[destination] = file;
        }
    }

    /// <inheritdoc/>
    public override void Delete(string name, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _files.Remove(name);
        }
    }

    /// <inheritdoc/>
    public override IReadOnlyList<string> List(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] names;
        lock (_gate)
        {
            names = [.. _files.Keys];
        }

        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    /// <inheritdoc/>
    public override void SyncDirectory(CancellationToken cancellationToken = default) => cancellationToken.ThrowIfCancellationRequested();

    private sealed class MemoryFile
    {
        public byte[] Data = [];
        public int Length;
    }

    private sealed class MemoryReader(string name, byte[] data, int length) : IRangeReader
    {
        public string Name => name;

        public long Length => length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= length)
            {
                return 0;
            }

            int n = (int)Math.Min(destination.Length, length - offset);
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(offset, destination.Span, cancellationToken));
        }

        public void Dispose()
        {
        }
    }

    private sealed class MemoryWriter(string name, MemoryFile file) : IStorageWriter
    {
        private bool _disposed;

        public string Name => name;

        public long Position
        {
            get
            {
                lock (file)
                {
                    return file.Length;
                }
            }
        }

        public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            lock (file)
            {
                int needed = checked(file.Length + data.Length);
                if (needed > file.Data.Length)
                {
                    // Grow into a new array so readers holding the old one keep a stable view.
                    var grown = new byte[Math.Max(needed, Math.Min(int.MaxValue / 2, file.Data.Length * 2))];
                    file.Data.AsSpan(0, file.Length).CopyTo(grown);
                    file.Data = grown;
                }

                data.CopyTo(file.Data.AsSpan(file.Length));
                file.Length = needed;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Write(data.Span, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public void Sync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public ValueTask SyncAsync(CancellationToken cancellationToken = default)
        {
            Sync(cancellationToken);
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
