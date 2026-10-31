namespace OneIndex.Storage;

/// <summary>A read-only view of another storage: every mutating member throws <see cref="NotSupportedException"/>.</summary>
/// <param name="inner">The storage to read from.</param>
public sealed class ReadOnlyStorage(IStorage inner) : IStorage
{
    private readonly IStorage _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool Exists(string name, CancellationToken cancellationToken = default) => _inner.Exists(name, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => _inner.ExistsAsync(name, cancellationToken);

    /// <inheritdoc/>
    public IRangeReader OpenRead(string name, CancellationToken cancellationToken = default) => _inner.OpenRead(name, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IRangeReader> OpenReadAsync(string name, CancellationToken cancellationToken = default) => _inner.OpenReadAsync(name, cancellationToken);

    /// <inheritdoc/>
    public IReadOnlyList<string> List(CancellationToken cancellationToken = default) => _inner.List(cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => _inner.ListAsync(cancellationToken);

    /// <inheritdoc/>
    public IStorageWriter Create(string name, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc/>
    public ValueTask<IStorageWriter> CreateAsync(string name, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc/>
    public void Rename(string source, string destination, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc/>
    public ValueTask RenameAsync(string source, string destination, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc/>
    public void Delete(string name, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc/>
    public void SyncDirectory(CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc/>
    public ValueTask SyncDirectoryAsync(CancellationToken cancellationToken = default) => throw ReadOnly();

    private static NotSupportedException ReadOnly() => new("The storage is read-only.");
}
