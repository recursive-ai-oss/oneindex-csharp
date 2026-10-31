namespace OneIndex.Storage;

/// <summary>
/// A starting point for backends whose metadata operations are cheap and local: the asynchronous members of every operation
/// except reads and writes run the synchronous member and return a completed task. A backend that does real asynchronous work
/// overrides the members it needs.
/// </summary>
public abstract class StorageBase : IStorage
{
    /// <inheritdoc/>
    public abstract bool IsReadOnly { get; }

    /// <inheritdoc/>
    public abstract bool Exists(string name, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract IRangeReader OpenRead(string name, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract IStorageWriter Create(string name, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract void Rename(string source, string destination, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract void Delete(string name, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract IReadOnlyList<string> List(CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract void SyncDirectory(CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public virtual ValueTask<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Exists(name, cancellationToken));

    /// <inheritdoc/>
    public virtual ValueTask<IRangeReader> OpenReadAsync(string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(OpenRead(name, cancellationToken));

    /// <inheritdoc/>
    public virtual ValueTask<IStorageWriter> CreateAsync(string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Create(name, cancellationToken));

    /// <inheritdoc/>
    public virtual ValueTask RenameAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        Rename(source, destination, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Delete(name, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(List(cancellationToken));

    /// <inheritdoc/>
    public virtual ValueTask SyncDirectoryAsync(CancellationToken cancellationToken = default)
    {
        SyncDirectory(cancellationToken);
        return ValueTask.CompletedTask;
    }
}
