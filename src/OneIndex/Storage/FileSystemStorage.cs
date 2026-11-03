using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace OneIndex.Storage;

/// <summary>
/// Files in a local directory, written append-only with an explicit fsync. Reads of files of 64 KiB or more are copied out of a
/// read-only memory mapping on Unix, so a cached range costs a copy and no system call; smaller files, Windows (where a mapped file
/// cannot be deleted by a merge) and any file that cannot be mapped use positional reads. Callers never see the difference.
/// Several processes may read the directory while one writes.
/// <para>
/// <b>Directory durability.</b> .NET cannot open a directory to fsync it, so <see cref="SyncDirectory"/> fsyncs each file that
/// was created or renamed into place since the last call. On Linux file systems with ordered metadata journals (ext4, XFS,
/// btrfs) that commits the directory entry together with the file; on macOS it is a full sync; on Windows NTFS journals
/// metadata and the flush covers it. Deletions are not forced to disk, which is harmless: a deleted file that reappears after a
/// crash is garbage no commit refers to. See docs/storage.md.
/// </para>
/// </summary>
public sealed class FileSystemStorage : StorageBase, IStorage
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
            // Windows: a synchronous read on a handle opened for overlapped I/O allocates on every call (the runtime sets up an
            // overlapped operation and waits on it), so readers there use an ordinary handle and their async twin completes inline.
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, OperatingSystem.IsWindows() ? FileOptions.None : FileOptions.Asynchronous);
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
        SafeFileHandle handle = File.OpenHandle(PathOf(name), FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous);
        lock (_gate)
        {
            _unsyncedNames.Add(name);
        }

        return new FileWriter(name, handle, Synced);
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

        string to = PathOf(destination);
        try
        {
            // rename(2) on POSIX and MoveFileEx(REPLACE_EXISTING) on Windows replace the destination in one step.
            File.Move(from, to, overwrite: true);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException && File.Exists(to))
        {
            // Windows refuses MoveFileEx over a destination that another handle has open. ReplaceFile swaps the two names
            // atomically and works when that handle allows sharing delete, as every reader of this storage does.
            File.Replace(from, to, null);
        }

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
    public bool SupportsLocks => !_readOnly;

    /// <inheritdoc/>
    public bool SupportsConcurrentSync => !OperatingSystem.IsBrowser();

    /// <summary>
    /// Opens the file with <see cref="FileShare.None"/>. On Linux and macOS the runtime takes a non-blocking <c>flock</c> on the
    /// open file, on Windows the share mode is enforced by the kernel; both are released when the process dies. The lock is
    /// per open handle, so it also excludes a second storage object of the same process.
    /// </summary>
    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The storage is read-only, or file locking is switched off (<c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>), which would make the lock a silent no-op.</exception>
    public IStorageLock? TryLock(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        cancellationToken.ThrowIfCancellationRequested();
        string path = PathOf(name);
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception e) when (e is IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
        {
            // A sharing violation (Windows) or EWOULDBLOCK from flock (Unix) surfaces as a plain IOException.
            return null;
        }

        try
        {
            // If locking is disabled the open above ignores the share mode; a second exclusive open then succeeds. Refuse rather than
            // let two writers believe they are alone.
            SafeFileHandle? second = null;
            try
            {
                second = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
            }

            if (second is not null)
            {
                second.Dispose();
                throw new NotSupportedException("File locking is switched off in this process (DOTNET_SYSTEM_IO_DISABLEFILELOCKING), so index locks cannot work.");
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new FileLock(name, handle);
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

    // A file synced after it was created needs no second flush for its name: on the journalling file systems this storage relies on
    // (see the class remarks) that flush already committed the directory entry. A later rename adds the new name again.
    private void Synced(string name)
    {
        lock (_gate)
        {
            _unsyncedNames.Remove(name);
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

    private sealed class FileLock(string name, SafeFileHandle handle) : IStorageLock
    {
        public string Name => name;

        public void Dispose() => handle.Dispose();
    }

    private sealed class FileReader : IRangeReader
    {
        // Below this size the mapping costs more than the few reads it saves.
        private const long MapThreshold = 64 * 1024;

        private readonly string _name;
        private readonly SafeFileHandle _handle;
        private readonly long _length;
        private readonly MemoryMappedFile? _file;
        private readonly MemoryMappedViewAccessor? _view;

        public FileReader(string name, SafeFileHandle handle)
        {
            _name = name;
            _handle = handle;
            _length = RandomAccess.GetLength(handle);
            if (_length >= MapThreshold && !OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser())
            {
                try
                {
                    _file = MemoryMappedFile.CreateFromFile(handle, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                    _view = _file.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OutOfMemoryException)
                {
                    // Mapping is an optimization: fall back to positional reads.
                    _view?.Dispose();
                    _file?.Dispose();
                    _view = null;
                    _file = null;
                }
            }
        }

        public string Name => _name;

        public long Length => _length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (_view is not null)
            {
                // Immutable files: the mapping covers exactly the length seen at open, and a range past it reads short, like a pread.
                long available = _length - offset;
                int count = available <= 0 ? 0 : (int)Math.Min(destination.Length, available);
                if (count > 0)
                {
                    _view.SafeMemoryMappedViewHandle.ReadSpan((ulong)offset, destination[..count]);
                }

                return count;
            }

            int total = 0;
            while (total < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = RandomAccess.Read(_handle, destination[total..], offset + total);
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
            if (_view is not null || OperatingSystem.IsWindows())
            {
                // A mapped range is a copy from memory, and a Windows reader has no overlapped handle: nothing to wait for.
                return Read(offset, destination.Span, cancellationToken);
            }

            int total = 0;
            while (total < destination.Length)
            {
                int n = await RandomAccess.ReadAsync(_handle, destination[total..], offset + total, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }

        public void Dispose()
        {
            _view?.Dispose();
            _file?.Dispose();
            _handle.Dispose();
        }
    }

    private sealed class FileWriter(string name, SafeFileHandle handle, Action<string> synced) : IStorageWriter
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
            synced(name);
        }

        public async ValueTask SyncAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await FlushBufferAsync(cancellationToken).ConfigureAwait(false);

            // .NET has no asynchronous fsync, so the flush below blocks.
            cancellationToken.ThrowIfCancellationRequested();
            RandomAccess.FlushToDisk(handle);
            synced(name);
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
