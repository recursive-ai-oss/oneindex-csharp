using System.Collections;
using System.Linq.Expressions;
using OneIndex.Aggregations;
using OneIndex.Highlighting;
using OneIndex.Index;
using OneIndex.Linq;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex;

/// <summary>
/// Entry points of the happy path: open an index of attributed documents in a directory or in memory. Settings come from the attributes
/// on the document type; there is no options object.
/// </summary>
public static class SearchIndex
{
    /// <summary>Opens an index in a directory, creating it when it does not exist.</summary>
    /// <typeparam name="T">The attributed document type; its mapping is generated at build time.</typeparam>
    /// <param name="path">The directory. It is created if missing. Only one process may write to it at a time.</param>
    /// <returns>The index, which holds the directory's single writer until disposed.</returns>
    /// <exception cref="InvalidOperationException">The type has no generated mapping or no <see cref="SearchIdAttribute"/> member.</exception>
    /// <exception cref="OneIndex.Index.SchemaEvolutionException">The directory holds an index whose schema cannot be changed into the type's; the message names the field.</exception>
    /// <exception cref="CorruptIndexException">The newest commit or a file it lists is damaged.</exception>
    public static SearchIndex<T> Open<T>(string path)
        where T : class
        => Open<T>(path, SearchEmbedders.Default, SearchSparseEncoders.Default, SearchRerankers.Default);

    /// <summary>Opens an index in a directory with an explicit embedder for <c>[Vector(From = ...)]</c> members (see <see cref="SearchEmbedders"/> and docs/auto-embedding.md).</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="path">The directory, created if missing.</param>
    /// <param name="embedder">The embedder, or <see langword="null"/> for none: vectors are then supplied by the application.</param>
    /// <returns>The index.</returns>
    /// <exception cref="InvalidOperationException">The embedder's dimension differs from a member's <c>Dimensions</c>, or the type has no generated mapping.</exception>
    public static SearchIndex<T> Open<T>(string path, IEmbedder? embedder)
        where T : class
        => Open<T>(path, embedder, SearchSparseEncoders.Default, SearchRerankers.Default);

    /// <summary>Opens an index in a directory with explicit embedder and sparse encoder, for <c>[Vector(From = ...)]</c> and <c>[Sparse(From = ...)]</c> members.</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="path">The directory, created if missing.</param>
    /// <param name="embedder">The embedder, or <see langword="null"/> for none.</param>
    /// <param name="sparseEncoder">The sparse encoder, or <see langword="null"/> for none: sparse vectors are then supplied by the application.</param>
    /// <returns>The index.</returns>
    public static SearchIndex<T> Open<T>(string path, IEmbedder? embedder, ISparseEncoder? sparseEncoder)
        where T : class
        => Open<T>(path, embedder, sparseEncoder, SearchRerankers.Default);

    /// <summary>
    /// Opens an index in a directory with every model given: embedder, sparse encoder and reranker (see <see cref="SearchRerankers"/> and docs/rerank.md). The shorter
    /// overloads fill what they leave out from the static defaults (<see cref="SearchEmbedders"/>, <see cref="SearchSparseEncoders"/>, <see cref="SearchRerankers"/>);
    /// this one uses exactly what it is given, <see langword="null"/> meaning none.
    /// </summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="path">The directory, created if missing.</param>
    /// <param name="embedder">The embedder, or <see langword="null"/> for none.</param>
    /// <param name="sparseEncoder">The sparse encoder, or <see langword="null"/> for none.</param>
    /// <param name="reranker">The reranker for <c>Rerank()</c> and for types with <c>[FullText(Rerank = true)]</c> members, or <see langword="null"/> for none.</param>
    /// <returns>The index.</returns>
    public static SearchIndex<T> Open<T>(string path, IEmbedder? embedder, ISparseEncoder? sparseEncoder, IReranker? reranker)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new SearchIndex<T>(new FileSystemStorage(path), SearchMappings.Require<T>(), embedder, sparseEncoder, reranker);
    }

    /// <summary>Opens or creates an index in a directory with a mapping built at run time instead of the generated one, for adapters whose document shape is not known at compile time.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The index directory.</param>
    /// <param name="mapping">The mapping, which must be deterministic: the schema it builds is recorded in the index.</param>
    /// <returns>The index. It has no embedder, sparse encoder or reranker.</returns>
    public static SearchIndex<T> OpenMapped<T>(string path, SearchMapping<T> mapping)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(mapping);
        return new SearchIndex<T>(new FileSystemStorage(path), mapping);
    }

    /// <summary>Creates an in-memory index with a mapping built at run time (see <see cref="OpenMapped{T}(string, SearchMapping{T})"/>).</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="mapping">The mapping.</param>
    /// <returns>The index.</returns>
    public static SearchIndex<T> InMemoryMapped<T>(SearchMapping<T> mapping)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new SearchIndex<T>(new MemoryStorage(), mapping);
    }

    /// <summary>Opens or creates an index in a directory whose automatically embedded vector members take the embedder's dimension instead of the length their attribute declares.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The index directory.</param>
    /// <param name="embedder">The embedder; its <see cref="IEmbedder.Dimension"/> becomes the length of every <c>[Vector(From = ...)]</c> member.</param>
    /// <returns>The index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="embedder"/> is null.</exception>
    public static SearchIndex<T> OpenSizedToEmbedder<T>(string path, IEmbedder embedder)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(embedder);
        return new SearchIndex<T>(new FileSystemStorage(path), SearchMappings.Require<T>().WithEmbeddedVectorDimensions(embedder.Dimension), embedder, SearchSparseEncoders.Default, SearchRerankers.Default);
    }

    /// <summary>Creates an in-memory index whose automatically embedded vector members take the embedder's dimension.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="embedder">The embedder.</param>
    /// <returns>The index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="embedder"/> is null.</exception>
    public static SearchIndex<T> InMemorySizedToEmbedder<T>(IEmbedder embedder)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(embedder);
        return new SearchIndex<T>(new MemoryStorage(), SearchMappings.Require<T>().WithEmbeddedVectorDimensions(embedder.Dimension), embedder, SearchSparseEncoders.Default, SearchRerankers.Default);
    }

    /// <summary>
    /// Opens the newest commit of an index directory for searching only. It takes no lock, so it works while another process writes,
    /// and the searches it runs return what the writer's own would for that commit. <c>Upsert</c>, <c>Delete</c> and <c>Commit</c> throw <see cref="NotSupportedException"/>.
    /// </summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="path">The index directory.</param>
    /// <param name="embedder">Embeds query text for hybrid search, or <see langword="null"/> for none.</param>
    /// <returns>The read-only index.</returns>
    /// <exception cref="CorruptIndexException">The newest commit is damaged.</exception>
    /// <exception cref="FileNotFoundException">The directory holds no commit.</exception>
    public static SearchIndex<T> OpenReadOnly<T>(string path, IEmbedder? embedder = null)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return OpenReadOnly<T>(new FileSystemStorage(path, readOnly: true), embedder);
    }

    /// <summary>Opens the newest commit in a storage for searching only (see <see cref="OpenReadOnly{T}(string, IEmbedder)"/>).</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="storage">The storage; it may be read-only.</param>
    /// <param name="embedder">Embeds query text for hybrid search, or <see langword="null"/> for none.</param>
    /// <returns>The read-only index.</returns>
    public static SearchIndex<T> OpenReadOnly<T>(IStorage storage, IEmbedder? embedder = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(storage);
        IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        return new SearchIndex<T>(storage, SearchMappings.Require<T>(), snapshot, embedder, SearchRerankers.Default);
    }

    /// <summary>Async twin of <see cref="OpenReadOnly{T}(IStorage, IEmbedder)"/>, for storages that really wait (HTTP, browser storage).</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="storage">The storage.</param>
    /// <param name="embedder">Embeds query text, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The read-only index.</returns>
    public static async ValueTask<SearchIndex<T>> OpenReadOnlyAsync<T>(IStorage storage, IEmbedder? embedder = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(storage);
        IndexSnapshot snapshot = await IndexSnapshot.OpenAsync(storage, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new SearchIndex<T>(storage, SearchMappings.Require<T>(), snapshot, embedder, SearchRerankers.Default);
    }

    /// <summary>Creates an in-memory index with explicit embedder and sparse encoder (see <see cref="Open{T}(string, IEmbedder, ISparseEncoder)"/>).</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="embedder">The embedder, or <see langword="null"/> for none.</param>
    /// <param name="sparseEncoder">The sparse encoder, or <see langword="null"/> for none.</param>
    /// <returns>The index.</returns>
    public static SearchIndex<T> InMemory<T>(IEmbedder? embedder, ISparseEncoder? sparseEncoder)
        where T : class
        => new(new MemoryStorage(), SearchMappings.Require<T>(), embedder, sparseEncoder, SearchRerankers.Default);

    /// <summary>Creates an in-memory index with every model given; <see langword="null"/> means none (see <see cref="Open{T}(string, IEmbedder, ISparseEncoder, IReranker)"/>).</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="embedder">The embedder, or <see langword="null"/> for none.</param>
    /// <param name="sparseEncoder">The sparse encoder, or <see langword="null"/> for none.</param>
    /// <param name="reranker">The reranker, or <see langword="null"/> for none.</param>
    /// <returns>The index.</returns>
    public static SearchIndex<T> InMemory<T>(IEmbedder? embedder, ISparseEncoder? sparseEncoder, IReranker? reranker)
        where T : class
        => new(new MemoryStorage(), SearchMappings.Require<T>(), embedder, sparseEncoder, reranker);

    /// <summary>Creates an index that lives in memory only and disappears when disposed.</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <returns>The index.</returns>
    /// <exception cref="InvalidOperationException">The type has no generated mapping or no <see cref="SearchIdAttribute"/> member.</exception>
    public static SearchIndex<T> InMemory<T>()
        where T : class
        => new(new MemoryStorage(), SearchMappings.Require<T>(), SearchEmbedders.Default, SearchSparseEncoders.Default, SearchRerankers.Default);

    /// <summary>Creates an in-memory index with an explicit embedder (see <see cref="Open{T}(string, IEmbedder)"/>).</summary>
    /// <typeparam name="T">The attributed document type.</typeparam>
    /// <param name="embedder">The embedder, or <see langword="null"/> for none.</param>
    /// <returns>The index.</returns>
    public static SearchIndex<T> InMemory<T>(IEmbedder? embedder)
        where T : class
        => new(new MemoryStorage(), SearchMappings.Require<T>(), embedder, SearchSparseEncoders.Default, SearchRerankers.Default);
}

/// <summary>
/// An open index of <typeparamref name="T"/> documents. Writes are buffered until <see cref="Commit(CancellationToken)"/>, which makes them durable
/// and visible; searches see the last commit. The index is safe to use from several threads.
/// </summary>
/// <typeparam name="T">The attributed document type.</typeparam>
public sealed class SearchIndex<T> : IDisposable, IAsyncDisposable
    where T : class
{
    private readonly IStorage _storage;
    private readonly IndexWriter? _writer;

    private IndexWriter Writer => _writer ?? throw new NotSupportedException("This index was opened read-only.");
    private readonly SearchMapping<T> _mapping;
    private readonly object _gate = new(); // writer calls and the changes waiting for the next commit
    private readonly object _readGate = new(); // the current generation: held only for a pointer swap, never across I/O
    private readonly bool _storesSource;
    // Until the index stores source itself, documents are kept in memory: each generation holds the documents of its commit (a copy,
    // so that a search in flight never sees a later version), and these are the changes waiting for the next commit.
    private Dictionary<string, Document> _sourceCommitted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Document> _sourcePending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletesPending = new(StringComparer.Ordinal);
    private Generation? _current;
    private volatile bool _disposed;
    private readonly AutoEmbedding? _auto;
    private readonly AutoSparse? _autoSparse;

    internal SearchIndex(IStorage storage, SearchMapping<T> mapping, IEmbedder? embedder = null, ISparseEncoder? sparseEncoder = null, IReranker? reranker = null)
    {
        Reranker = reranker;
        _storage = storage;
        _mapping = mapping;
        if (embedder is not null && mapping.Members.Any(static m => m.From is not null && m.Type == FieldType.Vector))
        {
            _auto = new AutoEmbedding(embedder, mapping.Members);
        }

        if (sparseEncoder is not null && mapping.Members.Any(static m => m.From is not null && m.Type == FieldType.Sparse))
        {
            _autoSparse = new AutoSparse(sparseEncoder, mapping.Members);
        }

        var options = new IndexWriterOptions();
        _storesSource = options.Codecs.Any(static c => c.Parts.Contains(SegmentParts.Stored));
        _writer = IndexWriter.Open(storage, mapping.Schema, options);
        try
        {
            if (_writer.Generation > 0)
            {
                _current = new Generation(_writer.OpenSnapshot(), _sourceCommitted);
            }
        }
        catch
        {
            _writer.Dispose();
            throw;
        }
    }

    // A read-only view of a committed index: no writer, no lock, so it also opens indexes on read-only storage.
    internal SearchIndex(IStorage storage, SearchMapping<T> mapping, IndexSnapshot snapshot, IEmbedder? embedder, IReranker? reranker)
    {
        Reranker = reranker;
        _storage = storage;
        _mapping = mapping;
        _storesSource = true;
        if (embedder is not null && mapping.Members.Any(static m => m.From is not null && m.Type == FieldType.Vector))
        {
            _auto = new AutoEmbedding(embedder, mapping.Members);
        }

        _current = new Generation(snapshot, _sourceCommitted);
    }

    /// <summary>The number of documents in the last commit.</summary>
    public long Count
    {
        get
        {
            lock (_readGate)
            {
                ThrowIfDisposed();
                return _current?.Snapshot.LiveDocCount ?? 0;
            }
        }
    }

    internal SearchMapping<T> Mapping => _mapping;

    internal AutoEmbedding? Auto => _auto;

    /// <summary>The reranker of searches that rerank (<c>Rerank()</c>), or <see langword="null"/> when the index has none.</summary>
    public IReranker? Reranker { get; }

    internal AutoSparse? AutoSparse => _autoSparse;

    /// <summary>The sparse encoder that fills <c>[Sparse(From = ...)]</c> members and encodes search text, or <see langword="null"/> when the index has none.</summary>
    public ISparseEncoder? SparseEncoder => _autoSparse?.Encoder;

    /// <summary>The schema of the index, which the strict query syntax, filter strings and query JSON are checked against.</summary>
    public Schema Schema => _mapping.Schema;

    /// <summary>The embedder that fills <c>[Vector(From = ...)]</c> members and embeds search text, or <see langword="null"/> when the index has none.</summary>
    public IEmbedder? Embedder => _auto?.Embedder;

    /// <summary>
    /// Adds a document, or replaces the one with the same identifier. It is searchable after the next commit. A <c>[Vector(From = ...)]</c> member
    /// that is left null is embedded from its source text first (reusing the stored vector when the text and model are unchanged); if the embedder fails
    /// the call throws and the index is unchanged.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="cancellationToken">Cancels embedding, or a flush this call triggers.</param>
    /// <exception cref="ArgumentException">The document has no identifier or does not fit the schema.</exception>
    /// <exception cref="EmbeddingException">The embedder failed or returned a non-finite value.</exception>
    public void Upsert(T document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document doc = _mapping.ToDocument(document);
        _auto?.Fill([doc], _mapping.Schema.IdField!, CommittedSource, replace: false, cancellationToken);
        _autoSparse?.Fill([doc], _mapping.Schema.IdField!, CommittedSource, replace: false, cancellationToken);
        Write(document, doc, cancellationToken);
    }

    private void Write(T? document, Document doc, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Writer.Upsert(doc, cancellationToken);
            if (!_storesSource)
            {
                string id = (document is null ? doc.Get(_mapping.Schema.IdField!).AsText() : _mapping.GetId(document)) ?? string.Empty;
                _sourcePending[id] = doc;
                _deletesPending.Remove(id);
            }
        }
    }

    /// <summary>Adds or replaces several documents. Automatic embedding runs in batches, so this is much faster than one call per document.</summary>
    /// <param name="documents">The documents.</param>
    /// <param name="cancellationToken">Cancels between batches.</param>
    public void Upsert(IEnumerable<T> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var batch = new List<T>(UpsertBatch);
        foreach (T d in documents)
        {
            ArgumentNullException.ThrowIfNull(d);
            batch.Add(d);
            if (batch.Count == UpsertBatch)
            {
                UpsertBatchOf(batch, cancellationToken);
                batch.Clear();
            }
        }

        UpsertBatchOf(batch, cancellationToken);
    }

    private const int UpsertBatch = 64;

    private void UpsertBatchOf(List<T> batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.Count == 0)
        {
            return;
        }

        var docs = new List<Document>(batch.Count);
        foreach (T d in batch)
        {
            docs.Add(_mapping.ToDocument(d));
        }

        _auto?.Fill(docs, _mapping.Schema.IdField!, CommittedSource, replace: false, cancellationToken);
        _autoSparse?.Fill(docs, _mapping.Schema.IdField!, CommittedSource, replace: false, cancellationToken);
        for (int i = 0; i < docs.Count; i++)
        {
            Write(batch[i], docs[i], cancellationToken);
        }
    }

    /// <summary>Adds or replaces a document, embedding with the embedder's asynchronous method (see <see cref="Upsert(T, CancellationToken)"/>).</summary>
    /// <param name="document">The document.</param>
    /// <param name="cancellationToken">Cancels embedding or a flush.</param>
    /// <returns>A task that completes when the document is buffered.</returns>
    public async ValueTask UpsertAsync(T document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document doc = _mapping.ToDocument(document);
        if (_auto is not null)
        {
            await _auto.FillAsync([doc], _mapping.Schema.IdField!, CommittedSource, replace: false, cancellationToken).ConfigureAwait(false);
        }

        if (_autoSparse is not null)
        {
            await _autoSparse.FillAsync([doc], _mapping.Schema.IdField!, CommittedSource, replace: false, cancellationToken).ConfigureAwait(false);
        }

        Write(document, doc, cancellationToken);
    }

    /// <summary>
    /// Embeds again every committed document whose stored vector was made by a different model or from different text, or is missing, then
    /// buffers the changes for the next commit. Call it after changing the embedder (its <see cref="IEmbedder.ModelId"/>); unchanged documents
    /// cost nothing. Documents upserted since the last commit are not touched.
    /// </summary>
    /// <param name="cancellationToken">Cancels between batches; what was already buffered stays.</param>
    /// <returns>How many documents were embedded again.</returns>
    /// <exception cref="InvalidOperationException">The index has no embedder.</exception>
    public int ReembedStale(CancellationToken cancellationToken = default)
    {
        AutoEmbedding? auto = _auto;
        AutoSparse? sparse = _autoSparse;
        if (auto is null && sparse is null)
        {
            throw new InvalidOperationException("The index has no embedder or sparse encoder, so there is nothing to embed with.");
        }

        Generation? generation = Acquire();
        if (generation is null)
        {
            return 0;
        }

        int total = 0;
        try
        {
            var batch = new List<Document>(UpsertBatch);
            foreach (LeafContext leaf in generation.Context.Leaves)
            {
                for (int local = 0; local < leaf.Reader.MaxDoc; local++)
                {
                    if (leaf.Reader.LiveDocs is { } live && !live.IsLive(local))
                    {
                        continue;
                    }

                    Document? stored = Source(generation, leaf, local, out _);
                    if (stored is not null && ((auto?.IsStale(stored) ?? false) || (sparse?.IsStale(stored) ?? false)))
                    {
                        batch.Add(stored);
                        if (batch.Count == UpsertBatch)
                        {
                            total += Reembed(auto, sparse, batch, cancellationToken);
                            batch.Clear();
                        }
                    }
                }
            }

            total += Reembed(auto, sparse, batch, cancellationToken);
        }
        finally
        {
            generation.Release();
        }

        return total;
    }

    /// <summary>Every live document of the last commit, in segment order (the order is stable for a commit but is not insertion order), for export and for building derived files.</summary>
    /// <returns>The documents, read lazily from stored source; enumerating pins the commit, so dispose the enumerator when you stop early.</returns>
    /// <exception cref="InvalidOperationException">The index does not store source (stored source is on by default).</exception>
    public IEnumerable<T> Documents()
    {
        Generation? generation = Acquire();
        if (generation is null)
        {
            yield break;
        }

        try
        {
            foreach (LeafContext leaf in generation.Context.Leaves)
            {
                for (int local = 0; local < leaf.Reader.MaxDoc; local++)
                {
                    if (leaf.Reader.LiveDocs is { } live && !live.IsLive(local))
                    {
                        continue;
                    }

                    Document stored = Source(generation, leaf, local, out _) ?? throw new InvalidOperationException("The index holds no stored source for its documents.");
                    yield return _mapping.FromDocument(stored);
                }
            }
        }
        finally
        {
            generation.Release();
        }
    }

    private int Reembed(AutoEmbedding? auto, AutoSparse? sparse, List<Document> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        // Copies, so a document being read by a search in flight is never changed.
        var copies = new List<Document>(batch.Count);
        foreach (Document d in batch)
        {
            var copy = new Document();
            foreach (DocumentField f in d.Fields)
            {
                copy.Add(f.Name, f.Value);
            }

            copies.Add(copy);
        }

        auto?.Fill(copies, _mapping.Schema.IdField!, static _ => null, replace: true, cancellationToken);
        sparse?.Fill(copies, _mapping.Schema.IdField!, static _ => null, replace: true, cancellationToken);
        foreach (Document c in copies)
        {
            Write(null, c, cancellationToken);
        }

        return copies.Count;
    }

    /// <summary>The asynchronous twin of <see cref="ReembedStale"/>. The work runs on the calling thread.</summary>
    /// <param name="cancellationToken">Cancels between batches.</param>
    /// <returns>How many documents were embedded again.</returns>
    public ValueTask<int> ReembedStaleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ReembedStale(cancellationToken));

    // The document stored at the last commit under an identifier: the durable embedding cache (docs/auto-embedding.md).
    private Document? CommittedSource(string id)
    {
        Generation? generation = Acquire();
        if (generation is null)
        {
            return null;
        }

        try
        {
            TopDocs top = generation.Searcher.Search(new TermQuery(_mapping.Schema.IdField!, id), 1);
            if (top.Hits.Count == 0)
            {
                return null;
            }

            int global = top.Hits[0].DocId;
            foreach (LeafContext leaf in generation.Context.Leaves)
            {
                if (global >= leaf.DocBase && global - leaf.DocBase < leaf.Reader.MaxDoc)
                {
                    return Source(generation, leaf, global - leaf.DocBase, out _);
                }
            }

            return null;
        }
        finally
        {
            generation.Release();
        }
    }

    /// <summary>Deletes the document with an identifier. The deletion is visible after the next commit.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns><see langword="true"/> if a document was deleted.</returns>
    public bool Delete(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_storesSource)
            {
                _sourcePending.Remove(id);
                _deletesPending.Add(id);
            }

            return Writer.DeleteById(id);
        }
    }

    /// <summary>Deletes the document with an identifier (see <see cref="Delete(string)"/>).</summary>
    /// <param name="id">The identifier.</param>
    /// <returns><see langword="true"/> if a document was deleted.</returns>
    public ValueTask<bool> DeleteAsync(string id) => ValueTask.FromResult(Delete(id));

    /// <summary>Makes every change since the last commit durable and visible to searches. The commit is atomic: after a crash the index holds the last complete commit.</summary>
    /// <param name="cancellationToken">Cancels before the commit file is published.</param>
    /// <returns>The commit's generation number.</returns>
    public long Commit(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            long generation = Writer.Commit(cancellationToken);
            if (!_storesSource)
            {
                var next = new Dictionary<string, Document>(_sourceCommitted, StringComparer.Ordinal);
                foreach (string id in _deletesPending)
                {
                    next.Remove(id);
                }

                foreach (KeyValuePair<string, Document> kv in _sourcePending)
                {
                    next[kv.Key] = kv.Value;
                }

                _sourceCommitted = next;
            }

            _deletesPending.Clear();
            _sourcePending.Clear();
            var fresh = new Generation(Writer.OpenSnapshot(), _sourceCommitted);
            Generation? old;
            lock (_readGate)
            {
                old = _current;
                _current = fresh;
            }

            old?.Release();
            return generation;
        }
    }

    /// <summary>
    /// Where the rebuild after a change to the document type stands. When the type's schema differs from the one in the directory the new
    /// index is rebuilt from stored source in the background while the old schema keeps serving every segment; each commit advances the
    /// rebuild by a few segments, and <see cref="CompleteSchemaEvolution(CancellationToken)"/> finishes it. The new schema becomes visible
    /// on all segments in one commit (docs/schema-evolution.md).
    /// </summary>
    public SchemaEvolutionStatus SchemaEvolution => Writer.SchemaEvolution;

    /// <summary>Rebuilds every segment that still awaits the schema change, committing as it goes (see <see cref="SchemaEvolution"/>). Returns at once when nothing is waiting.</summary>
    /// <param name="cancellationToken">Cancels between segments; what was committed stays.</param>
    /// <exception cref="IOException">A rebuild failed; the index is unchanged by it.</exception>
    public void CompleteSchemaEvolution(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!Writer.SchemaEvolution.InProgress)
            {
                return;
            }

            Writer.CompleteSchemaEvolution(cancellationToken);
        }

        Commit(cancellationToken);
    }

    /// <summary>Rebuilds segments awaiting a schema change (see <see cref="CompleteSchemaEvolution(CancellationToken)"/>).</summary>
    /// <param name="cancellationToken">Cancels between segments.</param>
    /// <returns>A completed task.</returns>
    public ValueTask CompleteSchemaEvolutionAsync(CancellationToken cancellationToken = default)
    {
        CompleteSchemaEvolution(cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The application's marker stored in the last commit (empty until one is set): for example how far an external change log has been applied.
    /// It changes only with a commit, so it always describes exactly the documents that commit holds.
    /// </summary>
    public ReadOnlyMemory<byte> AppliedPosition
    {
        get
        {
            lock (_readGate)
            {
                ThrowIfDisposed();
                return _current?.Snapshot.Commit.AppliedPosition ?? ReadOnlyMemory<byte>.Empty;
            }
        }
    }

    /// <summary>
    /// Commits like <see cref="Commit(CancellationToken)"/> and stores an application marker in the same commit, so the marker and the documents
    /// become durable together or not at all (see <see cref="AppliedPosition"/>). A commit with nothing else changed still publishes the marker.
    /// </summary>
    /// <param name="appliedPosition">At most 1,024 bytes.</param>
    /// <param name="cancellationToken">Cancels before the commit file is published.</param>
    /// <returns>The commit's generation number.</returns>
    /// <exception cref="ArgumentException">The marker is too long.</exception>
    public long Commit(ReadOnlySpan<byte> appliedPosition, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Writer.SetAppliedPosition(appliedPosition);
            return Commit(cancellationToken);
        }
    }

    /// <summary>The identifiers of every live document in the last commit, in no particular order.</summary>
    /// <returns>The identifiers; empty before the first commit.</returns>
    public List<string> CommittedIds()
    {
        Generation? generation = Acquire();
        var result = new List<string>();
        if (generation is null)
        {
            return result;
        }

        try
        {
            for (int s = 0; s < generation.Snapshot.SegmentCount; s++)
            {
                SegmentReader segment = generation.Snapshot.GetSegment(s);
                if (segment.Ids is null)
                {
                    continue;
                }

                foreach (KeyValuePair<byte[], int> entry in segment.Ids.ReadAll())
                {
                    if (segment.Live.IsLive(entry.Value))
                    {
                        result.Add(System.Text.Encoding.UTF8.GetString(entry.Key));
                    }
                }
            }

            return result;
        }
        finally
        {
            generation.Release();
        }
    }

    /// <summary>Commits (see <see cref="Commit(CancellationToken)"/>).</summary>
    /// <param name="cancellationToken">Cancels before the commit file is published.</param>
    /// <returns>The commit's generation number.</returns>
    public ValueTask<long> CommitAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Commit(cancellationToken));

    /// <summary>Starts a search over the last commit.</summary>
    /// <param name="text">What the user typed: words, "quoted phrases", <c>-excluded</c> words and <c>OR</c>. Any string is accepted; <see langword="null"/> or blank matches every document. See docs/user-text.md.</param>
    /// <returns>The search, which runs when it is enumerated or <see cref="SearchQuery{T}.Execute(CancellationToken)"/> is called.</returns>
    public SearchQuery<T> Search(string? text = null) => new(this, text, null, [], 20, 0, false, null);

    /// <summary>Starts a search with a query built from query objects.</summary>
    /// <param name="query">The query; it scores the matches.</param>
    /// <returns>The search.</returns>
    public SearchQuery<T> SearchBy(Search.Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new SearchQuery<T>(this, null, query, [], 20, 0, false, null);
    }

    /// <summary>
    /// Starts a vector search: the documents whose vector property is nearest to a query vector, best first. <c>Where</c> filters restrict which documents
    /// may be neighbours before the nearest are picked, so a full page comes back whenever enough documents pass. The search keeps the
    /// <paramref name="neighbors"/> nearest overall; <c>Take</c> and <c>Skip</c> page within them. See docs/vectors.md.
    /// </summary>
    /// <typeparam name="TVector">The property type, <c>float[]</c>.</typeparam>
    /// <param name="member">The vector property, marked <see cref="VectorAttribute"/>, like <c>x =&gt; x.Embedding</c>.</param>
    /// <param name="vector">The query vector, as long as the property's <c>Dimensions</c> and finite.</param>
    /// <param name="neighbors">How many nearest documents to find, 1 to 10,000. Default 20.</param>
    /// <returns>The search. Hits score their similarity (see docs/vectors.md).</returns>
    /// <exception cref="ArgumentException">The property is not a vector property, or the vector has the wrong length or holds a NaN or infinity.</exception>
    public SearchQuery<T> SearchVector<TVector>(Expression<Func<T, TVector>> member, float[] vector, int neighbors = 20)
    {
        ArgumentNullException.ThrowIfNull(vector);
        MappedMember m = MemberNames.Resolve(_mapping, member, nameof(SearchVector));
        return SearchVectorCore(m, vector, neighbors);
    }

    /// <summary>Like <see cref="SearchVector{TVector}(Expression{Func{T, TVector}}, float[], int)"/> for a vector property named by a string, for callers that learn the property at run time.</summary>
    /// <param name="vectorMember">The property name.</param>
    /// <param name="vector">The query vector, as long as the property's <c>Dimensions</c> and finite.</param>
    /// <param name="neighbors">How many nearest documents to find, 1 to 10,000. Default 20.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The name is not a vector property, or the vector has the wrong length or holds a NaN or infinity.</exception>
    public SearchQuery<T> SearchVector(string vectorMember, float[] vector, int neighbors = 20)
    {
        ArgumentNullException.ThrowIfNull(vectorMember);
        ArgumentNullException.ThrowIfNull(vector);
        if (!_mapping.TryGetMember(vectorMember, out MappedMember? m))
        {
            throw new ArgumentException($"SearchVector: {typeof(T).Name} has no property '{vectorMember}'.", nameof(vectorMember));
        }

        return SearchVectorCore(m, vector, neighbors);
    }

    private SearchQuery<T> SearchVectorCore(MappedMember m, float[] vector, int neighbors)
    {
        if (m.Type != FieldType.Vector)
        {
            throw new ArgumentException($"SearchVector: '{m.Name}' is not a [Vector] property.", nameof(m));
        }

        if (vector.Length != m.Dimensions || vector.Any(x => !float.IsFinite(x)))
        {
            throw new ArgumentException($"SearchVector: the query vector must hold {m.Dimensions} finite numbers.", nameof(vector));
        }

        if (neighbors < 1 || neighbors > KnnQuery.MaxK)
        {
            throw new ArgumentOutOfRangeException(nameof(neighbors), $"neighbors must be from 1 to {KnnQuery.MaxK}.");
        }

        return SearchBy(new KnnQuery(m.Name, (float[])vector.Clone(), neighbors));
    }

    /// <summary>Starts a search with a query in the strict syntax (fields, AND/OR/NOT, phrases with slop, prefixes, fuzzy terms, regular expressions, ranges, boosts). See docs/query-syntax.md.</summary>
    /// <param name="query">The query text.</param>
    /// <returns>The search.</returns>
    /// <exception cref="QueryParseException">The text is not valid or names an unknown field; the exception says where.</exception>
    public SearchQuery<T> SearchStrict(string query) => SearchBy(StrictQuery.Parse(query, Schema));

    /// <summary>
    /// Starts a search from query JSON (<c>{"v":1,"q":"overdue","filter":"Paid = false","limit":10}</c>), such as an LLM tool call made against
    /// <see cref="SearchTool.For{T}"/>. The document is checked against the schema first. See docs/query-json.md.
    /// </summary>
    /// <param name="queryJson">The document.</param>
    /// <returns>The search, with its paging, exact count and highlight settings applied.</returns>
    /// <exception cref="QueryParseException">The document is not valid; the exception carries the JSON path.</exception>
    public SearchQuery<T> Parse(string queryJson)
    {
        QueryRequest request = QueryJson.Read(queryJson, Schema);
        SearchQuery<T> search;
        if (request.Hybrid is { } hybrid)
        {
            // The text of q is the text arm and, embedded, the vector arm (docs/hybrid.md).
            Search.Query textArm = new UserTextCompiler(Schema).Compile(hybrid.Text);
            Search.Query scored = request.Query is null ? textArm : new BooleanQuery([new BooleanClause(textArm, Occur.Must), new BooleanClause(request.Query, Occur.Must)]);
            // The filter goes to both lists, so it is a filter of the search and not a clause of the text query.
            search = SearchBy(scored).HybridCore(new HybridSpec(hybrid.SemanticRatio, hybrid.Fusion, hybrid.RankConstant, hybrid.Window, hybrid.Field, null, hybrid.Text));
            if (hybrid.Sparse)
            {
                search = search.Sparse(hybrid.SparseWeight);
            }

            if (request.Filter is { } requestFilter)
            {
                search = search.WhereQuery(requestFilter);
            }

            search = search.Take(request.Limit).Skip(request.Offset);
        }
        else if (request.Rerank is { } held)
        {
            // The text of q is searched by words and read by the reranker (docs/rerank.md); the filter selects.
            Search.Query textArm = new UserTextCompiler(Schema).Compile(held.Text);
            Search.Query scored = request.Query is null ? textArm : new BooleanQuery([new BooleanClause(textArm, Occur.Must), new BooleanClause(request.Query, Occur.Must)]);
            search = SearchBy(scored);
            if (request.Filter is { } heldFilter)
            {
                search = search.WhereQuery(heldFilter);
            }

            search = search.Take(request.Limit).Skip(request.Offset);
        }
        else
        {
            // Ranking rules rank by the words of the text, so a request that is only text and a filter is searched as text.
            bool wordRules = request.Text is not null && (request.RankingRules is { Count: > 0 } || (request.RankingRules is null && Schema.Settings.RankingRules is { Count: > 0 }));
            if (wordRules)
            {
                search = Search(request.Text);
                if (request.Filter is { } textFilter)
                {
                    search = search.WhereQuery(textFilter);
                }

                search = search.Take(request.Limit).Skip(request.Offset);
            }
            else
            {
                search = SearchBy(request.ToQuery()).Take(request.Limit).Skip(request.Offset);
            }
        }

        if (request.RankingRules is { } requestRules)
        {
            search = requestRules.Count == 0 ? search.RankByRelevance() : search.RankingRules([.. requestRules]);
        }

        if (request.Rerank is { } rerankRequest)
        {
            search = search.RerankSpecCore(new RerankSpec(rerankRequest.Window, null, null, rerankRequest.Text));
        }

        if (request.Sort is { } sort)
        {
            search = search.WithKeys([.. sort.Keys]);
        }

        if (request.SearchAfter is { } token)
        {
            search = search.SearchAfter(token);
        }

        if (request.CountExactly)
        {
            search = search.CountExactly();
        }

        return request.Highlight is { } h ? search.Highlight(h) : search;
    }

    /// <summary>Like <see cref="Parse"/> but returns the error instead of throwing it.</summary>
    /// <param name="queryJson">The document.</param>
    /// <param name="search">The search when the document is valid.</param>
    /// <param name="error">The error when it is not.</param>
    /// <returns><see langword="true"/> when the document is valid.</returns>
    public bool TryParse(string queryJson, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchQuery<T>? search, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out QueryParseException? error)
    {
        try
        {
            search = Parse(queryJson);
            error = null;
            return true;
        }
        catch (QueryParseException e)
        {
            search = null;
            error = e;
            return false;
        }
    }

    /// <summary>Releases the index and its writer. Uncommitted changes are discarded.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Generation? old;
            lock (_readGate)
            {
                _disposed = true;
                old = _current;
                _current = null;
            }

            old?.Release();
            _writer?.Dispose();
        }
    }

    /// <summary>Releases the index (see <see cref="Dispose()"/>).</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal Generation? Acquire()
    {
        lock (_readGate)
        {
            ThrowIfDisposed();
            _current?.AddRef();
            return _current;
        }
    }

    internal Document? Source(Generation generation, LeafContext leaf, int localDocId, out string id)
    {
        if (leaf.Reader.StoredFields is { } stored)
        {
            Document d = stored.GetDocument(localDocId);
            id = d.Get(_mapping.Schema.IdField!).AsText();
            return d;
        }

        id = generation.IdOf(leaf.Ordinal, localDocId);
        return generation.Fallback.GetValueOrDefault(id);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>A committed snapshot with the search machinery over it, shared by searches while any is running.</summary>
    internal sealed class Generation
    {
        private readonly Dictionary<int, Dictionary<int, string>> _ids = [];
        private int _refs = 1;

        public Generation(IndexSnapshot snapshot, Dictionary<string, Document> fallback)
        {
            Snapshot = snapshot;
            Fallback = fallback;
            try
            {
                Context = SnapshotSearch.CreateContext(snapshot);
                Searcher = new IndexSearcher(Context, new QueryEngine());
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
        }

        public IndexSnapshot Snapshot { get; }

        public Dictionary<string, Document> Fallback { get; }

        public SearchContext Context { get; }

        public IndexSearcher Searcher { get; }

        public void AddRef() => Interlocked.Increment(ref _refs);

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) == 0)
            {
                Snapshot.Dispose();
            }
        }

        // Without stored source an identifier comes from the segment's identifier table, read once per segment.
        public string IdOf(int segment, int localDocId)
        {
            lock (_ids)
            {
                if (!_ids.TryGetValue(segment, out Dictionary<int, string>? map))
                {
                    map = [];
                    IdsReader? ids = Snapshot.GetSegment(segment).Ids;
                    if (ids is not null)
                    {
                        foreach (KeyValuePair<byte[], int> entry in ids.ReadAll())
                        {
                            map[entry.Value] = System.Text.Encoding.UTF8.GetString(entry.Key);
                        }
                    }

                    _ids[segment] = map;
                }

                return map.TryGetValue(localDocId, out string? id) ? id : throw new InvalidOperationException($"Segment {segment} has no identifier for document {localDocId}.");
            }
        }
    }
}

/// <summary>One matching document.</summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed class SearchHit<T>
    where T : class
{
    private readonly Document _stored;
    private readonly Schema _schema;
    private readonly HighlightTerms _terms;
    private readonly HighlightOptions? _options;
    private IReadOnlyDictionary<string, IReadOnlyList<Fragment>>? _highlights;

    internal SearchHit(string id, float score, T document, Document stored, Schema schema, HighlightTerms terms, HighlightOptions? options)
    {
        Id = id;
        Score = score;
        Document = document;
        _stored = stored;
        _schema = schema;
        _terms = terms;
        _options = options;
    }

    /// <summary>The document's identifier.</summary>
    public string Id { get; }

    /// <summary>For a sorted search: the place of this hit in the order, to continue after it with <c>SearchAfter</c>. <see langword="null"/> for a relevance-ranked search.</summary>
    public SortCursor? Cursor { get; internal set; }

    /// <summary>For a search sorted by distance: the metres from the sort point to the document's nearest (or, descending, farthest) point. <see langword="null"/> otherwise or when the document has no point.</summary>
    public double? GeoDistance { get; internal set; }

    /// <summary>The breakdown of <see cref="Score"/>, when the search asked for it with <see cref="SearchQuery{T}.Explain"/>; <see langword="null"/> otherwise. The root value equals <see cref="Score"/>.</summary>
    public Explanation? Explanation { get; internal set; }

    /// <summary>The relevance score: BM25F over the full-text fields for text queries; 1 times boost for filter-only and blank searches. After a rerank stage it is the reranker's score (docs/rerank.md).</summary>
    public float Score { get; private set; }

    /// <summary>The score the hit had before the rerank stage; <see langword="null"/> when the search was not reranked.</summary>
    public float? FirstStageScore { get; private set; }

    internal Document Stored => _stored;

    internal SearchHit<T> WithRerank(float score, Explanation? explanation)
    {
        var hit = new SearchHit<T>(Id, score, Document, _stored, _schema, _terms, _options) { Cursor = Cursor, GeoDistance = GeoDistance, Explanation = explanation ?? Explanation };
        hit.FirstStageScore = Score;
        return hit;
    }

    /// <summary>The document as stored when it was added. Only attributed members are kept.</summary>
    public T Document { get; }

    /// <summary>
    /// Snippets of the full-text fields with the matched words located, by property name; fields without a match are absent. Computed on
    /// first use by analyzing the stored text again, so searches that never read it pay nothing. Sizes: see <see cref="HighlightOptions"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Fragment>> Highlights
        => _highlights ??= Highlighter.Highlight(_schema, _stored, _terms, _options);
}

/// <summary>The outcome of a search.</summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed class SearchResults<T> : IReadOnlyList<SearchHit<T>>
    where T : class
{
    private readonly IReadOnlyList<SearchHit<T>> _hits;

    internal SearchResults(IReadOnlyList<SearchHit<T>> hits, long totalHits, bool exact)
    {
        _hits = hits;
        TotalHits = totalHits;
        TotalHitsExact = exact;
    }

    /// <summary>The facet counts asked for with <see cref="SearchQuery{T}.Facets{TField}"/>, by property name; empty when none were asked for. Counted over all matches, not just the page.</summary>
    public IReadOnlyDictionary<string, FacetResult> Facets { get; internal init; } = new Dictionary<string, FacetResult>();

    /// <summary>The results of <see cref="SearchQuery{T}.Aggregate"/>, by name; empty when none were asked for. Computed over all matches, not just the page.</summary>
    public AggregationResultSet Aggregations { get; internal init; } = AggregationResultSet.Empty;

    /// <summary>The number of matching documents: a lower bound unless <see cref="TotalHitsExact"/>.</summary>
    public long TotalHits { get; }

    /// <summary>Whether <see cref="TotalHits"/> is the exact count (see <see cref="SearchQuery{T}.CountExactly"/>).</summary>
    public bool TotalHitsExact { get; }

    /// <summary>For a sorted search: the cursor after the last hit, to fetch the next page with <c>SearchAfter</c>; <see langword="null"/> when there are no hits or the search is not sorted.</summary>
    public SortCursor? NextCursor => _hits.Count > 0 ? _hits[^1].Cursor : null;

    /// <inheritdoc/>
    public int Count => _hits.Count;

    /// <inheritdoc/>
    public SearchHit<T> this[int index] => _hits[index];

    /// <inheritdoc/>
    public IEnumerator<SearchHit<T>> GetEnumerator() => _hits.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A search being composed: free text, filters, paging. Each method returns a new search, so a search can be reused and extended. It runs
/// when enumerated or executed. Results come best first; ties are broken by the order documents were indexed.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public sealed partial class SearchQuery<T> : IEnumerable<SearchHit<T>>
    where T : class
{
    /// <summary>The largest <c>Skip + Take</c>. Deeper pages are rejected rather than made slow.</summary>
    public const int MaxResultWindow = 10_000;

    private readonly SearchIndex<T> _index;
    private readonly string? _text;
    private readonly Search.Query? _query;
    private readonly Expression<Func<T, bool>>[] _filters;
    private readonly int _take;
    private readonly int _skip;
    private readonly bool _exact;
    private readonly HighlightOptions? _highlight;
    private readonly QueryStages? _stages;
    private readonly QueryExtras? _extras;

    internal SearchQuery(SearchIndex<T> index, string? text, Search.Query? query, Expression<Func<T, bool>>[] filters, int take, int skip, bool exact, HighlightOptions? highlight, QueryStages? stages = null, QueryExtras? extras = null)
    {
        _stages = stages;
        _extras = extras;
        _index = index;
        _text = text;
        _query = query;
        _filters = filters;
        _take = take;
        _skip = skip;
        _exact = exact;
        _highlight = highlight;
    }

    /// <summary>Keeps only documents for which the predicate holds. Calls combine with and. The predicate is translated to a filter and never run; see docs/linq.md for what it may contain.</summary>
    /// <param name="predicate">The predicate over attributed members.</param>
    /// <returns>The narrowed search.</returns>
    /// <exception cref="UnsupportedExpressionException">The predicate cannot be translated. This is thrown here, at the call, and the message shows the unsupported part.</exception>
    public SearchQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _ = new FilterTranslator<T>(_index.Mapping).Translate(predicate);
        return new SearchQuery<T>(_index, _text, _query, [.. _filters, predicate], _take, _skip, _exact, _highlight, _stages, _extras);
    }

    /// <summary>Limits the number of hits returned. Default 20.</summary>
    /// <param name="count">0 to <see cref="MaxResultWindow"/>, together with <see cref="Skip(int)"/>.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count or the window is out of range.</exception>
    public SearchQuery<T> Take(int count)
    {
        CheckWindow(count, _skip);
        return new SearchQuery<T>(_index, _text, _query, _filters, count, _skip, _exact, _highlight, _stages, _extras);
    }

    /// <summary>Skips the first hits, for paging. Default 0.</summary>
    /// <param name="count">0 to <see cref="MaxResultWindow"/>, together with <see cref="Take(int)"/>.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count or the window is out of range.</exception>
    public SearchQuery<T> Skip(int count)
    {
        CheckWindow(_take, count);
        return new SearchQuery<T>(_index, _text, _query, _filters, _take, count, _exact, _highlight, _stages, _extras);
    }

    /// <summary>Makes <see cref="SearchResults{T}.TotalHits"/> and <see cref="Count()"/> exact. This visits every match, so it is slower on large result sets.</summary>
    /// <returns>The search.</returns>
    public SearchQuery<T> CountExactly() => new(_index, _text, _query, _filters, _take, _skip, true, _highlight, _stages, _extras);

    /// <summary>Sets the size of the snippets in <see cref="SearchHit{T}.Highlights"/>.</summary>
    /// <param name="options">Fragment length and count.</param>
    /// <returns>The search.</returns>
    public SearchQuery<T> Highlight(HighlightOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SearchQuery<T>(_index, _text, _query, _filters, _take, _skip, _exact, options, _stages, _extras);
    }

    /// <summary>Compiles the search to query objects: the text query that scores, and the filters that only select.</summary>
    /// <returns>The query the engine runs.</returns>
    public Search.Query ToQuery() => Build(out _, out _, out _);

    /// <summary>Runs the search.</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The hits from <c>Skip</c> on, at most <c>Take</c> of them, with the total.</returns>
    /// <exception cref="ObjectDisposedException">The index is disposed.</exception>
    /// <exception cref="InvalidOperationException">The stored source of a hit is missing (an index reopened before stored source is available).</exception>
    public SearchResults<T> Execute(CancellationToken cancellationToken = default)
    {
        if (_take > 0 && EffectiveRerank() is { } rerank)
        {
            return ExecuteReranked(rerank.Spec, rerank.Reranker, rerank.Query, cancellationToken);
        }

        Search.Query query = Build(out Search.Query textQuery, out IReadOnlyList<RankingRule>? rules, out IReadOnlyList<Search.Query> groups);
        SearchIndex<T>.Generation? generation = _index.Acquire();
        if (_stages is not null || rules is not null)
        {
            try
            {
                return ExecuteStages(query, textQuery, generation, cancellationToken, rules, groups);
            }
            finally
            {
                generation?.Release();
            }
        }

        if (generation is null)
        {
            return new SearchResults<T>([], 0, true);
        }

        try
        {
            Schema schema = _index.Mapping.Schema;
            if (_take == 0)
            {
                var counter = new CountCollector();
                generation.Searcher.Search(query, counter, cancellationToken);
                return new SearchResults<T>([], counter.Count, true);
            }

            TopDocs top = generation.Searcher.Search(query, _skip + _take, _exact, cancellationToken);
            HighlightTerms terms = HighlightTerms.From(textQuery);
            var hits = new List<SearchHit<T>>(Math.Max(0, Math.Min(_take, top.Hits.Count - _skip)));
            for (int i = _skip; i < top.Hits.Count; i++)
            {
                ScoreDoc sd = top.Hits[i];
                LeafContext leaf = LeafOf(generation.Context, sd.DocId);
                Document stored = _index.Source(generation, leaf, sd.DocId - leaf.DocBase, out string id)
                    ?? throw new InvalidOperationException($"The stored source of document '{id}' is not available.");
                hits.Add(new SearchHit<T>(id, sd.Score, _index.Mapping.FromDocument(stored), stored, schema, terms, _highlight) { Explanation = ExplainHit(generation, query, sd.DocId) });
            }

            return new SearchResults<T>(hits, top.TotalHits, top.TotalHitsExact);
        }
        finally
        {
            generation.Release();
        }
    }

    /// <summary>Runs the search (see <see cref="Execute(CancellationToken)"/>). The work happens on the calling thread: local storage reads do not yield.</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The results.</returns>
    public ValueTask<SearchResults<T>> ExecuteAsync(CancellationToken cancellationToken = default)
        => _take > 0 && EffectiveRerank() is { } rerank
            ? ExecuteRerankedAsync(rerank.Spec, rerank.Reranker, rerank.Query, cancellationToken)
            : ValueTask.FromResult(Execute(cancellationToken));

    /// <summary>Runs the search and returns the hits as a list.</summary>
    /// <returns>The hits.</returns>
    public List<SearchHit<T>> ToList() => [.. Execute()];

    /// <summary>The number of matches: <see cref="SearchResults{T}.TotalHits"/> of running this search, a lower bound unless <see cref="CountExactly"/> was called.</summary>
    /// <returns>The count.</returns>
    public long Count() => Execute().TotalHits;

    /// <inheritdoc/>
    public IEnumerator<SearchHit<T>> GetEnumerator() => Execute().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static void CheckWindow(int take, int skip)
    {
        if (take < 0 || skip < 0 || (long)take + skip > MaxResultWindow)
        {
            throw new ArgumentOutOfRangeException(take < 0 ? nameof(take) : nameof(skip), $"Take and Skip must not be negative and together at most {MaxResultWindow}; got Take {take}, Skip {skip}.");
        }
    }

    private static LeafContext LeafOf(SearchContext context, int globalDocId)
    {
        int lo = 0;
        int hi = context.Leaves.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (context.Leaves[mid].DocBase <= globalDocId)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return context.Leaves[lo];
    }

    private Search.Query Build(out Search.Query textQuery, out IReadOnlyList<RankingRule>? rules, out IReadOnlyList<Search.Query> groups)
    {
        HybridSpec? fused = EffectiveHybrid();
        rules = fused is null ? ActiveRules() : null; // a fusion ranks by fused position: ranking rules do not apply
        groups = [];
        if (_query is not null)
        {
            textQuery = _query;
        }
        else
        {
            var compiler = new UserTextCompiler(_index.Mapping.Schema);
            if (rules is not null)
            {
                // Ranking rules replace the proximity bonus (the proximity rule) and, with the words rule, relax "all words" to "any word".
                textQuery = rules.Any(static r => r.Kind == RankingRuleKind.Words) ? compiler.CompileRanked(_text, out groups) : compiler.CompileConjunctive(_text, out groups);
            }
            else
            {
                textQuery = compiler.Compile(_text);
            }
        }

        if (fused is { } hybrid)
        {
            return BuildHybrid(textQuery, hybrid);
        }

        return _extras is null && _filters.Length == 0 ? textQuery : WithExtras(textQuery);
    }
}
