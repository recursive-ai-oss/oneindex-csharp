using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// A segment's text index opened for reading. The term dictionaries and field lengths are read into memory once (they are small
/// next to the postings) and verified against their checksums; postings are not loaded: each postings iterator reads the
/// storage ranges it needs. Opening validates the structure of all three files and the footer of the postings file; the
/// postings file's full checksum is checked by <see cref="VerifyPostingsChecksum"/> (a commit's check of the recorded checksum
/// already guards the whole file). Any inconsistency raises <see cref="CorruptIndexException"/> naming the file.
/// </summary>
public sealed class TextSegmentReader : ITextFieldSource, IDisposable
{
    private readonly IRangeReader _postings;
    private readonly Dictionary<string, TextFieldReader> _fields = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];

    private TextSegmentReader(IRangeReader postings, int maxDoc)
    {
        _postings = postings;
        MaxDoc = maxDoc;
    }

    /// <summary>One more than the largest document id of the segment.</summary>
    public int MaxDoc { get; }

    /// <summary>The names of the fields that have terms, in ordinal order.</summary>
    public IReadOnlyList<string> FieldNames => _names;

    /// <summary>Opens the three files of a segment.</summary>
    /// <param name="storage">Where the files are.</param>
    /// <param name="termsFile">The terms file name.</param>
    /// <param name="postingsFile">The postings file name.</param>
    /// <param name="lengthsFile">The lengths file name.</param>
    /// <param name="limits">Limits; <see cref="IndexLimits.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reader; dispose it to release the postings file.</returns>
    /// <exception cref="CorruptIndexException">A file is damaged, inconsistent with the others, or over a limit.</exception>
    /// <exception cref="UnsupportedFormatException">A file was written with a newer format.</exception>
    public static TextSegmentReader Open(IStorage storage, string termsFile, string postingsFile, string lengthsFile, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        IRangeReader terms = storage.OpenRead(termsFile, cancellationToken);
        IRangeReader lengths;
        try
        {
            lengths = storage.OpenRead(lengthsFile, cancellationToken);
        }
        catch
        {
            terms.Dispose();
            throw;
        }

        IRangeReader postings;
        try
        {
            postings = storage.OpenRead(postingsFile, cancellationToken);
        }
        catch
        {
            terms.Dispose();
            lengths.Dispose();
            throw;
        }

        return Open(terms, postings, lengths, limits, cancellationToken);
    }

    /// <summary>Opens a segment from three already-open files. The reader takes ownership of all three: the terms and lengths files are read and closed, the postings file stays open until <see cref="Dispose"/>; on failure all are closed.</summary>
    /// <param name="terms">The terms file.</param>
    /// <param name="postings">The postings file.</param>
    /// <param name="lengths">The lengths file.</param>
    /// <param name="limits">Limits; <see cref="IndexLimits.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reader.</returns>
    /// <exception cref="CorruptIndexException">A file is damaged, inconsistent with the others, or over a limit.</exception>
    public static TextSegmentReader Open(IRangeReader terms, IRangeReader postings, IRangeReader lengths, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(postings);
        ArgumentNullException.ThrowIfNull(lengths);
        limits ??= IndexLimits.Default;
        try
        {
            string termsFile = terms.Name;
            string lengthsFile = lengths.Name;
            byte[] termBytes = ReadResident(terms, limits, cancellationToken);
            byte[] lengthBytes = ReadResident(lengths, limits, cancellationToken);
            IndexFile.ReadFooterChecksum(postings, cancellationToken);
            Span<byte> header = stackalloc byte[IndexFile.HeaderLength]; // fixed 8-byte buffer
            postings.ReadExactly(0, header, cancellationToken);
            return Create(termsFile, postings.Name, lengthsFile, termBytes, lengthBytes, postings, header, limits);
        }
        catch
        {
            terms.Dispose();
            lengths.Dispose();
            postings.Dispose();
            throw;
        }
    }

    /// <summary>The asynchronous twin of <see cref="Open(IStorage, string, string, string, IndexLimits?, CancellationToken)"/>. Searching afterwards is synchronous: it reads small ranges and must not await.</summary>
    /// <param name="storage">Where the files are.</param>
    /// <param name="termsFile">The terms file name.</param>
    /// <param name="postingsFile">The postings file name.</param>
    /// <param name="lengthsFile">The lengths file name.</param>
    /// <param name="limits">Limits; <see cref="IndexLimits.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The reader.</returns>
    public static async ValueTask<TextSegmentReader> OpenAsync(IStorage storage, string termsFile, string postingsFile, string lengthsFile, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        limits ??= IndexLimits.Default;
        byte[] terms = await ReadResidentAsync(await storage.OpenReadAsync(termsFile, cancellationToken).ConfigureAwait(false), limits, cancellationToken).ConfigureAwait(false);
        byte[] lengths = await ReadResidentAsync(await storage.OpenReadAsync(lengthsFile, cancellationToken).ConfigureAwait(false), limits, cancellationToken).ConfigureAwait(false);
        IRangeReader postings = await storage.OpenReadAsync(postingsFile, cancellationToken).ConfigureAwait(false);
        try
        {
            await IndexFile.ReadFooterChecksumAsync(postings, cancellationToken).ConfigureAwait(false);
            var header = new byte[IndexFile.HeaderLength];
            await postings.ReadExactlyAsync(0, header, cancellationToken).ConfigureAwait(false);
            return Create(termsFile, postingsFile, lengthsFile, terms, lengths, postings, header, limits);
        }
        catch
        {
            postings.Dispose();
            throw;
        }
    }

    /// <summary>Gets a field's reader.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The reader, or <see langword="null"/> when the segment has no terms for the field.</returns>
    public ITextFieldReader? GetTextField(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return _fields.GetValueOrDefault(field);
    }

    /// <summary>Reads the whole postings file and checks it against its footer checksum.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="CorruptIndexException">The checksum does not match.</exception>
    public void VerifyPostingsChecksum(CancellationToken cancellationToken = default) => IndexFile.VerifyChecksum(_postings, cancellationToken);

    /// <summary>Releases the postings file.</summary>
    public void Dispose() => _postings.Dispose();

    private static byte[] ReadResident(IRangeReader reader, IndexLimits limits, CancellationToken cancellationToken)
    {
        using (reader)
        {
            if (reader.Length > limits.MaxResidentFileBytes)
            {
                throw new CorruptIndexException(reader.Name, $"{reader.Length} bytes is above the resident file limit of {limits.MaxResidentFileBytes}");
            }

            var bytes = new byte[reader.Length];
            reader.ReadExactly(0, bytes, cancellationToken);
            return bytes;
        }
    }

    private static async ValueTask<byte[]> ReadResidentAsync(IRangeReader reader, IndexLimits limits, CancellationToken cancellationToken)
    {
        using (reader)
        {
            if (reader.Length > limits.MaxResidentFileBytes)
            {
                throw new CorruptIndexException(reader.Name, $"{reader.Length} bytes is above the resident file limit of {limits.MaxResidentFileBytes}");
            }

            var bytes = new byte[reader.Length];
            await reader.ReadExactlyAsync(0, bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
    }

    private static TextSegmentReader Create(string termsFile, string postingsFile, string lengthsFile, byte[] terms, byte[] lengths, IRangeReader postings, ReadOnlySpan<byte> postingsHeader, IndexLimits limits)
    {
        int termsVersion = IndexFile.Validate(termsFile, terms, TextSegmentWriter.TermsMagic, CodecRegistry.NewestVersion(SegmentParts.Terms));
        IndexFile.Validate(lengthsFile, lengths, TextSegmentWriter.LengthsMagic, CodecRegistry.NewestVersion(SegmentParts.Lengths));
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(postingsHeader) != TextSegmentWriter.PostingsMagic)
        {
            throw new CorruptIndexException(postingsFile, "wrong magic: not a postings file");
        }

        uint version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(postingsHeader[4..]);
        if (version == 0 || version > (uint)CodecRegistry.NewestVersion(SegmentParts.Postings))
        {
            throw new UnsupportedFormatException(postingsFile, $"format version {version} is not supported");
        }

        var cursor = new Cursor(termsFile, terms, IndexFile.HeaderLength, terms.Length - IndexFile.FooterLength);
        int maxDoc = cursor.ReadCount(limits.MaxDocsPerSegment, "document count");
        var reader = new TextSegmentReader(postings, maxDoc);
        var lengthViews = ReadLengths(lengthsFile, lengths, maxDoc, limits);
        string? previous = null;
        while (true)
        {
            byte marker = cursor.ReadByte();
            if (marker == 0)
            {
                break;
            }

            if (marker != 1)
            {
                throw new CorruptIndexException(termsFile, "unknown field marker");
            }

            string name = cursor.ReadString(limits.MaxStringBytes);
            if (previous is not null && string.CompareOrdinal(name, previous) <= 0)
            {
                throw new CorruptIndexException(termsFile, "fields are not in increasing order");
            }

            previous = name;
            if (reader._fields.Count >= limits.MaxFields)
            {
                throw new CorruptIndexException(termsFile, "more fields than the limit");
            }

            byte flags = cursor.ReadByte();
            if (flags > 3 || (flags & 2) != 0 && (flags & 1) == 0)
            {
                throw new CorruptIndexException(termsFile, $"field '{name}' has invalid flags");
            }

            long docCount = cursor.ReadVarUInt();
            long sumTtf = cursor.ReadVarUInt();
            long sumDf = cursor.ReadVarUInt();
            long termCount = cursor.ReadVarUInt();
            long totalTermBytes = cursor.ReadVarUInt();
            int maxTermBytes = cursor.ReadCount(limits.MaxTermBytes, "longest term");
            int fstLength = cursor.ReadCount(cursor.Remaining, "automaton length");
            int root = cursor.ReadCount(Math.Max(0, fstLength - 1), "automaton root");
            int fstStart = cursor.Position;
            cursor.Skip(fstLength);
            int tableLength = cursor.ReadCount(cursor.Remaining, "term statistics length");
            int tableStart = cursor.Position;
            cursor.Skip(tableLength);
            long sampleCount = cursor.ReadVarUInt();
            long expectedSamples = (termCount + TermInfoBuilder.GroupSize - 1) / TermInfoBuilder.GroupSize;
            if (docCount > maxDoc || termCount < 1 || termCount > int.MaxValue || sumDf < termCount || sumTtf < sumDf || sampleCount != expectedSamples || sampleCount * 12 > cursor.Remaining || fstLength < 1)
            {
                throw new CorruptIndexException(termsFile, $"field '{name}' has inconsistent counts");
            }

            int samplesStart = cursor.Position;
            cursor.Skip((int)(sampleCount * 12));
            if (!lengthViews.TryGetValue(name, out (int Start, int Width) lv))
            {
                throw new CorruptIndexException(lengthsFile, $"no field lengths for field '{name}'");
            }

            var dictionary = new FieldTermDictionary(termsFile, termsVersion, terms, fstStart, fstLength, root, tableStart, tableLength, samplesStart, sampleCount, termCount, totalTermBytes, maxTermBytes);
            var stats = new FieldStatistics((int)docCount, sumTtf, sumDf);
            PostingsFeatures features = (flags & 2) != 0 ? PostingsFeatures.Offsets : (flags & 1) != 0 ? PostingsFeatures.Positions : PostingsFeatures.Frequencies;
            reader._fields.Add(name, new TextFieldReader(dictionary, postings, maxDoc, features, stats, lengths, lv.Start, lv.Width, lengthsFile));
            reader._names.Add(name);
        }

        if (cursor.Remaining != 0)
        {
            throw new CorruptIndexException(termsFile, "unexpected bytes after the last field");
        }

        return reader;
    }

    private static Dictionary<string, (int Start, int Width)> ReadLengths(string fileName, byte[] bytes, int maxDoc, IndexLimits limits)
    {
        var cursor = new Cursor(fileName, bytes, IndexFile.HeaderLength, bytes.Length - IndexFile.FooterLength);
        int fileMaxDoc = cursor.ReadCount(limits.MaxDocsPerSegment, "document count");
        if (fileMaxDoc != maxDoc)
        {
            throw new CorruptIndexException(fileName, $"document count {fileMaxDoc} differs from the terms file's {maxDoc}");
        }

        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        while (true)
        {
            byte marker = cursor.ReadByte();
            if (marker == 0)
            {
                break;
            }

            if (marker != 1)
            {
                throw new CorruptIndexException(fileName, "unknown field marker");
            }

            string name = cursor.ReadString(limits.MaxStringBytes);
            int width = cursor.ReadByte();
            if (width is < 1 or > 4)
            {
                throw new CorruptIndexException(fileName, $"field '{name}' has an invalid length width");
            }

            long size = (long)maxDoc * width;
            if (size > cursor.Remaining)
            {
                throw new CorruptIndexException(fileName, $"field '{name}' lengths run past the end of the file");
            }

            if (!result.TryAdd(name, (cursor.Position, width)))
            {
                throw new CorruptIndexException(fileName, $"field '{name}' appears twice");
            }

            cursor.Skip((int)size);
        }

        if (cursor.Remaining != 0)
        {
            throw new CorruptIndexException(fileName, "unexpected bytes after the last field");
        }

        return result;
    }

    // A bounds-checked reader over a resident file body.
    private ref struct Cursor(string fileName, byte[] data, int start, int end)
    {
        private readonly string _fileName = fileName;
        private readonly byte[] _data = data;
        private readonly int _end = end;
        private int _pos = start;

        public readonly int Position => _pos;

        public readonly int Remaining => _end - _pos;

        public byte ReadByte()
        {
            if (_pos >= _end)
            {
                throw new CorruptIndexException(_fileName, "unexpected end of data");
            }

            return _data[_pos++];
        }

        public long ReadVarUInt()
        {
            long v = 0;
            for (int shift = 0; shift < 63; shift += 7)
            {
                byte b = ReadByte();
                v |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return v;
                }
            }

            throw new CorruptIndexException(_fileName, "a number is too long");
        }

        public int ReadCount(long max, string what)
        {
            long v = ReadVarUInt();
            if (v < 0 || v > max)
            {
                throw new CorruptIndexException(_fileName, $"{what} {v} is above the limit of {max}");
            }

            return (int)v;
        }

        public void Skip(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new CorruptIndexException(_fileName, "a section runs past the end of the data");
            }

            _pos += count;
        }

        public string ReadString(int maxBytes)
        {
            int n = ReadCount(maxBytes, "string length");
            if (n > Remaining)
            {
                throw new CorruptIndexException(_fileName, "a string runs past the end of the data");
            }

            try
            {
                string s = new UTF8Encoding(false, true).GetString(_data, _pos, n);
                _pos += n;
                return s;
            }
            catch (DecoderFallbackException)
            {
                throw new CorruptIndexException(_fileName, "a name is not valid UTF-8");
            }
        }
    }
}

/// <summary>One field of a segment: dictionary lookups, enumeration, postings and field lengths.</summary>
internal sealed class TextFieldReader : ITextFieldReader
{
    private readonly FieldTermDictionary _dictionary;
    private readonly IRangeReader _postings;
    private readonly int _maxDoc;
    private readonly byte[] _lengths;
    private readonly int _lengthsStart;
    private readonly int _lengthWidth;
    private readonly string _lengthsFile;

    public TextFieldReader(FieldTermDictionary dictionary, IRangeReader postings, int maxDoc, PostingsFeatures features, FieldStatistics statistics, byte[] lengths, int lengthsStart, int lengthWidth, string lengthsFile)
    {
        _dictionary = dictionary;
        _postings = postings;
        _maxDoc = maxDoc;
        IndexedFeatures = features;
        Statistics = statistics;
        _lengths = lengths;
        _lengthsStart = lengthsStart;
        _lengthWidth = lengthWidth;
        _lengthsFile = lengthsFile;
    }

    public FieldStatistics Statistics { get; }

    public PostingsFeatures IndexedFeatures { get; }

    public bool TryGetTerm(ReadOnlySpan<byte> term, out TermInfo info) => _dictionary.TryGetTerm(term, out info);

    public ITermEnumerator GetTerms(ReadOnlySpan<byte> prefix) => _dictionary.GetTerms(prefix);

    public ITermEnumerator Intersect(TermAutomaton automaton, ReadOnlySpan<byte> prefix = default)
    {
        ArgumentNullException.ThrowIfNull(automaton);
        return _dictionary.Enumerate(prefix, automaton);
    }

    public PostingsIterator GetPostings(in TermInfo term, PostingsFeatures features)
    {
        var postings = new TermPostings(_postings, _maxDoc, (IndexedFeatures & PostingsFeatures.Positions) == PostingsFeatures.Positions, (IndexedFeatures & PostingsFeatures.Offsets) == PostingsFeatures.Offsets);
        postings.Reset(term, features);
        return postings;
    }

    public void AddDocuments(in TermInfo term, ulong[] bits, ref PostingsIterator? scratch)
    {
        if (bits.Length < (_maxDoc + 63) >> 6)
        {
            throw new ArgumentException("The bit set is too short for the segment.", nameof(bits));
        }

        var postings = scratch as TermPostings;
        if (postings is null)
        {
            postings = new TermPostings(_postings, _maxDoc, (IndexedFeatures & PostingsFeatures.Positions) == PostingsFeatures.Positions, (IndexedFeatures & PostingsFeatures.Offsets) == PostingsFeatures.Offsets);
            scratch = postings;
        }

        postings.Reset(term, PostingsFeatures.None);
        postings.AddDocumentsTo(bits);
    }

    public PostingsIterator GetPostings(in TermInfo term, PostingsFeatures features, PostingsIterator? reuse)
    {
        if (reuse is not TermPostings postings)
        {
            return GetPostings(term, features);
        }

        postings.Reset(term, features);
        return postings;
    }

    public int GetFieldLength(int docId)
    {
        if ((uint)docId >= (uint)_maxDoc)
        {
            throw new ArgumentOutOfRangeException(nameof(docId));
        }

        ReadOnlySpan<byte> b = _lengths.AsSpan(_lengthsStart + (docId * _lengthWidth), _lengthWidth);
        uint v = 0;
        for (int i = 0; i < b.Length; i++)
        {
            v |= (uint)b[i] << (8 * i);
        }

        if (v > int.MaxValue)
        {
            throw new CorruptIndexException(_lengthsFile, "a field length is out of range");
        }

        return (int)v;
    }
}
