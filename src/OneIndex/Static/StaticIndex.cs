using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.StaticIndexes;

/// <summary>Settings of <see cref="StaticIndexBuilder"/>.</summary>
public sealed class StaticBuildOptions
{
    /// <summary>The chunk size in bytes, 1 KiB to 64 MiB. Default 16 KiB. This size applies to the files read in runs (the IVF and vector files); scattered files use <see cref="ScatteredChunkBytes"/>. A query fetches about the chunks it touches, so smaller chunks mean fewer bytes and more requests (docs/static-index.md has the measurements).</summary>
    public int ChunkBytes { get; init; } = 16 * 1024;

    /// <summary>
    /// The chunk size in bytes, 1 KiB to 64 MiB, of the files a query probes at scattered places: term dictionaries, postings, stored documents,
    /// field lengths, identifier tables, columns, points and small-part packs. A lookup in these touches a few bytes in many places, so a smaller chunk
    /// fetches less. Default 4 KiB. Set it equal to <see cref="ChunkBytes"/> for one size everywhere.
    /// </summary>
    public int ScatteredChunkBytes { get; init; } = 4 * 1024;

    /// <summary>The IVF settings of every vector field of the type. Default: cosine lists of 8-bit codes with full-precision rerank, about the square root of the count.</summary>
    public IvfOptions Ivf { get; init; } = new() { Storage = IvfStorage.EightBit };
}

/// <summary>
/// Builds a static index: a directory of immutable chunks that a browser (or any HTTP client) can search without a server process.
/// The directory holds the files of one commit of an ordinary index, cut into chunks with a manifest of checksums, plus an IVF file and an identifier table
/// per vector field. See docs/static-index.md.
/// </summary>
public static class StaticIndexBuilder
{
    /// <summary>Builds a static index from the newest commit of an index directory.</summary>
    /// <typeparam name="T">The document type of the index (its vector fields get an IVF file).</typeparam>
    /// <param name="indexPath">The index directory.</param>
    /// <param name="outDirectory">The directory to create; it must not already hold a manifest.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The manifest that was written.</returns>
    /// <exception cref="IOException">The output directory holds a static index already, or a file could not be read or written.</exception>
    /// <exception cref="CorruptIndexException">The source index is damaged.</exception>
    public static StaticManifest Build<T>(string indexPath, string outDirectory, StaticBuildOptions? options = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(indexPath);
        ArgumentException.ThrowIfNullOrEmpty(outDirectory);
        options ??= new StaticBuildOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ChunkBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ChunkBytes, 1 << 26);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ScatteredChunkBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ScatteredChunkBytes, 1 << 26);
        Directory.CreateDirectory(outDirectory);
        if (File.Exists(Path.Combine(outDirectory, StaticManifest.FileName)))
        {
            throw new IOException($"'{outDirectory}' already holds a static index; build into an empty directory.");
        }

        var source = new FileSystemStorage(indexPath, readOnly: true);
        long newest = -1;
        string? commit = null;
        foreach (string name in source.List(cancellationToken))
        {
            if (CommitFileNames.TryParse(name, out long gen) && gen > newest)
            {
                newest = gen;
                commit = name;
            }
        }

        if (commit is null)
        {
            throw new FileNotFoundException($"'{indexPath}' holds no committed index.");
        }

        var files = new List<StaticFile>();
        foreach (string name in source.List(cancellationToken))
        {
            bool skip = name.EndsWith(".lock", StringComparison.Ordinal) || name.EndsWith(".lease", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal)
                || (CommitFileNames.TryParse(name, out _) && name != commit);
            if (skip)
            {
                continue;
            }

            using IRangeReader reader = source.OpenRead(name, cancellationToken);
            files.Add(WriteChunks(outDirectory, name, IsScattered(name) ? options.ScatteredChunkBytes : options.ChunkBytes, options.ChunkBytes, reader.Length, (offset, buffer) => reader.ReadExactly(offset, buffer, cancellationToken)));
        }

        // Vector fields: an IVF file over the stored vectors, and the identifier of each vector.
        SearchMapping<T> mapping = SearchMappings.Require<T>();
        var vectorFields = mapping.Members.Where(static m => m.Type == FieldType.Vector).ToList();
        if (vectorFields.Count > 0)
        {
            using SearchIndex<T> index = SearchIndex.OpenReadOnly<T>(source);
            foreach (MappedMember m in vectorFields)
            {
                var vectors = new List<float>();
                var ids = new List<string>();
                var docIds = new List<int>();
                foreach ((T doc, int docId) in index.DocumentsWithIds())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Document d = mapping.ToDocument(doc);
                    FieldValue v = d.Get(m.Name);
                    if (v.Kind == FieldValueKind.Vector)
                    {
                        vectors.AddRange(v.AsVector().ToArray());
                        ids.Add(mapping.GetId(doc) ?? string.Empty);
                        docIds.Add(docId);
                    }
                }

                if (ids.Count == 0)
                {
                    continue;
                }

                IvfOptions o = options.Ivf;
                var withMetric = new IvfOptions { Metric = m.Metric, Lists = o.Lists, Iterations = o.Iterations, Seed = o.Seed, Storage = o.Storage, TrainingPerList = o.TrainingPerList };
                // The length comes from the stored vectors: an index built with another embedder may differ from the attribute.
                byte[] ivf = IvfBuilder.Build(vectors.ToArray(), vectors.Count / ids.Count, withMetric);
                files.Add(WriteChunks(outDirectory, IvfFileName(m.Name), options.ChunkBytes, options.ChunkBytes, ivf.Length, (offset, buffer) => ivf.AsSpan((int)offset, buffer.Length).CopyTo(buffer)));
                byte[] table = IdTable(ids);
                files.Add(WriteChunks(outDirectory, IdsFileName(m.Name), options.ScatteredChunkBytes, options.ChunkBytes, table.Length, (offset, buffer) => table.AsSpan((int)offset, buffer.Length).CopyTo(buffer)));

                // Hybrid search names documents by their id in the commit. When vector number i is document i (no deletes, every document has a
                // vector) nothing more is needed; otherwise a table of u32 document ids, one per vector number, says which is which.
                bool identity = true;
                for (int i = 0; i < docIds.Count && identity; i++)
                {
                    identity = docIds[i] == i;
                }

                if (!identity)
                {
                    var map = new byte[4 * docIds.Count];
                    for (int i = 0; i < docIds.Count; i++)
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(4 * i), (uint)docIds[i]);
                    }

                    files.Add(WriteChunks(outDirectory, DocsFileName(m.Name), options.ScatteredChunkBytes, options.ChunkBytes, map.Length, (offset, buffer) => map.AsSpan((int)offset, buffer.Length).CopyTo(buffer)));
                }
            }
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        var manifest = new StaticManifest(options.ChunkBytes, commit, files);
        string tmp = Path.Combine(outDirectory, StaticManifest.FileName + ".tmp");
        File.WriteAllBytes(tmp, manifest.ToJson());
        File.Move(tmp, Path.Combine(outDirectory, StaticManifest.FileName));
        return manifest;
    }

    internal static string IvfFileName(string field) => "ivf-" + Safe(field) + ".ivf";

    internal static string DocsFileName(string field) => "ivf-" + Safe(field) + ".docs";

    internal static string IdsFileName(string field) => "ivf-" + Safe(field) + ".ids";

    private static string Safe(string field)
    {
        var sb = new StringBuilder();
        foreach (char c in field)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }

        return sb.ToString();
    }

    // count (u32), then count+1 u32 end offsets, then the UTF-8 identifiers back to back: identifier i is bytes [end[i-1], end[i]) of the text.
    internal static byte[] IdTable(IReadOnlyList<string> ids)
    {
        var text = new MemoryStream();
        var ends = new List<uint>();
        foreach (string id in ids)
        {
            byte[] b = Encoding.UTF8.GetBytes(id);
            text.Write(b);
            ends.Add((uint)text.Length);
        }

        var result = new byte[4 + (4 * (ends.Count + 1)) + (int)text.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)ids.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 0);
        for (int i = 0; i < ends.Count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8 + (4 * i)), ends[i]);
        }

        text.GetBuffer().AsSpan(0, (int)text.Length).CopyTo(result.AsSpan(4 + (4 * (ends.Count + 1))));
        return result;
    }

    private static bool IsScattered(string name) => name.EndsWith(".terms", StringComparison.Ordinal) || name.EndsWith(".lengths", StringComparison.Ordinal)
        || name.EndsWith(".ids", StringComparison.Ordinal) || name.EndsWith(".columns", StringComparison.Ordinal) || name.EndsWith(".points", StringComparison.Ordinal)
        || name.EndsWith(".pack", StringComparison.Ordinal) || name.EndsWith(".postings", StringComparison.Ordinal) || name.EndsWith(".stored", StringComparison.Ordinal) || name.EndsWith(".docs", StringComparison.Ordinal);

    private static StaticFile WriteChunks(string dir, string name, int chunkBytes, int manifestChunkBytes, long length, Action<long, Span<byte>> read)
    {
        long chunks = length == 0 ? 0 : ((length - 1) / chunkBytes) + 1;
        var crcs = new List<uint>();
        var buffer = new byte[(int)Math.Min(chunkBytes, Math.Max(length, 1))];
        for (int c = 0; c < chunks; c++)
        {
            int n = (int)Math.Min(chunkBytes, length - ((long)c * chunkBytes));
            Span<byte> span = buffer.AsSpan(0, n);
            read((long)c * chunkBytes, span);
            crcs.Add(Crc32C.Compute(span));
            File.WriteAllBytes(Path.Combine(dir, StaticManifest.ChunkName(name, chunks, c)), span.ToArray());
        }

        return new StaticFile(name, length, crcs, chunkBytes == manifestChunkBytes ? 0 : chunkBytes);
    }
}

/// <summary>
/// Opens static indexes.
/// </summary>
public static class StaticIndex
{
    /// <summary>Opens a static index.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="source">The chunks.</param>
    /// <param name="embedder">Embeds query text for hybrid search; null for none.</param>
    /// <param name="maxCacheBytes">The chunk cache size. Default 128 MiB.</param>
    /// <param name="readAhead">Chunks fetched after a missing one. Default 1.</param>
    /// <param name="cancellationToken">Cancels the fetches.</param>
    /// <returns>The index.</returns>
    public static async ValueTask<StaticIndex<T>> OpenAsync<T>(IStaticSource source, IEmbedder? embedder = null, long maxCacheBytes = 128L << 20, int readAhead = 1, CancellationToken cancellationToken = default)
        where T : class
    {
        ChunkedStaticStorage storage = await ChunkedStaticStorage.OpenAsync(source, maxCacheBytes, cancellationToken).ConfigureAwait(false);
        return new StaticIndex<T>(storage, embedder, Math.Max(0, readAhead));
    }
}

/// <summary>
/// A static index opened over a source (HTTP in a browser). Queries are ordinary <see cref="SearchIndex{T}"/> queries run against the chunks:
/// whatever chunk a query needs and has not got is fetched, and the query runs again, so the results equal what the same index returns on a
/// server. Vector search uses the IVF file instead of the HNSW graph, which would need a request per graph hop.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed class StaticIndex<T>
    where T : class
{
    private readonly IEmbedder? _embedder;
    private readonly Dictionary<string, IvfIndex> _ivf = new(StringComparer.Ordinal);

    internal StaticIndex(ChunkedStaticStorage storage, IEmbedder? embedder, int readAhead)
    {
        Storage = storage;
        _embedder = embedder;
        ReadAhead = readAhead;
    }

    /// <summary>The chunk storage, with its counters.</summary>
    public ChunkedStaticStorage Storage { get; }

    /// <summary>Chunks fetched after the missing one, in the same file, for the IVF and vector files, which are read in runs; other files fetch only the chunk a query is missing. Default 1.</summary>
    public int ReadAhead { get; }

    /// <summary>
    /// How many IVF lists a hybrid search reads for its vector list, 1 or more (capped at the field's list count). Default 16 (about 0.89 of the top results of the same hybrid search with a graph vector list, on 30,000 abstracts; 8 gives 0.71, 32 gives 0.95). More lists find more of
    /// the true neighbours and fetch more bytes (a list holds about the square root of the vector count in vectors).
    /// </summary>
    public int HybridNprobe { get; set; } = 16;

    /// <summary>How many times queries ran against missing chunks and had to run again, since opening.</summary>
    public int Retries { get; private set; }

    /// <summary>Runs a query against the static index, fetching chunks as it needs them.</summary>
    /// <typeparam name="TResult">The result type. Materialise it inside <paramref name="query"/> (call <c>Execute()</c>, <c>ToList()</c>, ...): it runs again on every retry, and the index is closed when it returns.</typeparam>
    /// <param name="query">The query, for example <c>i =&gt; i.Search("fox").Where(d =&gt; d.Year &gt; 2000).Take(10).Execute()</c>.</param>
    /// <param name="cancellationToken">Cancels the fetches.</param>
    /// <returns>What <paramref name="query"/> returned on its final run.</returns>
    /// <exception cref="CorruptIndexException">A chunk does not match the manifest.</exception>
    public async ValueTask<TResult> QueryAsync<TResult>(Func<SearchIndex<T>, TResult> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        for (int round = 0; round < 100_000; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using SearchIndex<T> index = await SearchIndex.OpenReadOnlyAsync<T>(Storage, _embedder, cancellationToken).ConfigureAwait(false);
                index.ExternalVectorArm = new StaticVectorArm(Storage, _ivf, Math.Max(1, HybridNprobe));
                return query(index);
            }
            catch (Exception e) when (Missing(e) is { } miss)
            {
                Retries++;
                int last = ChunkCount(miss.File) - 1;
                for (int c = miss.Chunk; c <= Math.Min(last, miss.Chunk + (ReadsInRuns(miss.File) ? ReadAhead : 0)); c++)
                {
                    await Storage.LoadChunkAsync(miss.File, c, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException("The query kept asking for chunks; giving up after 100000 retries.");
    }

    // The IVF and vector files are read in runs (a probed list, a block of rows), so the next chunk is usually wanted too. Every other
    // file is probed at scattered places in small chunks, where a neighbouring chunk is mostly wasted bytes (measured: read-ahead on
    // postings and stored documents added 19 KB to a text query).
    private static bool ReadsInRuns(string file)
    {
        int dot = file.LastIndexOf(".c", StringComparison.Ordinal);
        string name = dot > 0 ? file[..dot] : file;
        return name.EndsWith(".ivf", StringComparison.Ordinal) || name.EndsWith(".vectors", StringComparison.Ordinal);
    }

    private int ChunkCount(string file) => Storage.Manifest.Files.First(f => f.Name == file).ChunkCrcs.Count;

    private static ChunkNotLoadedException? Missing(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is ChunkNotLoadedException m)
            {
                return m;
            }

            if (x is AggregateException agg)
            {
                foreach (Exception inner in agg.InnerExceptions)
                {
                    if (Missing(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Nearest-neighbour search through the field's IVF file; reads only the probed lists and the candidates' full-precision rows.</summary>
    /// <param name="vectorField">The vector property's name.</param>
    /// <param name="vector">The query vector.</param>
    /// <param name="k">How many neighbours, 1 to 10,000.</param>
    /// <param name="nprobe">How many lists to read. Default 8.</param>
    /// <param name="filter">Keeps only documents whose identifier it accepts; null keeps all. Applied while scanning.</param>
    /// <param name="cancellationToken">Cancels the fetches.</param>
    /// <returns>The documents and their IVF scores, nearest first.</returns>
    /// <exception cref="ArgumentException">The field has no IVF file in this static index.</exception>
    public async ValueTask<IReadOnlyList<(T Document, float Score)>> SearchVectorAsync(string vectorField, float[] vector, int k, int nprobe = 8, Func<string, bool>? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vectorField);
        ArgumentNullException.ThrowIfNull(vector);
        if (!_ivf.TryGetValue(vectorField, out IvfIndex? ivf))
        {
            string file = StaticIndexBuilder.IvfFileName(vectorField);
            if (!Storage.Exists(file, cancellationToken))
            {
                throw new ArgumentException($"The static index has no vectors for '{vectorField}'.", nameof(vectorField));
            }

            ivf = await IvfIndex.OpenAsync(await Storage.OpenReadAsync(file, cancellationToken).ConfigureAwait(false), verifyChecksum: false, cancellationToken).ConfigureAwait(false);
            _ivf[vectorField] = ivf;
        }

        string idsFile = StaticIndexBuilder.IdsFileName(vectorField);
        IRangeReader idsReader = await Storage.OpenReadAsync(idsFile, cancellationToken).ConfigureAwait(false);
        IvfHit[] hits;
        if (filter is null)
        {
            hits = await ivf.SearchAsync(vector, k, nprobe, null, 4, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The filter is asked about identifiers while the lists are scanned, which cannot wait for the network: fetch the whole table first.
            await Storage.PrefetchAsync(idsFile, 0, (int)idsReader.Length, cancellationToken).ConfigureAwait(false);
            hits = await ivf.SearchAsync(vector, k, nprobe, id => filter(ReadIdSync(idsReader, id)), 4, cancellationToken).ConfigureAwait(false);
        }

        var ids = new List<string>(hits.Length);
        foreach (IvfHit h in hits)
        {
            ids.Add(await ReadIdAsync(idsReader, h.Id, cancellationToken).ConfigureAwait(false));
        }

        SearchMapping<T> mapping = SearchMappings.Require<T>();
        List<T?> docs = await QueryAsync(index =>
        {
            var found = new List<T?>(ids.Count);
            foreach (string id in ids)
            {
                SearchResults<T> r = index.SearchBy(new TermQuery(mapping.Schema.IdField!, id)).Take(1).Execute();
                found.Add(r.Count == 0 ? null : r[0].Document);
            }

            return found;
        }, cancellationToken).ConfigureAwait(false);
        var result = new List<(T, float)>(hits.Length);
        for (int i = 0; i < hits.Length; i++)
        {
            if (docs[i] is { } d)
            {
                result.Add((d, hits[i].Score));
            }
        }

        return result;
    }

    private static async ValueTask<string> ReadIdAsync(IRangeReader table, int id, CancellationToken ct)
    {
        var head = new byte[8];
        await table.ReadExactlyAsync(4 + (4L * id), head, ct).ConfigureAwait(false);
        uint start = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head);
        uint end = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        long count = await ReadCountAsync(table, ct).ConfigureAwait(false);
        if (id < 0 || id >= count || end < start || end > table.Length)
        {
            throw new CorruptIndexException(table.Name, "the identifier table is damaged");
        }

        var text = new byte[end - start];
        await table.ReadExactlyAsync(4 + (4 * (count + 1)) + start, text, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(text);
    }

    private static async ValueTask<long> ReadCountAsync(IRangeReader table, CancellationToken ct)
    {
        var b = new byte[4];
        await table.ReadExactlyAsync(0, b, ct).ConfigureAwait(false);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static string ReadIdSync(IRangeReader table, int id)
    {
        var b = new byte[4];
        table.ReadExactly(0, b);
        long count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b);
        var head = new byte[8];
        table.ReadExactly(4 + (4L * id), head);
        uint start = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head);
        uint end = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        if (id < 0 || id >= count || end < start || end > table.Length)
        {
            throw new CorruptIndexException(table.Name, "the identifier table is damaged");
        }

        var text = new byte[end - start];
        table.ReadExactly(4 + (4 * (count + 1)) + start, text);
        return Encoding.UTF8.GetString(text);
    }
}

/// <summary>
/// The vector list of a hybrid search over a static index: the field's IVF lists, read through the chunk cache (a chunk that is not there yet
/// raises <see cref="ChunkNotLoadedException"/>, which the query loop answers by fetching and running again). Ranks by the 8-bit codes and
/// never reads the full-precision rows, so a list of 100 costs the probed lists and nothing more.
/// </summary>
internal sealed class StaticVectorArm(ChunkedStaticStorage storage, Dictionary<string, IvfIndex> ivf, int nprobe) : IExternalVectorArm
{
    public bool Handles(string field) => storage.Exists(StaticIndexBuilder.IvfFileName(field));

    public IReadOnlyList<(int Doc, float Score)> Nearest(string field, float[] vector, int count)
    {
        IvfIndex index;
        lock (ivf)
        {
            if (!ivf.TryGetValue(field, out IvfIndex? open))
            {
                open = IvfIndex.Open(storage.OpenRead(StaticIndexBuilder.IvfFileName(field)), verifyChecksum: false);
                ivf[field] = open;
            }

            index = open;
        }

        IvfHit[] hits = index.SearchApproximate(vector, Math.Min(Math.Max(1, count), Math.Min(index.Count, 10_000)), nprobe);
        string mapFile = StaticIndexBuilder.DocsFileName(field);
        IRangeReader? map = storage.Exists(mapFile) ? storage.OpenRead(mapFile) : null;
        var result = new List<(int, float)>(hits.Length);
        Span<byte> entry = stackalloc byte[4]; // one u32 of the vector-number to document-id table
        using (map)
        {
            foreach (IvfHit h in hits)
            {
                int doc = h.Id;
                if (map is not null)
                {
                    if (4L * (h.Id + 1) > map.Length)
                    {
                        throw new CorruptIndexException(map.Name, "the document table is shorter than the IVF file's vector count");
                    }

                    map.ReadExactly(4L * h.Id, entry);
                    uint raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    if (raw > int.MaxValue)
                    {
                        throw new CorruptIndexException(map.Name, "a document id is out of range");
                    }

                    doc = (int)raw;
                }

                result.Add((doc, h.Score));
            }
        }

        return result;
    }
}
