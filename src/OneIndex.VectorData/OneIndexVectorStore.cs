using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.VectorData;

namespace OneIndex.VectorData;

/// <summary>
/// A vector store whose collections are OneIndex indexes: one directory per collection under a root directory, or memory only.
/// Records are your own classes marked with the abstraction's attributes (<see cref="VectorStoreKeyAttribute"/>,
/// <see cref="VectorStoreDataAttribute"/>, <see cref="VectorStoreVectorAttribute"/>) or described by a <see cref="VectorStoreCollectionDefinition"/>.
/// </summary>
/// <remarks>
/// Every write is committed before the call returns, so a record that an upsert acknowledged is durable and visible to the next read.
/// Collection names are limited to letters, digits, <c>.</c>, <c>_</c> and <c>-</c> (they are directory names).
/// </remarks>
public sealed class OneIndexVectorStore : VectorStore
{
    private readonly string? _root;
    private readonly Dictionary<string, object> _open = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates a store whose collections live under a directory (created when missing).</summary>
    /// <param name="directory">The root directory.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or empty.</exception>
    public OneIndexVectorStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _root = Path.GetFullPath(directory);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Creates a store that keeps its collections in memory; they disappear with the store.</summary>
    public OneIndexVectorStore()
    {
    }

    internal string? Root => _root;

    /// <inheritdoc />
    [RequiresDynamicCode("Records are read and written through reflection over their properties.")]
    [RequiresUnreferencedCode("Records are read and written through reflection over their properties.")]
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null)
        => new OneIndexRecords<TKey, TRecord>(this, name, definition);

    /// <inheritdoc />
    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition)
        => throw new NotSupportedException("Dynamic collections are not supported; use GetCollection with a record class.");

    /// <inheritdoc />
    public override async IAsyncEnumerable<string> ListCollectionNamesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> names;
        if (_root is null)
        {
            lock (_gate)
            {
                names = [.. _open.Keys];
            }
        }
        else
        {
            names = [.. Directory.EnumerateDirectories(_root).Select(static d => Path.GetFileName(d)).Where(static n => n.Length > 0 && !n.StartsWith('.'))];
        }

        names.Sort(StringComparer.Ordinal);
        foreach (string n in names)
        {
            yield return n;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateName(name);
        if (_root is null)
        {
            return Task.FromResult(Exists(name));
        }

        return Task.FromResult(Directory.Exists(Path.Combine(_root, name)));
    }

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateName(name);
        Forget(name);
        if (_root is not null)
        {
            string dir = Path.Combine(_root, name);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                throw new ArgumentException($"The collection name '{name}' may use only letters, digits, '.', '_' and '-'.", nameof(name));
            }
        }

        if (name[0] == '.')
        {
            throw new ArgumentException("A collection name cannot start with a dot.", nameof(name));
        }
    }

    internal bool Exists(string name)
    {
        lock (_gate)
        {
            if (_open.ContainsKey(name))
            {
                return true;
            }
        }

        return _root is not null && Directory.Exists(Path.Combine(_root, name));
    }

    /// <summary>Finds the open index of a collection or opens it; one index per name, shared by every collection object of that name.</summary>
    internal SearchIndex<TRecord> GetIndex<TRecord>(string name, SearchMapping<TRecord> mapping, bool create)
        where TRecord : class
    {
        lock (_gate)
        {
            if (_open.TryGetValue(name, out object? existing))
            {
                return existing as SearchIndex<TRecord> ?? throw new VectorStoreException($"The collection '{name}' is already open with a different record type.");
            }

            if (!create && !Exists(name))
            {
                throw new VectorStoreException($"The collection '{name}' does not exist.") { CollectionName = name };
            }

            SearchIndex<TRecord> index = _root is null ? SearchIndex.InMemoryMapped(mapping) : SearchIndex.OpenMapped(Path.Combine(_root, name), mapping);
            _open[name] = index;
            return index;
        }
    }

    internal void Forget(string name)
    {
        lock (_gate)
        {
            if (_open.Remove(name, out object? idx))
            {
                ((IDisposable)idx).Dispose();
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                foreach (object idx in _open.Values)
                {
                    ((IDisposable)idx).Dispose();
                }

                _open.Clear();
            }
        }

        base.Dispose(disposing);
    }
}
