using System.Buffers;

namespace OneIndex.Storage;

/// <summary>
/// Applies an <see cref="IBlockTransform"/> to every byte read from or written to another storage. File names, lengths and
/// the namespace operations pass through unchanged, so everything above sees plain bytes. With the identity transform it is a
/// pass-through.
/// </summary>
/// <param name="inner">The storage holding the transformed bytes.</param>
/// <param name="transform">The transform; <see cref="IdentityBlockTransform.Instance"/> when <see langword="null"/>.</param>
public sealed class TransformingStorage(IStorage inner, IBlockTransform? transform = null) : IStorage
{
    private const int Chunk = 64 * 1024;

    private readonly IStorage _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IBlockTransform _transform = transform ?? IdentityBlockTransform.Instance;

    /// <inheritdoc/>
    public bool IsReadOnly => _inner.IsReadOnly;

    /// <inheritdoc/>
    public bool SupportsLocks => _inner.SupportsLocks;

    /// <inheritdoc/>
    public IStorageLock? TryLock(string name, CancellationToken cancellationToken = default) => _inner.TryLock(name, cancellationToken);

    /// <inheritdoc/>
    public bool Exists(string name, CancellationToken cancellationToken = default) => _inner.Exists(name, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => _inner.ExistsAsync(name, cancellationToken);

    /// <inheritdoc/>
    public IRangeReader OpenRead(string name, CancellationToken cancellationToken = default) => new Reader(_inner.OpenRead(name, cancellationToken), _transform);

    /// <inheritdoc/>
    public async ValueTask<IRangeReader> OpenReadAsync(string name, CancellationToken cancellationToken = default)
        => new Reader(await _inner.OpenReadAsync(name, cancellationToken).ConfigureAwait(false), _transform);

    /// <inheritdoc/>
    public IStorageWriter Create(string name, CancellationToken cancellationToken = default) => new Writer(_inner.Create(name, cancellationToken), _transform);

    /// <inheritdoc/>
    public async ValueTask<IStorageWriter> CreateAsync(string name, CancellationToken cancellationToken = default)
        => new Writer(await _inner.CreateAsync(name, cancellationToken).ConfigureAwait(false), _transform);

    /// <inheritdoc/>
    public void Rename(string source, string destination, CancellationToken cancellationToken = default) => _inner.Rename(source, destination, cancellationToken);

    /// <inheritdoc/>
    public ValueTask RenameAsync(string source, string destination, CancellationToken cancellationToken = default) => _inner.RenameAsync(source, destination, cancellationToken);

    /// <inheritdoc/>
    public void Delete(string name, CancellationToken cancellationToken = default) => _inner.Delete(name, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => _inner.DeleteAsync(name, cancellationToken);

    /// <inheritdoc/>
    public IReadOnlyList<string> List(CancellationToken cancellationToken = default) => _inner.List(cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => _inner.ListAsync(cancellationToken);

    /// <inheritdoc/>
    public void SyncDirectory(CancellationToken cancellationToken = default) => _inner.SyncDirectory(cancellationToken);

    /// <inheritdoc/>
    public ValueTask SyncDirectoryAsync(CancellationToken cancellationToken = default) => _inner.SyncDirectoryAsync(cancellationToken);

    private sealed class Reader(IRangeReader inner, IBlockTransform transform) : IRangeReader
    {
        public string Name => inner.Name;

        public long Length => inner.Length;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            int n = inner.Read(offset, destination, cancellationToken);
            transform.Decode(offset, destination[..n]);
            return n;
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            int n = await inner.ReadAsync(offset, destination, cancellationToken).ConfigureAwait(false);
            transform.Decode(offset, destination.Span[..n]);
            return n;
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class Writer(IStorageWriter inner, IBlockTransform transform) : IStorageWriter
    {
        public string Name => inner.Name;

        public long Position => inner.Position;

        public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
        {
            byte[] scratch = ArrayPool<byte>.Shared.Rent(Math.Min(data.Length, Chunk));
            try
            {
                while (!data.IsEmpty)
                {
                    int n = Math.Min(data.Length, Chunk);
                    Span<byte> part = scratch.AsSpan(0, n);
                    data[..n].CopyTo(part);
                    transform.Encode(inner.Position, part);
                    inner.Write(part, cancellationToken);
                    data = data[n..];
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            byte[] scratch = ArrayPool<byte>.Shared.Rent(Math.Min(data.Length, Chunk));
            try
            {
                while (!data.IsEmpty)
                {
                    int n = Math.Min(data.Length, Chunk);
                    data.Span[..n].CopyTo(scratch);
                    transform.Encode(inner.Position, scratch.AsSpan(0, n));
                    await inner.WriteAsync(scratch.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    data = data[n..];
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        public void Sync(CancellationToken cancellationToken = default) => inner.Sync(cancellationToken);

        public ValueTask SyncAsync(CancellationToken cancellationToken = default) => inner.SyncAsync(cancellationToken);

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
