namespace OneIndex.Storage;

/// <summary>
/// A flat set of named files with range reads and durable, ordered publication. Every backend (memory, local file, read-only
/// HTTP range, browser storage) and every wrapper (fault injector, block transform) implements this. Names follow
/// <see cref="StorageNames"/>. Each operation has a synchronous and an asynchronous form and takes a cancellation token.
/// A read-only backend reports <see cref="IsReadOnly"/> and throws <see cref="NotSupportedException"/> from every mutating member.
/// </summary>
public interface IStorage
{
    /// <summary>Whether the storage rejects every mutation.</summary>
    bool IsReadOnly { get; }

    /// <summary>Whether a file exists.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    bool Exists(string name, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Exists"/>.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    ValueTask<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Opens a file for range reads.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A reader the caller disposes.</returns>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    IRangeReader OpenRead(string name, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="OpenRead"/>.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A reader the caller disposes.</returns>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    ValueTask<IRangeReader> OpenReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Creates a new, empty file for appending.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A writer the caller disposes.</returns>
    /// <exception cref="IOException">A file with that name exists.</exception>
    IStorageWriter Create(string name, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Create"/>.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A writer the caller disposes.</returns>
    /// <exception cref="IOException">A file with that name exists.</exception>
    ValueTask<IStorageWriter> CreateAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a file, atomically replacing any file at the destination: a reader sees the old file or the new one, never a mix,
    /// and after a crash the destination is one of the two. The rename is durable only after <see cref="SyncDirectory"/>.
    /// </summary>
    /// <param name="source">The existing file.</param>
    /// <param name="destination">The new name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    void Rename(string source, string destination, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Rename"/>.</summary>
    /// <param name="source">The existing file.</param>
    /// <param name="destination">The new name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the rename is done.</returns>
    ValueTask RenameAsync(string source, string destination, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file. Deleting a missing file is not an error. Readers already open keep working where the platform allows.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    void Delete(string name, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Delete"/>.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the file is deleted.</returns>
    ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists every file name.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The names in ordinal order.</returns>
    IReadOnlyList<string> List(CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="List"/>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The names in ordinal order.</returns>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes every creation, rename and deletion done so far durable (the directory fsync). Call it after the files of a commit are
    /// synced and again after the commit file is renamed into place. Where the platform cannot sync a directory the backend does
    /// the strongest thing it can (see docs/storage.md).
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    void SyncDirectory(CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="SyncDirectory"/>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the namespace changes are durable.</returns>
    ValueTask SyncDirectoryAsync(CancellationToken cancellationToken = default);
}
