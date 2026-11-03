using OneIndex.Storage;

namespace OneIndex.Browser;

/// <summary>
/// The few asynchronous file operations the browser's origin-private file system (OPFS) offers an application, as the host page provides them.
/// OneIndex has no JavaScript interop of its own (it must stay free of generated unsafe code), so the application implements this interface over
/// <c>navigator.storage.getDirectory()</c> with <c>[JSImport]</c> and hands it to <see cref="OpfsPersistence"/>.
/// </summary>
public interface IOpfsHost
{
    /// <summary>The names of the files in the directory, in any order.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The names.</returns>
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a whole file.</summary>
    /// <param name="name">The file's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    ValueTask<byte[]> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a whole file, replacing it; the write is complete (closed) when the task completes. The write must be atomic: a page that is closed or
    /// crashes half way leaves the old file or the new one, never a mixture. The File System Standard expects a writable stream's close to update the file
    /// atomically, so a host built on <c>createWritable()</c> meets this. A host that can leave a torn file can make the newest commit unreadable, which opens as a corruption error.
    /// </summary>
    /// <param name="name">The file's name.</param>
    /// <param name="bytes">The content.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the file is written.</returns>
    ValueTask WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file; a missing file is not an error.</summary>
    /// <param name="name">The file's name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the file is gone.</returns>
    ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps an index in the browser's OPFS between page loads. The engine writes synchronously and the browser's main thread cannot block on file
/// writes, so an index lives in a <see cref="MemoryStorage"/> while the page runs and is saved to, or loaded from, OPFS as a whole, file by file, through
/// the asynchronous host. Saving is crash-safe in the same way a commit is: the commit file (the one that makes a generation visible) is written last
/// and files no commit lists are not copied, so an interrupted save leaves either the previous commit or the new one.
/// </summary>
public static class OpfsPersistence
{
    /// <summary>Copies the files of a storage to OPFS and removes the files OPFS holds that the storage no longer has.</summary>
    /// <param name="source">The storage, usually an index's <see cref="MemoryStorage"/> after a commit.</param>
    /// <param name="host">The OPFS directory.</param>
    /// <param name="cancellationToken">Cancels between files.</param>
    /// <returns>A task that completes when OPFS holds the same files.</returns>
    public static async ValueTask SaveAsync(IStorage source, IOpfsHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(host);
        IReadOnlyList<string> have = await host.ListAsync(cancellationToken).ConfigureAwait(false);
        var existing = new HashSet<string>(have, StringComparer.Ordinal);
        var names = source.List(cancellationToken).Where(static n => !n.EndsWith(".lock", StringComparison.Ordinal) && !n.EndsWith(".lease", StringComparison.Ordinal)).ToList();
        // Segment files first, commit files last: a reader never sees a commit whose files are missing.
        foreach (string name in names.Where(static n => !IsCommit(n)).Concat(names.Where(static n => IsCommit(n))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existing.Contains(name) && !IsCommit(name))
            {
                continue; // segment files are immutable: same name, same bytes
            }

            using IRangeReader r = source.OpenRead(name, cancellationToken);
            var bytes = new byte[r.Length];
            r.ReadExactly(0, bytes, cancellationToken);
            await host.WriteAsync(name, bytes, cancellationToken).ConfigureAwait(false);
        }

        var keep = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (string stale in have.Where(n => !keep.Contains(n)))
        {
            await host.DeleteAsync(stale, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Copies every file OPFS holds into a storage.</summary>
    /// <param name="host">The OPFS directory.</param>
    /// <param name="destination">An empty, writable storage (normally a new <see cref="MemoryStorage"/>).</param>
    /// <param name="cancellationToken">Cancels between files.</param>
    /// <returns>The number of files loaded.</returns>
    public static async ValueTask<int> LoadAsync(IOpfsHost host, IStorage destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(destination);
        IReadOnlyList<string> names = await host.ListAsync(cancellationToken).ConfigureAwait(false);
        int count = 0;
        foreach (string name in names.OrderBy(static n => IsCommit(n) ? 1 : 0).ThenBy(static n => n, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageNames.Validate(name);
            byte[] bytes = await host.ReadAsync(name, cancellationToken).ConfigureAwait(false);
            using IStorageWriter w = destination.Create(name, cancellationToken);
            w.Write(bytes, cancellationToken);
            w.Sync(cancellationToken);
            count++;
        }

        return count;
    }

    private static bool IsCommit(string name) => name.StartsWith("commit-", StringComparison.Ordinal);
}
