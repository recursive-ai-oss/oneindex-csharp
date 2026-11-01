using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// The stored-source codec: owns the <c>stored</c> part, the original values of every field marked stored, compressed in blocks of
/// about 32 KB with random access by document id (docs/stored-source.md). A segment of a schema with no stored field has no stored
/// file. Each document record also has an auxiliary section, empty today, reserved for per-document data kept beside the source
/// (such as cached embeddings) so that adding it needs no format change.
/// </summary>
public sealed class StoredSegmentCodec : ISegmentCodec
{
    internal static readonly uint Magic = IndexFile.Magic("OIXQ");

    /// <summary>The uncompressed size at which a block is closed.</summary>
    internal const int TargetBlockBytes = 32 * 1024;

    /// <summary>The compression method written by this version: 1 is the OneIndex LZ of <c>StoredLz</c>.</summary>
    internal const byte MethodLz = 1;

    /// <inheritdoc/>
    public string Name => "stored";

    /// <inheritdoc/>
    public IReadOnlyList<string> Parts { get; } = [SegmentParts.Stored];

    /// <inheritdoc/>
    public void Flush(SegmentFlushContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        string[] names = StoredFieldNames(context.Schema);
        if (names.Length == 0 || context.Documents.Count == 0)
        {
            return;
        }

        var index = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            index[names[i]] = i;
        }

        using var writer = new StoredFileWriter(sink, names, context.Documents.Count, cancellationToken);
        var record = new ByteBuilder(1024);
        var source = new ByteBuilder(1024);
        for (int d = 0; d < context.Documents.Count; d++)
        {
            source.Clear();
            StoredDocumentFormat.Encode(context.Documents[d], index, source);
            record.Clear();
            record.WriteVarUInt((uint)source.Length);
            record.Write(source.Span);
            record.WriteVarUInt(0); // the auxiliary section
            writer.AddRecord(record.Span);
        }

        writer.Complete();
    }

    /// <inheritdoc/>
    public void Merge(SegmentMergeContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        string[] names = StoredFieldNames(context.Schema);
        if (names.Length == 0 || context.DocumentCount == 0)
        {
            return;
        }

        using var writer = new StoredFileWriter(sink, names, context.DocumentCount, cancellationToken);
        var record = new ByteBuilder(1024);
        var rewritten = new ByteBuilder(1024);
        foreach (MergeSource source in context.Sources)
        {
            if (source.DocMap.LiveCount == 0)
            {
                continue;
            }

            if (source.Reader is not StoredPartReader part)
            {
                throw new InvalidOperationException($"Segment {source.Info.Name} has live documents but no stored source, so it cannot be merged with stored segments.");
            }

            SegmentStoredFields fields = part.Fields;
            bool sameTable = fields.FieldNames.AsSpan().SequenceEqual(names);
            int[] map = [];
            if (!sameTable)
            {
                map = new int[fields.FieldNames.Length];
                for (int i = 0; i < map.Length; i++)
                {
                    map[i] = Array.IndexOf(names, fields.FieldNames[i]);
                }
            }

            using SegmentStoredFields.Cursor cursor = fields.OpenCursor();
            for (int d = 0; d < source.MaxDoc; d++)
            {
                if (source.DocMap[d] < 0)
                {
                    continue;
                }

                if ((d & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ReadOnlySpan<byte> rec = cursor.Record(d);
                if (sameTable)
                {
                    writer.AddRecord(rec);
                    continue;
                }

                SegmentStoredFields.SplitRecord(fields.FileName, rec, out ReadOnlySpan<byte> src, out ReadOnlySpan<byte> aux);
                rewritten.Clear();
                StoredDocumentFormat.Transcode(fields.FileName, src, map, rewritten, context.Limits);
                record.Clear();
                record.WriteVarUInt((uint)rewritten.Length);
                record.Write(rewritten.Span);
                record.WriteVarUInt((uint)aux.Length);
                record.Write(aux);
                writer.AddRecord(record.Span);
            }
        }

        writer.Complete();
    }

    /// <inheritdoc/>
    public ISegmentPartReader? OpenReader(SegmentReaderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IRangeReader? reader = context.OpenPart(SegmentParts.Stored, out _, cancellationToken);
        if (reader is null)
        {
            return null;
        }

        try
        {
            return new StoredPartReader(SegmentStoredFields.Open(reader, context.Info.DocCount, context.Limits, cancellationToken));
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static string[] StoredFieldNames(Schema schema) => [.. schema.Fields.Where(f => f.Stored).Select(f => f.Name)];
}

/// <summary>Writes the blocks, then the table of contents, of one stored file.</summary>
internal sealed class StoredFileWriter : IDisposable
{
    private readonly SegmentFileBuilder _file;
    private readonly string[] _names;
    private readonly int _docCount;
    private readonly CancellationToken _cancellationToken;
    private readonly LzCompressor _lz = new();
    private readonly ByteBuilder _records = new(StoredSegmentCodec.TargetBlockBytes * 2);
    private readonly List<int> _ends = [];
    private readonly ByteBuilder _compressed = new(StoredSegmentCodec.TargetBlockBytes);
    private readonly ByteBuilder _blockEntries = new(256);
    private int _firstDoc;
    private int _docs;
    private int _blocks;

    public StoredFileWriter(SegmentFileSink sink, string[] names, int docCount, CancellationToken cancellationToken)
    {
        _names = names;
        _docCount = docCount;
        _cancellationToken = cancellationToken;
        _file = sink.Create(SegmentParts.Stored, "OIXQ", CodecRegistry.NewestVersion(SegmentParts.Stored), cancellationToken);
    }

    public void AddRecord(ReadOnlySpan<byte> record)
    {
        _records.Write(record);
        _ends.Add(_records.Length);
        _docs++;
        if (_records.Length >= StoredSegmentCodec.TargetBlockBytes)
        {
            FlushBlock();
        }
    }

    public void Complete()
    {
        if (_ends.Count > 0)
        {
            FlushBlock();
        }

        if (_firstDoc != _docCount)
        {
            throw new InvalidOperationException($"The stored writer was given {_firstDoc} records for a segment of {_docCount} documents.");
        }

        long metaAt = _file.Position;
        var meta = new ByteBuilder(256 + _blockEntries.Length);
        meta.WriteUInt32((uint)_docCount);
        meta.WriteUInt32((uint)_blocks);
        meta.WriteByte(StoredSegmentCodec.MethodLz);
        meta.WriteVarUInt((uint)_names.Length);
        foreach (string n in _names)
        {
            byte[] b = Encoding.UTF8.GetBytes(n);
            meta.WriteVarUInt((uint)b.Length);
            meta.Write(b);
        }

        meta.Write(_blockEntries.Span);
        meta.WriteUInt64((ulong)metaAt); // trailer: where the table of contents starts
        _file.Write(meta.Span, _cancellationToken);
        _file.Complete(_cancellationToken);
    }

    public void Dispose() => _file.Dispose();

    private void FlushBlock()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        int n = _ends.Count;
        var raw = new ByteBuilder(n * 4 + _records.Length);
        foreach (int end in _ends)
        {
            raw.WriteUInt32((uint)end);
        }

        raw.Write(_records.Span);
        _compressed.Clear();
        _lz.Compress(raw.Span, _compressed);
        bool useCompressed = _compressed.Length < raw.Length;
        ReadOnlySpan<byte> payload = useCompressed ? _compressed.Span : raw.Span;
        long at = _file.Position;
        _file.Write(payload, _cancellationToken);
        _blockEntries.WriteUInt32((uint)_firstDoc);
        _blockEntries.WriteUInt64((ulong)at);
        _blockEntries.WriteUInt32((uint)payload.Length);
        _blockEntries.WriteUInt32((uint)raw.Length);
        _firstDoc += n;
        _blocks++;
        _ends.Clear();
        _records.Clear();
        _docs = 0;
    }
}

/// <summary>A segment's stored source, opened. Implements <see cref="IStoredFieldsSource"/>.</summary>
internal sealed class StoredPartReader(SegmentStoredFields fields) : ISegmentPartReader, IStoredFieldsSource
{
    public SegmentStoredFields Fields { get; } = fields;

    public IStoredFieldsReader StoredFields => Fields;

    public void Dispose() => Fields.Dispose();
}

/// <summary>
/// The stored documents of one segment. A fetch finds the block of the document in the resident table of contents, reads the
/// block's range from storage, decompresses it into a pooled buffer and copies the document out. Safe for concurrent use.
/// </summary>
public sealed class SegmentStoredFields : IStoredFieldsReader, IDisposable
{
    private const int EntrySize = 20;
    private readonly IRangeReader _file;
    private readonly IndexLimits _limits;
    private readonly byte[] _entries;
    private readonly int _blockCount;

    private SegmentStoredFields(IRangeReader file, IndexLimits limits, int docCount, string[] names, byte[] entries, int blockCount)
    {
        _file = file;
        _limits = limits;
        DocCount = docCount;
        FieldNames = names;
        _entries = entries;
        _blockCount = blockCount;
    }

    /// <summary>The number of documents.</summary>
    public int DocCount { get; }

    internal string[] FieldNames { get; }

    internal string FileName => _file.Name;

    internal static SegmentStoredFields Open(IRangeReader file, int docCount, IndexLimits limits, CancellationToken cancellationToken)
    {
        string name = file.Name;
        long length = file.Length;
        if (length < IndexFile.HeaderLength + 8 + 9 + IndexFile.FooterLength)
        {
            throw new CorruptIndexException(name, $"{length} bytes is too short for stored source");
        }

        IndexFile.ReadFooterChecksum(file, cancellationToken);
        Span<byte> head = stackalloc byte[IndexFile.HeaderLength]; // fixed 8-byte buffer
        file.ReadExactly(0, head, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != StoredSegmentCodec.Magic)
        {
            throw new CorruptIndexException(name, "wrong magic: not stored source");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        if (version == 0 || version > (uint)CodecRegistry.NewestVersion(SegmentParts.Stored))
        {
            throw new UnsupportedFormatException(name, $"format version {version} is not supported");
        }

        long trailerAt = length - IndexFile.FooterLength - 8;
        Span<byte> trailer = stackalloc byte[8]; // fixed 8-byte buffer
        file.ReadExactly(trailerAt, trailer, cancellationToken);
        ulong metaAt = BinaryPrimitives.ReadUInt64LittleEndian(trailer);
        if (metaAt < IndexFile.HeaderLength || metaAt >= (ulong)trailerAt)
        {
            throw new CorruptIndexException(name, "the table of contents offset lies outside the file");
        }

        long metaBytes = trailerAt - (long)metaAt;
        if (metaBytes > limits.MaxResidentFileBytes)
        {
            throw new CorruptIndexException(name, $"the table of contents is {metaBytes} bytes, above the resident limit of {limits.MaxResidentFileBytes}");
        }

        var meta = new byte[metaBytes];
        file.ReadExactly((long)metaAt, meta, cancellationToken);
        var cursor = new ByteCursor(name, meta, 0, meta.Length);
        if (meta.Length < 9)
        {
            throw new CorruptIndexException(name, "the table of contents is too short");
        }

        uint docs = BinaryPrimitives.ReadUInt32LittleEndian(meta);
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(4));
        byte method = meta[8];
        _ = cursor.ReadBytes(9);
        if (docs != (uint)docCount || method != StoredSegmentCodec.MethodLz)
        {
            throw new CorruptIndexException(name, docs != (uint)docCount ? $"the file holds {docs} documents, the commit says {docCount}" : $"unknown compression method {method}");
        }

        int fieldCount = ResidentFile.ReadCount(name, ref cursor, limits.MaxFields, "field count");
        var names = new string[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            int n = ResidentFile.ReadCount(name, ref cursor, limits.MaxStringBytes, "field name length");
            try
            {
                names[i] = new UTF8Encoding(false, true).GetString(cursor.ReadBytes(n));
            }
            catch (DecoderFallbackException)
            {
                throw new CorruptIndexException(name, "a field name is not valid UTF-8");
            }

            // A document cannot hold a field with no name, so a table that lists one is damaged.
            if (names[i].Length == 0)
            {
                throw new CorruptIndexException(name, "a field name is empty");
            }
        }

        if ((long)blocks * EntrySize != cursor.Remaining || blocks == 0 || blocks > docs)
        {
            throw new CorruptIndexException(name, "the block table does not match the block count");
        }

        byte[] entries = meta.AsSpan(cursor.Position).ToArray();
        long maxRaw = limits.MaxResidentFileBytes;
        long expectedOffset = IndexFile.HeaderLength;
        long expectedFirst = 0;
        for (int b = 0; b < (int)blocks; b++)
        {
            ReadOnlySpan<byte> e = entries.AsSpan(b * EntrySize, EntrySize);
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(e);
            ulong at = BinaryPrimitives.ReadUInt64LittleEndian(e[4..]);
            uint comp = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            uint nextFirst = b + 1 < (int)blocks ? BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan((b + 1) * EntrySize)) : docs;
            if (first != expectedFirst || nextFirst <= first || nextFirst > docs || at != (ulong)expectedOffset || comp == 0 || comp > raw || raw > maxRaw
                || raw < (long)(nextFirst - first) * 4 || (comp < raw && raw > LzDecompressor.MaxExpandedLength((int)comp)))
            {
                throw new CorruptIndexException(name, $"block {b} has an invalid table entry");
            }

            expectedFirst = nextFirst;
            expectedOffset += comp;
        }

        if (expectedOffset != (long)metaAt || expectedFirst != docs)
        {
            throw new CorruptIndexException(name, "the blocks and table of contents do not tile the file");
        }

        return new SegmentStoredFields(file, limits, docCount, names, entries, (int)blocks);
    }

    /// <inheritdoc/>
    public Document GetDocument(int docId)
    {
        byte[]? buffer = null;
        try
        {
            ReadOnlySpan<byte> record = FetchRecord(docId, ref buffer);
            SplitRecord(FileName, record, out ReadOnlySpan<byte> source, out _);
            return StoredDocumentFormat.Decode(FileName, source, FieldNames, _limits);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <inheritdoc/>
    public bool TryGetSource(int docId, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[]? buffer = null;
        try
        {
            ReadOnlySpan<byte> record = FetchRecord(docId, ref buffer);
            SplitRecord(FileName, record, out ReadOnlySpan<byte> source, out _);
            destination.Write(source);
            return true;
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Releases the file.</summary>
    public void Dispose() => _file.Dispose();

    internal static void SplitRecord(string fileName, ReadOnlySpan<byte> record, out ReadOnlySpan<byte> source, out ReadOnlySpan<byte> aux)
    {
        int at = 0;
        ulong sourceLength = ReadVar(fileName, record, ref at);
        if (sourceLength > (ulong)(record.Length - at))
        {
            throw new CorruptIndexException(fileName, "a document's source runs past its record");
        }

        source = record.Slice(at, (int)sourceLength);
        at += (int)sourceLength;
        ulong auxLength = ReadVar(fileName, record, ref at);
        if (auxLength != (ulong)(record.Length - at))
        {
            throw new CorruptIndexException(fileName, "a document's auxiliary section does not fill its record");
        }

        aux = record[at..];
    }

    internal Cursor OpenCursor() => new(this);

    private static ulong ReadVar(string fileName, ReadOnlySpan<byte> data, ref int at)
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (at >= data.Length)
            {
                throw new CorruptIndexException(fileName, "a record ends early");
            }

            byte b = data[at++];
            if (shift == 63 && b > 1)
            {
                throw new CorruptIndexException(fileName, "a record length overflows");
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new CorruptIndexException(fileName, "a record length is too long");
    }

    private int BlockOf(int docId)
    {
        if ((uint)docId >= (uint)DocCount)
        {
            throw new ArgumentOutOfRangeException(nameof(docId), docId, "The document id is outside the segment.");
        }

        int lo = 0;
        int hi = _blockCount - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo + 1) >> 1);
            if (BinaryPrimitives.ReadUInt32LittleEndian(_entries.AsSpan(mid * EntrySize)) <= (uint)docId)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    // Loads and decompresses a block into `raw` (rented, grown as needed). Returns the block's first document and document count.
    private void LoadBlock(int block, ref byte[]? raw, out int rawLength, out int firstDoc, out int docs)
    {
        ReadOnlySpan<byte> e = _entries.AsSpan(block * EntrySize, EntrySize);
        firstDoc = (int)BinaryPrimitives.ReadUInt32LittleEndian(e);
        long at = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[4..]);
        int comp = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
        rawLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
        int next = block + 1 < _blockCount ? (int)BinaryPrimitives.ReadUInt32LittleEndian(_entries.AsSpan((block + 1) * EntrySize)) : DocCount;
        docs = next - firstDoc;
        if (raw is null || raw.Length < rawLength)
        {
            if (raw is not null)
            {
                ArrayPool<byte>.Shared.Return(raw);
            }

            raw = ArrayPool<byte>.Shared.Rent(rawLength);
        }

        if (comp == rawLength)
        {
            _file.ReadExactly(at, raw.AsSpan(0, rawLength));
        }
        else
        {
            byte[] packed = ArrayPool<byte>.Shared.Rent(comp);
            try
            {
                _file.ReadExactly(at, packed.AsSpan(0, comp));
                if (!LzDecompressor.TryDecompress(packed.AsSpan(0, comp), raw.AsSpan(0, rawLength)))
                {
                    throw new CorruptIndexException(FileName, $"block {block} does not decompress to its recorded size");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packed);
            }
        }

        // The block starts with the cumulative end of each record; they must rise and fit.
        int prev = 0;
        int records = rawLength - (docs * 4);
        for (int i = 0; i < docs; i++)
        {
            int end = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
            if (end < prev || end > records)
            {
                throw new CorruptIndexException(FileName, $"block {block} has an invalid record table");
            }

            prev = end;
        }

        if (prev != records)
        {
            throw new CorruptIndexException(FileName, $"block {block}'s records do not fill the block");
        }
    }

    private ReadOnlySpan<byte> FetchRecord(int docId, ref byte[]? buffer)
    {
        int block = BlockOf(docId);
        LoadBlock(block, ref buffer, out int rawLength, out int firstDoc, out int docs);
        return RecordIn(buffer!, rawLength, firstDoc, docs, docId);
    }

    private static ReadOnlySpan<byte> RecordIn(byte[] raw, int rawLength, int firstDoc, int docs, int docId)
    {
        int j = docId - firstDoc;
        int start = j == 0 ? 0 : (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((j - 1) * 4));
        int end = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(j * 4));
        _ = rawLength;
        return raw.AsSpan((docs * 4) + start, end - start);
    }

    /// <summary>Reads documents in order, decompressing each block once (used by merges). Not thread-safe.</summary>
    internal sealed class Cursor : IDisposable
    {
        private readonly SegmentStoredFields _owner;
        private byte[]? _raw;
        private int _rawLength;
        private int _firstDoc = -1;
        private int _docs;

        public Cursor(SegmentStoredFields owner) => _owner = owner;

        public ReadOnlySpan<byte> Record(int docId)
        {
            if (_raw is null || docId < _firstDoc || docId >= _firstDoc + _docs)
            {
                _owner.LoadBlock(_owner.BlockOf(docId), ref _raw, out _rawLength, out _firstDoc, out _docs);
            }

            return RecordIn(_raw!, _rawLength, _firstDoc, _docs, docId);
        }

        public void Dispose()
        {
            if (_raw is not null)
            {
                ArrayPool<byte>.Shared.Return(_raw);
                _raw = null;
            }
        }
    }
}
