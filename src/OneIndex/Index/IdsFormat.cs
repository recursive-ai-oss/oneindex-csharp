using System.Buffers;
using System.Buffers.Binary;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// The <c>ids</c> part, <c>OIXI</c> version 1: a sorted map from a segment's document identifiers to document ids, so the index
/// can find a document by identifier (replace it, delete it, look it up) without any other codec. Body: <c>u32 count</c>; a table
/// of <c>count</c> entries of (<c>u32 dataOffset</c>, <c>u32 docId</c>) sorted by the identifier's UTF-8 bytes in ordinal order
/// with no two equal; then the identifiers, each a varint length and its bytes, stored back to back in table order, so each
/// <c>dataOffset</c> is where the previous identifier ended. Lookups binary-search the table with range reads and hold nothing in
/// memory.
/// </summary>
public static class IdsFormat
{
    /// <summary>The format version this build writes and reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>The magic of an ids file.</summary>
    public static readonly uint Magic = IndexFile.Magic("OIXI");

    internal const int TableStart = IndexFile.HeaderLength + 4;

    /// <summary>Writes the ids file of a segment.</summary>
    /// <param name="sink">The segment's file sink.</param>
    /// <param name="entries">Identifier (UTF-8) and document id; the identifiers must be distinct.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">Two documents of the segment have the same identifier.</exception>
    public static void Write(SegmentFileSink sink, List<KeyValuePair<byte[], int>> entries, CancellationToken cancellationToken = default)
    {
        entries.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i - 1].Key.AsSpan().SequenceEqual(entries[i].Key))
            {
                throw new ArgumentException("Two documents of one segment have the same identifier.", nameof(entries));
            }
        }

        var data = new ByteSink();
        var table = new ByteSink();
        table.WriteUInt32((uint)entries.Count);
        foreach (KeyValuePair<byte[], int> e in entries)
        {
            table.WriteUInt32((uint)data.Length);
            table.WriteUInt32((uint)e.Value);
            data.WriteBytes(e.Key);
        }

        using SegmentFileBuilder file = sink.Create(SegmentParts.Ids, "OIXI", FormatVersion, cancellationToken);
        file.Write(table.WrittenSpan, cancellationToken);
        file.Write(data.WrittenSpan, cancellationToken);
        file.Complete(cancellationToken);
    }

    /// <summary>Validates a whole ids file thoroughly (order, offsets, ranges, distinct document ids).</summary>
    /// <param name="fileName">The file name for messages.</param>
    /// <param name="file">The whole file.</param>
    /// <param name="maxDoc">The segment's document count.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="error">What is wrong on failure.</param>
    /// <returns><see langword="true"/> if valid.</returns>
    /// <exception cref="UnsupportedFormatException">The file is intact but of a newer version.</exception>
    public static bool TryValidate(string fileName, ReadOnlySpan<byte> file, int maxDoc, IndexLimits? limits, out string? error)
    {
        limits ??= IndexLimits.Default;
        try
        {
            IndexFile.Validate(fileName, file, Magic, FormatVersion);
        }
        catch (UnsupportedFormatException)
        {
            throw;
        }
        catch (CorruptIndexException e)
        {
            error = e.Message;
            return false;
        }

        try
        {
            var src = new ByteSource(file[IndexFile.HeaderLength..^IndexFile.FooterLength]);
            uint countRaw = src.ReadUInt32();
            if (countRaw > (uint)maxDoc || countRaw > src.Remaining / 8)
            {
                throw new MalformedInputException("identifier count is out of range");
            }

            int count = (int)countRaw;
            var offsets = new uint[count];
            var docs = new int[count];
            for (int i = 0; i < count; i++)
            {
                offsets[i] = src.ReadUInt32();
                uint doc = src.ReadUInt32();
                if (doc >= (uint)maxDoc)
                {
                    throw new MalformedInputException("document id is out of range");
                }

                docs[i] = (int)doc;
            }

            long position = 0;
            ReadOnlySpan<byte> previous = default;
            for (int i = 0; i < count; i++)
            {
                if (offsets[i] != position)
                {
                    throw new MalformedInputException("identifier offsets are not sequential");
                }

                int before = src.Remaining;
                ReadOnlySpan<byte> id = src.ReadBytes(limits.MaxStringBytes, "identifier");
                position += before - src.Remaining;
                if (i > 0 && previous.SequenceCompareTo(id) >= 0)
                {
                    throw new MalformedInputException("identifiers are not strictly ascending");
                }

                previous = id;
            }

            if (src.Remaining != 0)
            {
                throw new MalformedInputException("trailing bytes after the identifiers");
            }

            Array.Sort(docs);
            for (int i = 1; i < count; i++)
            {
                if (docs[i] == docs[i - 1])
                {
                    throw new MalformedInputException("two identifiers share a document id");
                }
            }

            error = null;
            return true;
        }
        catch (MalformedInputException e)
        {
            error = e.Message;
            return false;
        }
    }
}

/// <summary>Reads an <c>ids</c> file by binary search over range reads. Safe for concurrent use.</summary>
public sealed class IdsReader : IDisposable
{
    private readonly IRangeReader _reader;
    private readonly string _fileName;
    private readonly int _count;
    private readonly long _dataStart;
    private readonly long _dataEnd;
    private readonly int _maxDoc;
    private readonly int _maxIdBytes;

    /// <summary>Opens an ids file and checks its header against the file length.</summary>
    /// <param name="reader">The file; the reader takes ownership.</param>
    /// <param name="maxDoc">The segment's document count.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <exception cref="CorruptIndexException">The header is damaged.</exception>
    public IdsReader(IRangeReader reader, int maxDoc, IndexLimits? limits = null)
    {
        _reader = reader;
        _fileName = reader.Name;
        _maxDoc = maxDoc;
        _maxIdBytes = (limits ?? IndexLimits.Default).MaxStringBytes;
        try
        {
            Span<byte> head = stackalloc byte[IdsFormat.TableStart]; // fixed 12-byte header
            if (reader.Length < IdsFormat.TableStart + IndexFile.FooterLength)
            {
                throw new CorruptIndexException(_fileName, "file is shorter than its header and footer");
            }

            reader.ReadExactly(0, head);
            if (BinaryPrimitives.ReadUInt32LittleEndian(head) != IdsFormat.Magic)
            {
                throw new CorruptIndexException(_fileName, "wrong magic: not an ids file");
            }

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (version != IdsFormat.FormatVersion)
            {
                throw new UnsupportedFormatException(_fileName, $"ids format version {version} is not supported");
            }

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
            long body = reader.Length - IdsFormat.TableStart - IndexFile.FooterLength;
            if (count > (uint)maxDoc || count * 8L > body)
            {
                throw new CorruptIndexException(_fileName, "identifier count is out of range");
            }

            _count = (int)count;
            _dataStart = IdsFormat.TableStart + (count * 8L);
            _dataEnd = reader.Length - IndexFile.FooterLength;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The number of identifiers.</summary>
    public int Count => _count;

    /// <summary>Finds the document with an identifier. Allocates nothing for identifiers up to 256 bytes.</summary>
    /// <param name="id">The identifier's UTF-8 bytes.</param>
    /// <param name="docId">The document id on success.</param>
    /// <returns><see langword="true"/> if the segment holds the identifier.</returns>
    /// <exception cref="CorruptIndexException">A probed entry is out of range.</exception>
    public bool TryFind(ReadOnlySpan<byte> id, out int docId)
    {
        docId = -1;
        int lo = 0;
        int hi = _count - 1;
        byte[]? rented = null;
        Span<byte> stack = stackalloc byte[256 + 5]; // varint length plus up to 256 identifier bytes
        Span<byte> entry = stackalloc byte[8]; // one table entry
        try
        {
            while (lo <= hi)
            {
                int mid = (int)(((uint)lo + (uint)hi) >> 1);
                _reader.ReadExactly(IdsFormat.TableStart + (mid * 8L), entry);
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                uint doc = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                long at = _dataStart + offset;
                if (at >= _dataEnd || doc >= (uint)_maxDoc)
                {
                    throw new CorruptIndexException(_fileName, "table entry points outside the file");
                }

                int want = (int)Math.Min(_dataEnd - at, 5 + (long)_maxIdBytes);
                Span<byte> buffer = want <= stack.Length ? stack[..want] : (rented ??= ArrayPool<byte>.Shared.Rent(5 + _maxIdBytes)).AsSpan(0, want);
                _reader.ReadExactly(at, buffer);
                int length = 0;
                int shift = 0;
                int used = 0;
                while (true)
                {
                    if (used >= buffer.Length || shift > 28)
                    {
                        throw new CorruptIndexException(_fileName, "identifier length is malformed");
                    }

                    byte b = buffer[used++];
                    length |= (b & 0x7F) << shift;
                    shift += 7;
                    if ((b & 0x80) == 0)
                    {
                        break;
                    }
                }

                if (length < 0 || length > _maxIdBytes || used + length > buffer.Length)
                {
                    throw new CorruptIndexException(_fileName, "identifier runs past the data");
                }

                int cmp = id.SequenceCompareTo(buffer.Slice(used, length));
                if (cmp == 0)
                {
                    docId = (int)doc;
                    return true;
                }

                if (cmp < 0)
                {
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return false;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Reads every entry (used by merges).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Identifier bytes and document ids, in identifier order.</returns>
    /// <exception cref="CorruptIndexException">The file fails <see cref="IdsFormat.TryValidate"/>.</exception>
    public List<KeyValuePair<byte[], int>> ReadAll(CancellationToken cancellationToken = default)
    {
        if (_reader.Length > int.MaxValue)
        {
            throw new CorruptIndexException(_fileName, "file is too large");
        }

        var bytes = new byte[(int)_reader.Length];
        _reader.ReadExactly(0, bytes, cancellationToken);
        if (!IdsFormat.TryValidate(_fileName, bytes, _maxDoc, null, out string? error))
        {
            throw new CorruptIndexException(_fileName, error!);
        }

        var result = new List<KeyValuePair<byte[], int>>(_count);
        var src = new ByteSource(bytes.AsSpan(IdsFormat.TableStart, bytes.Length - IdsFormat.TableStart - IndexFile.FooterLength));
        var docs = new int[_count];
        for (int i = 0; i < _count; i++)
        {
            _ = src.ReadUInt32();
            docs[i] = (int)src.ReadUInt32();
        }

        for (int i = 0; i < _count; i++)
        {
            result.Add(new KeyValuePair<byte[], int>(src.ReadBytes(_maxIdBytes, "identifier").ToArray(), docs[i]));
        }

        return result;
    }

    /// <inheritdoc/>
    public void Dispose() => _reader.Dispose();
}
