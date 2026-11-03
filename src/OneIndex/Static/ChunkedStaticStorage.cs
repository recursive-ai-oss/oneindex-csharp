using System.Net;
using System.Net.Http;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.StaticIndexes;

/// <summary>Where the objects of a static index come from: whole objects by name, over HTTP or from a directory.</summary>
public interface IStaticSource
{
    /// <summary>Fetches one object (the manifest or a chunk).</summary>
    /// <param name="objectName">The object's name.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="FileNotFoundException">There is no such object.</exception>
    ValueTask<byte[]> FetchAsync(string objectName, CancellationToken cancellationToken = default);

    /// <summary>Total bytes returned by <see cref="FetchAsync"/> so far.</summary>
    long BytesFetched { get; }

    /// <summary>Number of <see cref="FetchAsync"/> calls so far.</summary>
    int Requests { get; }
}

/// <summary>A static index read over HTTP: <c>GET base/object</c> for each chunk. Needs no range support from the server.</summary>
public sealed class HttpStaticSource : IStaticSource
{
    private readonly HttpClient _client;
    private readonly Uri _base;
    private readonly long _maxObjectBytes;
    private long _bytes;
    private int _requests;

    /// <summary>Creates a source.</summary>
    /// <param name="client">The client; it is not disposed.</param>
    /// <param name="baseUri">The address of the static index's directory.</param>
    /// <param name="maxObjectBytes">The largest object accepted, a guard against a hostile server. Default 64 MiB.</param>
    public HttpStaticSource(HttpClient client, Uri baseUri, long maxObjectBytes = 64L << 20)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(baseUri);
        _client = client;
        string b = baseUri.AbsoluteUri;
        _base = new Uri(b.EndsWith('/') ? b : b + "/");
        _maxObjectBytes = maxObjectBytes;
    }

    /// <inheritdoc />
    public long BytesFetched => Interlocked.Read(ref _bytes);

    /// <inheritdoc />
    public int Requests => Volatile.Read(ref _requests);

    /// <inheritdoc />
    public async ValueTask<byte[]> FetchAsync(string objectName, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(objectName);
        Interlocked.Increment(ref _requests);
        using HttpResponseMessage response = await _client.GetAsync(new Uri(_base, objectName), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"The static index has no object '{objectName}'.", objectName);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } len && len > _maxObjectBytes)
        {
            throw new CorruptIndexException(objectName, $"the server sent {len} bytes for one chunk");
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > _maxObjectBytes)
        {
            throw new CorruptIndexException(objectName, $"the server sent {bytes.Length} bytes for one chunk");
        }

        Interlocked.Add(ref _bytes, bytes.Length);
        return bytes;
    }
}

/// <summary>A static index read from a local directory; for tests and for serving a built index to oneself.</summary>
public sealed class DirectoryStaticSource : IStaticSource
{
    private readonly string _dir;
    private long _bytes;
    private int _requests;

    /// <summary>Creates a source.</summary>
    /// <param name="directory">The static index's directory.</param>
    public DirectoryStaticSource(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _dir = directory;
    }

    /// <inheritdoc />
    public long BytesFetched => Interlocked.Read(ref _bytes);

    /// <inheritdoc />
    public int Requests => Volatile.Read(ref _requests);

    /// <inheritdoc />
    public async ValueTask<byte[]> FetchAsync(string objectName, CancellationToken cancellationToken = default)
    {
        StorageNames.Validate(objectName);
        Interlocked.Increment(ref _requests);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(_dir, objectName), cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _bytes, bytes.Length);
        return bytes;
    }
}

/// <summary>Thrown by a synchronous read of a chunk that has not been fetched yet; <see cref="StaticIndex{T}"/> catches it, fetches the chunk and runs the query again.</summary>
public sealed class ChunkNotLoadedException : Exception
{
    internal ChunkNotLoadedException(string file, int chunk)
        : base($"Chunk {chunk} of '{file}' is not loaded.")
    {
        File = file;
        Chunk = chunk;
    }

    /// <summary>The file.</summary>
    public string File { get; }

    /// <summary>The chunk number.</summary>
    public int Chunk { get; }
}

/// <summary>
/// A read-only storage over the chunks of a static index. A synchronous read is answered from the chunks already fetched and otherwise
/// throws <see cref="ChunkNotLoadedException"/>; an asynchronous read fetches what it needs. Every chunk is checked against the manifest's CRC-32C
/// when it arrives.
/// </summary>
public sealed class ChunkedStaticStorage : IStorage, IDeferredOpenStorage
{
    private readonly IStaticSource _source;
    private readonly Dictionary<string, StaticFile> _files;
    private readonly Dictionary<(string File, int Chunk), byte[]> _cache = [];
    private readonly Queue<(string File, int Chunk)> _order = new();
    private readonly Lock _gate = new();
    private long _cached;

    private ChunkedStaticStorage(IStaticSource source, StaticManifest manifest, long maxCacheBytes)
    {
        _source = source;
        Manifest = manifest;
        MaxCacheBytes = maxCacheBytes;
        _files = manifest.Files.ToDictionary(static f => f.Name, StringComparer.Ordinal);
    }

    /// <summary>The manifest.</summary>
    public StaticManifest Manifest { get; }

    /// <summary>Chunks beyond this many bytes are dropped oldest first. Default 128 MiB.</summary>
    public long MaxCacheBytes { get; }

    /// <summary>The source's counters.</summary>
    public IStaticSource Source => _source;

    /// <summary>Opens a static index: fetches and validates its manifest.</summary>
    /// <param name="source">Where the objects come from.</param>
    /// <param name="maxCacheBytes">The chunk cache size. Default 128 MiB.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The storage.</returns>
    /// <exception cref="CorruptIndexException">The manifest is invalid.</exception>
    public static async ValueTask<ChunkedStaticStorage> OpenAsync(IStaticSource source, long maxCacheBytes = 128L << 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] json = await source.FetchAsync(StaticManifest.FileName, cancellationToken).ConfigureAwait(false);
        return new ChunkedStaticStorage(source, StaticManifest.Parse(json), maxCacheBytes);
    }

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool Exists(string name, CancellationToken cancellationToken = default) => _files.ContainsKey(name);

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult(Exists(name, cancellationToken));

    /// <inheritdoc />
    public IReadOnlyList<string> List(CancellationToken cancellationToken = default) => [.. _files.Keys.Order(StringComparer.Ordinal)];

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(List(cancellationToken));

    /// <inheritdoc />
    public IRangeReader OpenRead(string name, CancellationToken cancellationToken = default) => new Reader(this, Find(name));

    /// <inheritdoc />
    public ValueTask<IRangeReader> OpenReadAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult<IRangeReader>(new Reader(this, Find(name)));

    /// <inheritdoc />
    public IStorageWriter Create(string name, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public ValueTask<IStorageWriter> CreateAsync(string name, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public void Rename(string source, string destination, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public ValueTask RenameAsync(string source, string destination, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public void Delete(string name, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public void SyncDirectory(CancellationToken cancellationToken = default) => throw ReadOnly();

    /// <inheritdoc />
    public ValueTask SyncDirectoryAsync(CancellationToken cancellationToken = default) => throw ReadOnly();

    private static NotSupportedException ReadOnly() => new("A static index is read-only.");

    private StaticFile Find(string name)
        => _files.TryGetValue(name, out StaticFile? f) ? f : throw new FileNotFoundException($"The static index has no file '{name}'.", name);

    /// <summary>Fetches the chunks that cover a range, unless they are already cached.</summary>
    /// <param name="name">The file.</param>
    /// <param name="offset">First byte.</param>
    /// <param name="length">Number of bytes.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>A task that completes when the range can be read synchronously.</returns>
    public async ValueTask PrefetchAsync(string name, long offset, int length, CancellationToken cancellationToken = default)
    {
        StaticFile file = Find(name);
        (int first, int last) = ChunkSpan(file, offset, length);
        for (int c = first; c <= last; c++)
        {
            await LoadAsync(file, c, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Fetches one chunk (checked against the manifest) unless it is cached.</summary>
    /// <param name="name">The file.</param>
    /// <param name="chunk">The chunk number.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>A task that completes when the chunk is cached.</returns>
    public async ValueTask LoadChunkAsync(string name, int chunk, CancellationToken cancellationToken = default)
        => await LoadAsync(Find(name), chunk, cancellationToken).ConfigureAwait(false);

    private (int First, int Last) ChunkSpan(StaticFile file, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > file.Size || length > file.Size - offset)
        {
            throw new CorruptIndexException(file.Name, $"range {offset}+{length} lies outside the file of {file.Size} bytes");
        }

        if (length == 0)
        {
            return (0, -1);
        }

        int size = Manifest.ChunkSize(file);
        return ((int)(offset / size), (int)((offset + length - 1) / size));
    }

    private async ValueTask LoadAsync(StaticFile file, int chunk, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cache.ContainsKey((file.Name, chunk)))
            {
                return;
            }
        }

        byte[] bytes = await _source.FetchAsync(StaticManifest.ChunkName(file, chunk), ct).ConfigureAwait(false);
        int size = Manifest.ChunkSize(file);
        long expected = Math.Min(size, file.Size - ((long)chunk * size));
        if (bytes.Length != expected || Crc32C.Compute(bytes) != file.ChunkCrcs[chunk])
        {
            throw new CorruptIndexException(file.Name, $"chunk {chunk} does not match the manifest");
        }

        lock (_gate)
        {
            if (_cache.TryAdd((file.Name, chunk), bytes))
            {
                _order.Enqueue((file.Name, chunk));
                _cached += bytes.Length;
                while (_cached > MaxCacheBytes && _order.Count > 1)
                {
                    (string f, int c) = _order.Dequeue();
                    if (_cache.Remove((f, c), out byte[]? dropped))
                    {
                        _cached -= dropped.Length;
                    }
                }
            }
        }
    }

    private bool TryCopy(StaticFile file, int chunk, int within, Span<byte> destination)
    {
        byte[]? bytes;
        lock (_gate)
        {
            _cache.TryGetValue((file.Name, chunk), out bytes);
        }

        if (bytes is null)
        {
            return false;
        }

        bytes.AsSpan(within, destination.Length).CopyTo(destination);
        return true;
    }

    private sealed class Reader(ChunkedStaticStorage owner, StaticFile file) : IRangeReader
    {
        public string Name => file.Name;

        public long Length => file.Size;

        public int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset >= file.Size || destination.IsEmpty)
            {
                return 0;
            }

            int want = (int)Math.Min(destination.Length, file.Size - offset);
            int done = 0;
            int chunkBytes = owner.Manifest.ChunkSize(file);
            while (done < want)
            {
                long at = offset + done;
                int chunk = (int)(at / chunkBytes);
                int within = (int)(at % chunkBytes);
                int take = Math.Min(want - done, chunkBytes - within);
                if (!owner.TryCopy(file, chunk, within, destination.Slice(done, take)))
                {
                    throw new ChunkNotLoadedException(file.Name, chunk);
                }

                done += take;
            }

            return done;
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset >= file.Size || destination.IsEmpty)
            {
                return 0;
            }

            int want = (int)Math.Min(destination.Length, file.Size - offset);
            await owner.PrefetchAsync(file.Name, offset, want, cancellationToken).ConfigureAwait(false);
            return Read(offset, destination.Span[..want], cancellationToken);
        }

        public void Dispose()
        {
        }
    }
}
