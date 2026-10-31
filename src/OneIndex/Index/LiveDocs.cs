using System.Buffers.Binary;
using System.Numerics;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// The documents of one segment that are deleted, as an immutable view. Deleted documents keep their ids until a merge rewrites
/// the segment. A set that is mostly empty is held as a sorted list of ids; a larger one as a bitmap.
/// </summary>
public sealed class LiveDocs
{
    private readonly ulong[]? _words;
    private readonly int[]? _sorted;

    private LiveDocs(int maxDoc, int deletedCount, ulong[]? words, int[]? sorted)
    {
        MaxDoc = maxDoc;
        DeletedCount = deletedCount;
        _words = words;
        _sorted = sorted;
    }

    /// <summary>A segment of <paramref name="maxDoc"/> documents with none deleted.</summary>
    /// <param name="maxDoc">The number of documents.</param>
    /// <returns>The view.</returns>
    public static LiveDocs AllLive(int maxDoc) => new(maxDoc, 0, null, []);

    /// <summary>The number of documents including deleted ones.</summary>
    public int MaxDoc { get; }

    /// <summary>The number of deleted documents.</summary>
    public int DeletedCount { get; }

    /// <summary>The number of live documents.</summary>
    public int LiveCount => MaxDoc - DeletedCount;

    /// <summary>Whether a document is deleted.</summary>
    /// <param name="docId">The document id, in <c>[0, MaxDoc)</c>.</param>
    /// <returns><see langword="true"/> if deleted. Out-of-range ids are reported as deleted.</returns>
    public bool IsDeleted(int docId)
    {
        if ((uint)docId >= (uint)MaxDoc)
        {
            return true;
        }

        if (_words is not null)
        {
            return (_words[docId >> 6] >> (docId & 63) & 1) != 0;
        }

        return _sorted is { Length: > 0 } && Array.BinarySearch(_sorted, docId) >= 0;
    }

    /// <summary>Whether a document is live.</summary>
    /// <param name="docId">The document id.</param>
    /// <returns><see langword="true"/> if the id is in range and not deleted.</returns>
    public bool IsLive(int docId) => !IsDeleted(docId);

    internal static LiveDocs FromWords(int maxDoc, ulong[] words)
    {
        int count = 0;
        foreach (ulong w in words)
        {
            count += BitOperations.PopCount(w);
        }

        return new LiveDocs(maxDoc, count, words, null);
    }

    internal static LiveDocs FromSorted(int maxDoc, int[] sorted) => new(maxDoc, sorted.Length, null, sorted);

    /// <summary>A mutable bitmap copy, for the writer to add deletions to.</summary>
    internal ulong[] ToWords()
    {
        var words = new ulong[(MaxDoc + 63) >> 6];
        if (_words is not null)
        {
            _words.CopyTo(words, 0);
        }
        else if (_sorted is not null)
        {
            foreach (int d in _sorted)
            {
                words[d >> 6] |= 1UL << (d & 63);
            }
        }

        return words;
    }
}

/// <summary>
/// The delete file of one segment generation, <c>OIXD</c> version 1. Body: <c>u32 maxDoc</c>, <c>u32 deletedCount</c>,
/// <c>u8 encoding</c>, then either (0) the bitmap as <c>ceil(maxDoc/64)</c> little-endian <c>u64</c> words, or (1) the sorted ids as
/// varints: the first id, then each gap minus one. The writer picks the smaller encoding (the bitmap on a tie), so equal sets give
/// equal bytes. A reader rejects a count that disagrees with the ids, bits at or past <c>maxDoc</c>, ids out of order or range, and
/// trailing bytes.
/// </summary>
public static class DeleteFileFormat
{
    /// <summary>The format version this build writes and reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>The magic of a delete file.</summary>
    public static readonly uint Magic = IndexFile.Magic("OIXD");

    /// <summary>Serializes a deleted set (the frame is added by <see cref="IndexFileWriter"/>; this is the body).</summary>
    /// <param name="maxDoc">The segment's document count.</param>
    /// <param name="words">The deleted documents as a bitmap of <c>ceil(maxDoc/64)</c> words.</param>
    /// <returns>The body bytes.</returns>
    public static byte[] SerializeBody(int maxDoc, ReadOnlySpan<ulong> words)
    {
        int count = 0;
        foreach (ulong w in words)
        {
            count += BitOperations.PopCount(w);
        }

        var sink = new ByteSink();
        sink.WriteUInt32((uint)maxDoc);
        sink.WriteUInt32((uint)count);

        var ids = new ByteSink();
        int previous = -1;
        for (int wi = 0; wi < words.Length; wi++)
        {
            ulong w = words[wi];
            while (w != 0)
            {
                int bit = BitOperations.TrailingZeroCount(w);
                w &= w - 1;
                int id = (wi << 6) + bit;
                ids.WriteVarUInt(previous < 0 ? (ulong)id : (ulong)(id - previous - 1));
                previous = id;
            }
        }

        if (ids.Length < words.Length * 8)
        {
            sink.WriteByte(1);
            sink.WriteBytesRaw(ids.WrittenSpan);
        }
        else
        {
            sink.WriteByte(0);
            Span<byte> buf = stackalloc byte[8]; // one little-endian word
            foreach (ulong w in words)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buf, w);
                sink.WriteBytesRaw(buf);
            }
        }

        return sink.ToArray();
    }

    /// <summary>Parses a whole delete file, frame included.</summary>
    /// <param name="fileName">The file's name, for exceptions.</param>
    /// <param name="file">The whole file.</param>
    /// <param name="expectedMaxDoc">The segment's document count from the commit.</param>
    /// <param name="expectedDeleted">The deleted count from the commit.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <returns>The deleted set.</returns>
    /// <exception cref="CorruptIndexException">The file is damaged or disagrees with the commit.</exception>
    public static LiveDocs Parse(string fileName, ReadOnlySpan<byte> file, int expectedMaxDoc, int expectedDeleted, IndexLimits? limits = null)
    {
        if (!TryParse(fileName, file, expectedMaxDoc, expectedDeleted, limits, out LiveDocs? live, out string? error))
        {
            throw new CorruptIndexException(fileName, error!);
        }

        return live!;
    }

    /// <summary>Parses a whole delete file without throwing for malformed content.</summary>
    /// <param name="fileName">The file's name.</param>
    /// <param name="file">The whole file.</param>
    /// <param name="expectedMaxDoc">The segment's document count, or -1 to accept the file's own.</param>
    /// <param name="expectedDeleted">The deleted count, or -1 to accept the file's own.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="live">The deleted set on success.</param>
    /// <param name="error">What is wrong on failure.</param>
    /// <returns><see langword="true"/> if the file is valid.</returns>
    /// <exception cref="UnsupportedFormatException">The file is intact but of a newer version.</exception>
    public static bool TryParse(string fileName, ReadOnlySpan<byte> file, int expectedMaxDoc, int expectedDeleted, IndexLimits? limits, out LiveDocs? live, out string? error)
    {
        limits ??= IndexLimits.Default;
        live = null;
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
            uint maxDocRaw = src.ReadUInt32();
            uint countRaw = src.ReadUInt32();
            if (maxDocRaw > (uint)limits.MaxDocsPerSegment || countRaw > maxDocRaw)
            {
                throw new MalformedInputException("document or deleted count is out of range");
            }

            int maxDoc = (int)maxDocRaw;
            int count = (int)countRaw;
            if ((expectedMaxDoc >= 0 && maxDoc != expectedMaxDoc) || (expectedDeleted >= 0 && count != expectedDeleted))
            {
                throw new MalformedInputException($"covers {maxDoc} documents with {count} deleted but the commit says {expectedMaxDoc} and {expectedDeleted}");
            }

            byte encoding = src.ReadByte();
            if (encoding == 0)
            {
                int wordCount = (maxDoc + 63) >> 6;
                if (src.Remaining != wordCount * 8L)
                {
                    throw new MalformedInputException("bitmap length does not match the document count");
                }

                var words = new ulong[wordCount];
                int bits = 0;
                for (int i = 0; i < wordCount; i++)
                {
                    words[i] = src.ReadUInt64();
                    bits += BitOperations.PopCount(words[i]);
                }

                if (bits != count)
                {
                    throw new MalformedInputException("bitmap population differs from the deleted count");
                }

                if ((maxDoc & 63) != 0 && wordCount > 0 && (words[wordCount - 1] >> (maxDoc & 63)) != 0)
                {
                    throw new MalformedInputException("bitmap has bits set past the last document");
                }

                live = LiveDocs.FromWords(maxDoc, words);
            }
            else if (encoding == 1)
            {
                // Each id takes at least one byte, so the count cannot exceed what is left: no allocation past the file's size.
                if (count > src.Remaining)
                {
                    throw new MalformedInputException("deleted count exceeds the data");
                }

                var ids = new int[count];
                long previous = -1;
                for (int i = 0; i < count; i++)
                {
                    ulong v = src.ReadVarUInt();
                    if (v >= (ulong)maxDoc)
                    {
                        throw new MalformedInputException("deleted id is out of range");
                    }

                    long id = previous + 1 + (long)v;
                    if (id >= maxDoc)
                    {
                        throw new MalformedInputException("deleted id is out of range");
                    }

                    ids[i] = (int)id;
                    previous = id;
                }

                if (src.Remaining != 0)
                {
                    throw new MalformedInputException("trailing bytes after the deleted ids");
                }

                live = LiveDocs.FromSorted(maxDoc, ids);
            }
            else
            {
                throw new MalformedInputException($"unknown encoding {encoding}");
            }

            error = null;
            return true;
        }
        catch (MalformedInputException e)
        {
            live = null;
            error = e.Message;
            return false;
        }
    }
}
