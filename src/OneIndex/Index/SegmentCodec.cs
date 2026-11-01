using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// A codec owns one or more parts of a segment (for example the term dictionary, postings and lengths together). The index
/// writer hands it the buffered documents when a segment is flushed and the source segments when segments are merged, and it
/// opens the segment's files again for reading. The writer, not the codec, names files, records them in the commit and decides
/// when they are deleted; a codec only writes through the <see cref="SegmentFileSink"/> it is given.
/// </summary>
/// <remarks>
/// Codecs are registered explicitly in <see cref="IndexWriterOptions.Codecs"/>; nothing is discovered by reflection. Every part
/// a codec writes must be listed in <see cref="CodecRegistry"/>, whose number is the format version the file is written with.
/// Methods may be called from a background merge thread, so a codec instance holds no per-segment state: everything it needs is
/// in the context it is given.
/// </remarks>
public interface ISegmentCodec
{
    /// <summary>A short name for diagnostics, for example <c>"text"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The parts this codec writes and reads, each registered in <see cref="CodecRegistry"/>. No two codecs may share a part, and
    /// the core parts (<see cref="SegmentParts.Ids"/> and <see cref="SegmentParts.Deletes"/>) belong to the index itself.
    /// </summary>
    IReadOnlyList<string> Parts { get; }

    /// <summary>
    /// Writes this codec's files for a new segment holding the buffered documents. A codec with nothing to write for this schema
    /// writes nothing.
    /// </summary>
    /// <param name="context">The documents, the schema and the segment name.</param>
    /// <param name="sink">Where to create files. Every file must be completed before this method returns.</param>
    /// <param name="cancellationToken">Cancels the flush; the writer deletes the files created so far.</param>
    void Flush(SegmentFlushContext context, SegmentFileSink sink, CancellationToken cancellationToken);

    /// <summary>
    /// Writes this codec's files for one segment built from several. Live documents of the sources are numbered in order: the
    /// first live document of the first source becomes 0, and so on, which is exactly what each source's
    /// <see cref="MergeSource.DocMap"/> says.
    /// </summary>
    /// <param name="context">The sources with their readers and document maps, and the segment name.</param>
    /// <param name="sink">Where to create files. Every file must be completed before this method returns.</param>
    /// <param name="cancellationToken">Cancels the merge; the writer deletes the files created so far.</param>
    void Merge(SegmentMergeContext context, SegmentFileSink sink, CancellationToken cancellationToken);

    /// <summary>Opens this codec's view of one committed segment.</summary>
    /// <param name="context">The storage, schema and segment.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A reader, or <see langword="null"/> when the segment has none of this codec's parts.</returns>
    /// <exception cref="CorruptIndexException">A file is damaged; the exception names it.</exception>
    ISegmentPartReader? OpenReader(SegmentReaderContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A codec's open view of one segment. A reader is safe to use from several threads. The same object can implement the query
/// contracts of the layers above (text fields, columns, stored fields); <see cref="SegmentReader.GetPart{T}"/> finds it by
/// type.
/// </summary>
public interface ISegmentPartReader : IDisposable
{
}

/// <summary>
/// Implemented by a part reader that can find the documents holding an exact keyword value, so that
/// <see cref="IndexWriter.DeleteByTerm(string, string, CancellationToken)"/> can reach committed segments.
/// </summary>
public interface ITermDocLookup
{
    /// <summary>Adds the ids of every document of the segment whose <paramref name="field"/> has the exact value <paramref name="term"/>.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="term">The value's UTF-8 bytes.</param>
    /// <param name="destination">Receives document ids, including documents already deleted (the caller filters).</param>
    void CollectDocs(string field, ReadOnlySpan<byte> term, List<int> destination);
}

/// <summary>What <see cref="ISegmentCodec.Flush"/> receives.</summary>
public sealed class SegmentFlushContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="schema">The schema every document was validated against.</param>
    /// <param name="segmentName">The new segment's name.</param>
    /// <param name="documents">The documents to write.</param>
    /// <param name="limits">The limits to enforce.</param>
    public SegmentFlushContext(Schema schema, string segmentName, IReadOnlyList<Document> documents, IndexLimits limits)
    {
        Schema = schema;
        SegmentName = segmentName;
        Documents = documents;
        Limits = limits;
    }

    /// <summary>The schema.</summary>
    public Schema Schema { get; }

    /// <summary>The new segment's name (informational; the sink derives file names from it).</summary>
    public string SegmentName { get; }

    /// <summary>
    /// The live buffered documents. The document at index <c>i</c> becomes document id <c>i</c> of the segment. Documents
    /// deleted or replaced while still buffered are already gone, so the segment starts with no deletes.
    /// </summary>
    public IReadOnlyList<Document> Documents { get; }

    /// <summary>The limits to enforce.</summary>
    public IndexLimits Limits { get; }
}

/// <summary>What <see cref="ISegmentCodec.Merge"/> receives.</summary>
public sealed class SegmentMergeContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="segmentName">The new segment's name.</param>
    /// <param name="sources">The source segments in merge order.</param>
    /// <param name="documentCount">The live documents across all sources, which is the new segment's size.</param>
    /// <param name="limits">The limits to enforce.</param>
    public SegmentMergeContext(Schema schema, string segmentName, IReadOnlyList<MergeSource> sources, int documentCount, IndexLimits limits)
    {
        Schema = schema;
        SegmentName = segmentName;
        Sources = sources;
        DocumentCount = documentCount;
        Limits = limits;
    }

    /// <summary>The schema.</summary>
    public Schema Schema { get; }

    /// <summary>The new segment's name.</summary>
    public string SegmentName { get; }

    /// <summary>The source segments in merge order.</summary>
    public IReadOnlyList<MergeSource> Sources { get; }

    /// <summary>The number of live documents across all sources.</summary>
    public int DocumentCount { get; }

    /// <summary>The limits to enforce.</summary>
    public IndexLimits Limits { get; }
}

/// <summary>One source segment of a merge.</summary>
public sealed class MergeSource
{
    /// <summary>Creates a merge source.</summary>
    /// <param name="info">The segment as committed.</param>
    /// <param name="reader">This codec's reader for the segment, or <see langword="null"/> when the segment has none of its parts.</param>
    /// <param name="docMap">Old to new document ids.</param>
    /// <param name="live">The segment's live documents as of the merge start.</param>
    public MergeSource(SegmentInfo info, ISegmentPartReader? reader, DocMap docMap, LiveDocs live)
    {
        Info = info;
        Reader = reader;
        DocMap = docMap;
        Live = live;
    }

    /// <summary>The segment as committed.</summary>
    public SegmentInfo Info { get; }

    /// <summary>Documents including deleted ones.</summary>
    public int MaxDoc => Info.DocCount;

    /// <summary>The codec's reader for this source, valid for the duration of the merge.</summary>
    public ISegmentPartReader? Reader { get; }

    /// <summary>Old document id to new document id, or -1 for a deleted document.</summary>
    public DocMap DocMap { get; }

    /// <summary>Which documents are live; documents deleted after the merge started are still copied and deleted again in the result.</summary>
    public LiveDocs Live { get; }
}

/// <summary>
/// Renumbers a source segment's documents for a merge: live documents keep their order and are numbered consecutively from the
/// source's base; deleted documents map to -1.
/// </summary>
public sealed class DocMap
{
    private readonly int[] _map;

    internal DocMap(LiveDocs live, int maxDoc, int newBase)
    {
        _map = new int[maxDoc];
        int next = newBase;
        for (int d = 0; d < maxDoc; d++)
        {
            _map[d] = live.IsDeleted(d) ? -1 : next++;
        }

        Base = newBase;
        LiveCount = next - newBase;
    }

    /// <summary>The new id of this source's first live document.</summary>
    public int Base { get; }

    /// <summary>The live documents this source contributes.</summary>
    public int LiveCount { get; }

    /// <summary>The number of documents in the source, deleted included.</summary>
    public int MaxDoc => _map.Length;

    /// <summary>The new id of an old document.</summary>
    /// <param name="oldDoc">The document id in the source, in <c>[0, MaxDoc)</c>.</param>
    /// <returns>The id in the merged segment, or -1 if the document was deleted.</returns>
    public int this[int oldDoc] => _map[oldDoc];
}

/// <summary>What <see cref="ISegmentCodec.OpenReader"/> receives.</summary>
public sealed class SegmentReaderContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="info">The segment.</param>
    /// <param name="limits">The limits to enforce.</param>
    public SegmentReaderContext(IStorage storage, Schema schema, SegmentInfo info, IndexLimits limits)
    {
        Storage = storage;
        Schema = schema;
        Info = info;
        Limits = limits;
    }

    /// <summary>The index storage.</summary>
    public IStorage Storage { get; }

    /// <summary>The schema.</summary>
    public Schema Schema { get; }

    /// <summary>The segment being opened.</summary>
    public SegmentInfo Info { get; }

    /// <summary>The limits to enforce.</summary>
    public IndexLimits Limits { get; }

    /// <summary>Opens the file of a part.</summary>
    /// <param name="part">The part name.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <param name="file">The commit's record of the file: name, length, checksum and codec version.</param>
    /// <returns>A reader, or <see langword="null"/> if the segment has no such part.</returns>
    /// <exception cref="CorruptIndexException">The file is missing or its length differs from the commit's record.</exception>
    public IRangeReader? OpenPart(string part, out SegmentFile? file, CancellationToken cancellationToken = default)
    {
        file = Info.FindPart(part);
        if (file is null)
        {
            return null;
        }

        IRangeReader reader;
        try
        {
            reader = Storage.OpenRead(file.FileName, cancellationToken);
        }
        catch (FileNotFoundException e)
        {
            throw new CorruptIndexException(file.FileName, "listed by the commit but missing", e);
        }

        if (reader.Length != file.Length)
        {
            long actual = reader.Length;
            reader.Dispose();
            throw new CorruptIndexException(file.FileName, $"length {actual} differs from the {file.Length} the commit recorded");
        }

        return reader;
    }
}

/// <summary>
/// Creates the files of one new segment. Names are <c>&lt;segment&gt;.&lt;part&gt;</c>. The sink remembers every file it created, so
/// the writer can delete them if the flush or merge fails.
/// </summary>
public sealed class SegmentFileSink
{
    private readonly IStorage _storage;
    private readonly string _segmentName;
    private readonly List<SegmentFile> _files = [];
    private readonly List<string> _created = [];
    private readonly Action<long>? _onBytes;
    private readonly Func<string, string> _nameFor;

    internal SegmentFileSink(IStorage storage, string segmentName, Action<long>? onBytes = null, Func<string, string>? nameFor = null)
    {
        _storage = storage;
        _segmentName = segmentName;
        _onBytes = onBytes;
        _nameFor = nameFor ?? (part => $"{segmentName}.{part}");
    }

    private readonly List<IStorageWriter> _unsynced = [];

    /// <summary>
    /// When set, completing a part leaves its file open and unsynced; <see cref="SyncPending"/> then flushes all of them together,
    /// concurrently where the storage allows it. The caller must call it before anything refers to the files. Default off.
    /// </summary>
    internal bool DeferSync { get; init; }

    internal IReadOnlyList<SegmentFile> Files => _files;

    internal IReadOnlyList<string> CreatedNames => _created;

    /// <summary>The file name a part gets in this segment.</summary>
    /// <param name="part">The part.</param>
    /// <returns><c>&lt;segment&gt;.&lt;part&gt;</c>.</returns>
    public string FileNameFor(string part) => _nameFor(part);

    /// <summary>Creates the file of a part. A part can be created once per segment.</summary>
    /// <param name="part">The part, registered in <see cref="CodecRegistry"/>.</param>
    /// <param name="magicTag">Four ASCII characters that identify the file type.</param>
    /// <param name="version">The codec's format version; at most the newest the registry lists.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>A builder; call <see cref="SegmentFileBuilder.Complete"/> when the body is written.</returns>
    /// <exception cref="ArgumentException">The part is unknown, already created, or the version is not supported.</exception>
    public SegmentFileBuilder Create(string part, string magicTag, int version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(part);
        var codec = new CodecRef(part, version);
        if (!CodecRegistry.IsSupported(codec))
        {
            throw new ArgumentException($"Part '{part}' version {version} is not in the codec registry.", nameof(part));
        }

        foreach (SegmentFile f in _files)
        {
            if (string.Equals(f.Part, part, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Part '{part}' was already created for segment {_segmentName}.", nameof(part));
            }
        }

        string name = FileNameFor(part);
        if (_created.Contains(name))
        {
            throw new ArgumentException($"Part '{part}' is already being written for segment {_segmentName}.", nameof(part));
        }

        IStorageWriter writer = _storage.Create(name, cancellationToken);
        _created.Add(name);
        return new SegmentFileBuilder(this, part, name, codec, new IndexFileWriter(writer, IndexFile.Magic(magicTag), version));
    }

    internal void Register(SegmentFile file) => _files.Add(file);

    internal void AddUnsynced(IStorageWriter writer) => _unsynced.Add(writer);

    /// <summary>Syncs and closes every file completed with deferred sync.</summary>
    internal void SyncPending(CancellationToken cancellationToken)
    {
        IStorageWriter[] writers = [.. _unsynced];
        try
        {
            if (writers.Length > 1 && _storage.SupportsConcurrentSync)
            {
                // Concurrent fsyncs of different files let the file system fold their journal flushes together.
                // Only a storage that says it is safe takes this path; the others sync in order, with identical results.
                Parallel.ForEach(writers, new ParallelOptions { CancellationToken = cancellationToken }, static w => w.Sync());
            }
            else
            {
                foreach (IStorageWriter w in writers)
                {
                    w.Sync(cancellationToken);
                }
            }
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            throw;
        }
        finally
        {
            AbortPending();
        }
    }

    /// <summary>Closes files that were completed but not synced (after a failure), so they can be deleted.</summary>
    internal void AbortPending()
    {
        foreach (IStorageWriter w in _unsynced)
        {
            w.Dispose();
        }

        _unsynced.Clear();
    }

    internal void Account(long bytes) => _onBytes?.Invoke(bytes);
}

/// <summary>One segment file being written: header, body, footer and fsync are handled here.</summary>
public sealed class SegmentFileBuilder : IDisposable
{
    private readonly SegmentFileSink _sink;
    private readonly string _part;
    private readonly string _fileName;
    private readonly CodecRef _codec;
    private readonly IndexFileWriter _writer;
    private bool _done;

    internal SegmentFileBuilder(SegmentFileSink sink, string part, string fileName, CodecRef codec, IndexFileWriter writer)
    {
        _sink = sink;
        _part = part;
        _fileName = fileName;
        _codec = codec;
        _writer = writer;
    }

    /// <summary>The offset of the next body byte in the file, counting the 8-byte header.</summary>
    public long Position => _writer.Position;

    /// <summary>Appends body bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        _writer.Write(data, cancellationToken);
        _sink.Account(data.Length);
    }

    /// <summary>Writes the footer, fsyncs the file and records it for the commit.</summary>
    /// <param name="cancellationToken">Cancels the finish.</param>
    public void Complete(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        IndexFileSummary summary;
        if (_sink.DeferSync)
        {
            summary = _writer.FinishUnsynced(out IStorageWriter open, cancellationToken);
            _sink.AddUnsynced(open);
        }
        else
        {
            summary = _writer.Finish(cancellationToken);
        }

        _done = true;
        _sink.Register(new SegmentFile(_part, _fileName, _codec, summary.Length, summary.Checksum));
    }

    /// <summary>Releases the file; if it was not completed it is left for the writer to delete.</summary>
    public void Dispose()
    {
        _done = true;
        _writer.Dispose();
    }
}
