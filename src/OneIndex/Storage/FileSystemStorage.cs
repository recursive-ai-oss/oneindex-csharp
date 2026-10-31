using Microsoft.Win32.SafeHandles;

namespace OneIndex.Storage;

/// <summary>
/// Files in a local directory, read with positional reads (no memory mapping) and written append-only with an explicit fsync.
/// Several processes may read the directory while one writes.
/// <para>
/// <b>Directory durability.</b> .NET cannot open a directory to fsync it, so <see cref="SyncDirectory"/> fsyncs each file that
/// was created or renamed into place since the last call. On Linux file systems with ordered metadata journals (ext4, XFS,
/// btrfs) that commits the directory entry together with the file; on macOS it is a full sync; on Windows NTFS journals
/// metadata and the flush covers it. Deletions are not forced to disk, which is harmless: a deleted file that reappears after a
/// crash is garbage no commit refers to. See docs/storage.md.
/// </para>
/// </summary>
public sealed class FileSystemStorage : StorageBase
{
    private const int WriteBufferSize = 64 * 1024;

    private readonly string _directory;
    private readonly bool _readOnly;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _unsyncedNames = new(StringComparer.Ordinal);

    /// <summary>Opens a directory as storage.</summary>
    /// <param name="directory">The directory path.</param>
    /// <param name="readOnly">Whether to reject mutations; a read-only storage does not create the directory.</param>
    /// <exception cref="DirectoryNotFoundException">The directory is missing and <paramref name="readOnly"/> is set.</exception>
    public FileSystemStorage(string directory, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = Path.GetFullPath(directory);
        _readOnly = readOnly;
        if (readOnly)
        {
            if (!Directory.Exists(_directory))
            {
                throw new DirectoryNotFoundException($"No such directory: {_directory}");
            }
        }
        else
        {
            Directory.CreateDirectory(_directory);
        }
    }

    /// <inheritdoc/>
    public override bool IsReadOnly => _readOnly;

    /// <inheritdoc/>
    public override bool Exists(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return File.Exists(PathOf(name));
    }

    /// <inheritdoc/>
    public override IRangeReader OpenRead(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = PathOf(name);
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"No such file: {name}", name);
        }

        return new FileReader(name, handle);
    }

    /// <inheritdoc/>
    public override IStorageWriter Create(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle handle = File.OpenHandle(PathOf(name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);
        lock (_gate)
        {
            _unsyncedNames.Add(name);
        }

        return new FileWriter(name, handle);
    }

    /// <inheritdoc/>
    public override void Rename(string source, string destination, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        cancellationToken.ThrowIfCancellationRequested();
        string from = PathOf(source);
        if (!File.Exists(from))
        {
            throw new FileNotFoundException($"No such file: {source}", source);
        }

        // rename(2) on POSIX and MoveFileEx(REPLACE_EXISTING) on Windows replace the destination in one step.
        File.Move(from, PathOf(destination), overwrite: true);
        lock (_gate)
        {
            _unsyncedNames.Remove(source);
            _unsyncedNames.Add(destination);
        }
    }

    /// <inheritdoc/>
    public override void Delete(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PathOf(name));
        lock (_gate)
        {
            _unsyncedNames.Remove(name);
        }
    }

    /// <inheritdoc/>
    public override IReadOnlyList<string> List(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = new List<string>();
        foreach (string path in Directory.EnumerateFiles(_directory))
        {
            string name = Path.GetFileName(path);
            if (StorageNames.IsValid(name))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <inheritdoc/>
    public override void SyncDirectory(CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        string[] pending;
        lock (_gate)
        {
            pending = [.. _unsyncedNames];
            _unsyncedNames.Clear();
        }

        foreach (string name in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using SafeFileHandle handle = File.OpenHandle(PathOf(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                RandomAccess.FlushToDisk(handle);
            }
            catch (FileNotFoundException)
            {
                // Renamed away or deleted since: nothing left to make durable under this name.
            }
        }
    }

    private string PathOf(string name)
    {
        StorageNames.Validate(name);
        return Path.Combine(_directory, name);
    }

    private void ThrowIfReadOnly()
    {
        if (_readOnly)
        {
            throw new NotSupportedException("The storage is read-only.");
        }
    }

    private sealed class FileReader(string name, SafeFileHandle handle) : IRangeReader
    {
        private readonly long _length = RandomAccess.GetLength(handle);

        public string Name => name;

        public long Length => _length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            int total = 0;
            while (total < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = RandomAccess.Read(handle, destination[total..], offset + total);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            int total = 0;
            while (total < destination.Length)
            {
                int n = await RandomAccess.ReadAsync(handle, destination[total..], offset + total, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }

        public void Dispose() => handle.Dispose();
    }

    private sealed class FileWriter(string name, SafeFileHandle handle) : IStorageWriter
    {
        private readonly byte[] _buffer = new byte[WriteBufferSize];
        private int _buffered;
        private long _flushed;
        private bool _disposed;

        public string Name => name;

        public long Position => _flushed + _buffered;

        public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length <= _buffer.Length - _buffered)
            {
                data.CopyTo(_buffer.AsSpan(_buffered));
                _buffered += data.Length;
                return;
            }

            FlushBuffer();
            if (data.Length >= _buffer.Length)
            {
                RandomAccess.Write(handle, data, _flushed);
                _flushed += data.Length;
            }
            else
            {
                data.CopyTo(_buffer);
                _buffered = data.Length;
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length <= _buffer.Length - _buffered)
            {
                data.Span.CopyTo(_buffer.AsSpan(_buffered));
                _buffered += data.Length;
                return;
            }

            await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
            if (data.Length >= _buffer.Length)
            {
                await RandomAccess.WriteAsync(handle, data, _flushed, cancellationToken).ConfigureAwait(false);
                _flushed += data.Length;
            }
            else
            {
                data.Span.CopyTo(_buffer);
                _buffered = data.Length;
            }
        }

        public void Sync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            FlushBuffer();
            RandomAccess.FlushToDisk(handle);
        }

        public async ValueTask SyncAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await FlushBufferAsync(cancellationToken).ConfigureAwait(false);

            // .NET has no asynchronous fsync, so the flush below blocks.
            cancellationToken.ThrowIfCancellationRequested();
            RandomAccess.FlushToDisk(handle);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                FlushBuffer();
            }
            finally
            {
                _disposed = true;
                handle.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await FlushBufferAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _disposed = true;
                handle.Dispose();
            }
        }

        private void FlushBuffer()
        {
            if (_buffered > 0)
            {
                RandomAccess.Write(handle, _buffer.AsSpan(0, _buffered), _flushed);
                _flushed += _buffered;
                _buffered = 0;
            }
        }

        private async ValueTask FlushBufferAsync(CancellationToken cancellationToken)
        {
            if (_buffered > 0)
            {
                await RandomAccess.WriteAsync(handle, _buffer.AsMemory(0, _buffered), _flushed, cancellationToken).ConfigureAwait(false);
                _flushed += _buffered;
                _buffered = 0;
            }
        }
    }
}
