namespace OneIndex.Storage;

/// <summary>
/// An exclusive lock on a named file of a storage, held until it is disposed or the owning process dies. It is how one writer
/// keeps a second writer out and how a reader process announces to the writer that it still uses a commit (a lease). The operating
/// system releases the lock when the process exits or is killed, so a crashed holder never blocks anyone. See docs/processes.md.
/// </summary>
public interface IStorageLock : IDisposable
{
    /// <summary>The name of the locked file.</summary>
    string Name { get; }
}
