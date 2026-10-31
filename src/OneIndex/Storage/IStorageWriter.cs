namespace OneIndex.Storage;

/// <summary>
/// Append-only writer for one new file. Bytes are durable only after <see cref="Sync"/> returns; disposing the writer hands
/// buffered bytes to the operating system but promises nothing about a crash. The file's name becomes durable only after
/// <see cref="IStorage.SyncDirectory"/>. A writer is used by one thread at a time.
/// </summary>
public interface IStorageWriter : IDisposable, IAsyncDisposable
{
    /// <summary>The file name.</summary>
    string Name { get; }

    /// <summary>The number of bytes written so far, which is the offset of the next byte.</summary>
    long Position { get; }

    /// <summary>Appends <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to append.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Write"/>.</summary>
    /// <param name="data">The bytes to append; they must stay valid until the task completes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are accepted.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Flushes buffered bytes and forces them to stable storage (fsync).</summary>
    /// <param name="cancellationToken">Cancels the sync.</param>
    void Sync(CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Sync"/>.</summary>
    /// <param name="cancellationToken">Cancels the sync.</param>
    /// <returns>A task that completes when the bytes are durable.</returns>
    ValueTask SyncAsync(CancellationToken cancellationToken = default);
}
