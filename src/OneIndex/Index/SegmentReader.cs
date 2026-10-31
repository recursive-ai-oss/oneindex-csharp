using System.Buffers;
using System.Text;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// One committed segment opened for reading: its deleted documents, the identifier map and every codec's part reader. A reader
/// is safe for concurrent use and must be disposed; it holds file handles.
/// </summary>
public sealed class SegmentReader : IDisposable
{
    private readonly List<ISegmentPartReader> _parts = [];
    private readonly List<(ISegmentCodec Codec, ISegmentPartReader Reader)> _byCodec = [];

    private SegmentReader(SegmentInfo info, Schema schema, LiveDocs live)
    {
        Info = info;
        Schema = schema;
        Live = live;
    }

    /// <summary>The segment as committed.</summary>
    public SegmentInfo Info { get; }

    /// <summary>The schema of the index.</summary>
    public Schema Schema { get; }

    /// <summary>The number of documents including deleted ones.</summary>
    public int MaxDoc => Info.DocCount;

    /// <summary>The deleted documents as of the commit this reader was opened for.</summary>
    public LiveDocs Live { get; }

    internal IdsReader? Ids { get; private set; }

    /// <summary>Opens a segment.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="info">The segment as listed in a commit.</param>
    /// <param name="codecs">The codecs to open parts with.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="live">The deleted set if already loaded, otherwise it is read from the delete file.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="CorruptIndexException">A file is missing or damaged.</exception>
    public static SegmentReader Open(IStorage storage, Schema schema, SegmentInfo info, IReadOnlyList<ISegmentCodec> codecs, IndexLimits limits, LiveDocs? live = null, CancellationToken cancellationToken = default)
    {
        live ??= LoadLiveDocs(storage, info, limits, cancellationToken);
        var reader = new SegmentReader(info, schema, live);
        try
        {
            var context = new SegmentReaderContext(storage, schema, info, limits);
            if (info.FindPart(SegmentParts.Ids) is not null)
            {
                reader.Ids = new IdsReader(context.OpenPart(SegmentParts.Ids, out _, cancellationToken)!, info.DocCount, limits);
            }

            foreach (ISegmentCodec codec in codecs)
            {
                ISegmentPartReader? part = codec.OpenReader(context, cancellationToken);
                if (part is not null)
                {
                    reader._parts.Add(part);
                    reader._byCodec.Add((codec, part));
                }
            }

            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Reads the deleted set a commit records for a segment.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="info">The segment.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The deleted set; everything live when the segment has no delete file.</returns>
    /// <exception cref="CorruptIndexException">The delete file is missing, damaged or disagrees with the commit.</exception>
    public static LiveDocs LoadLiveDocs(IStorage storage, SegmentInfo info, IndexLimits limits, CancellationToken cancellationToken = default)
    {
        SegmentFile? file = info.FindPart(SegmentParts.Deletes);
        if (info.DeleteGeneration == 0 || file is null)
        {
            if (info.DeleteGeneration != 0 || info.DeletedCount != 0 || file is not null)
            {
                throw new CorruptIndexException(info.Name, "the commit records deletes but lists no delete file, or the reverse");
            }

            return LiveDocs.AllLive(info.DocCount);
        }

        using IRangeReader reader = storage.OpenRead(file.FileName, cancellationToken);
        if (reader.Length != file.Length || reader.Length > int.MaxValue)
        {
            throw new CorruptIndexException(file.FileName, $"length {reader.Length} differs from the {file.Length} the commit recorded");
        }

        var bytes = new byte[(int)reader.Length];
        reader.ReadExactly(0, bytes, cancellationToken);
        return DeleteFileFormat.Parse(file.FileName, bytes, info.DocCount, info.DeletedCount, limits);
    }

    /// <summary>The first part reader of type <typeparamref name="T"/>, such as a codec's text-field or stored-fields reader.</summary>
    /// <typeparam name="T">A reader contract implemented by a codec's part reader.</typeparam>
    /// <returns>The reader, or <see langword="null"/> if no codec of the segment provides one.</returns>
    public T? GetPart<T>()
        where T : class
    {
        foreach (ISegmentPartReader p in _parts)
        {
            if (p is T t)
            {
                return t;
            }
        }

        return null;
    }

    internal ISegmentPartReader? GetPartOf(ISegmentCodec codec)
    {
        foreach ((ISegmentCodec c, ISegmentPartReader r) in _byCodec)
        {
            if (ReferenceEquals(c, codec))
            {
                return r;
            }
        }

        return null;
    }

    /// <summary>Finds a live document by identifier. Allocates nothing for identifiers whose UTF-8 form is up to 256 bytes.</summary>
    /// <param name="id">The identifier.</param>
    /// <param name="docId">The document id on success.</param>
    /// <returns><see langword="true"/> if the segment holds a live document with this identifier.</returns>
    /// <exception cref="InvalidOperationException">The schema has no identifier field.</exception>
    public bool TryFindId(ReadOnlySpan<char> id, out int docId)
    {
        docId = -1;
        if (Ids is null)
        {
            return false;
        }

        int max = Encoding.UTF8.GetMaxByteCount(id.Length);
        byte[]? rented = null;
        Span<byte> stack = stackalloc byte[256]; // fixed buffer for short identifiers
        Span<byte> bytes = max <= stack.Length ? stack : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            int n = Encoding.UTF8.GetBytes(id, bytes);
            return Ids.TryFind(bytes[..n], out docId) && Live.IsLive(docId);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (ISegmentPartReader p in _parts)
        {
            p.Dispose();
        }

        _parts.Clear();
        _byCodec.Clear();
        Ids?.Dispose();
        Ids = null;
    }
}
