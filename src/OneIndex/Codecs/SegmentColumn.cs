using System.Buffers.Binary;
using System.Numerics;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// One field's per-document values in one segment, read from the segment's <c>columns</c> file (docs/columns.md). Random access by
/// document id and index within the document, sequential decoding of runs, and for keyword fields the sorted per-segment dictionary
/// with ordinal and term lookup. The file is resident; every access is a few loads and allocates nothing. Instances are immutable
/// and safe for concurrent use.
/// </summary>
public sealed class SegmentColumn : IColumnReader
{
    private readonly string _fileName;
    private readonly byte[] _bytes;
    private readonly ColumnLayout _layout;
    private readonly int _maxDoc;
    private readonly int _wordsAt;
    private readonly uint[] _rank = [];
    private readonly IntStream _offsets;
    private readonly IntStream _values;
    private readonly IntStream _values2;
    private readonly int _termCount;
    private readonly int _termOffsetsAt;
    private readonly int _termBytesAt;
    private readonly bool _isInteger;

    internal SegmentColumn(string fileName, byte[] bytes, int maxDoc, FieldType type, ColumnLayout layout, long docsWithValue, long valueCount, long min, long max, int start, int end)
    {
        _fileName = fileName;
        _bytes = bytes;
        _maxDoc = maxDoc;
        _layout = layout;
        Type = type;
        _isInteger = type is FieldType.Integral or FieldType.Date or FieldType.Bool;
        MinRaw = min;
        MaxRaw = max;
        DocsWithValue = (int)docsWithValue;
        int at = start;
        if (type == FieldType.Keyword)
        {
            if (end - at < 8)
            {
                throw new CorruptIndexException(fileName, "a keyword dictionary header runs past its section");
            }

            uint terms = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            uint termBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            long tableBytes = ((long)terms + 1) * 4;
            if (terms > int.MaxValue / 8 || 8 + tableBytes + termBytes > end - at)
            {
                throw new CorruptIndexException(fileName, "a keyword dictionary is longer than its section");
            }

            _termCount = (int)terms;
            _termOffsetsAt = at + 8;
            _termBytesAt = (int)(_termOffsetsAt + tableBytes);
            ValidateDictionary(termBytes);
            at = (int)(_termBytesAt + termBytes);
        }

        switch (layout)
        {
            case ColumnLayout.Optional:
                {
                    long wordsBytes = (((long)maxDoc + 63) >> 6) * 8;
                    if (wordsBytes > end - at)
                    {
                        throw new CorruptIndexException(fileName, "a presence bitmap is longer than its section");
                    }

                    _wordsAt = at;
                    _rank = new uint[((maxDoc + 63) >> 6) + 1];
                    long total = 0;
                    for (int w = 0; w < (maxDoc + 63) >> 6; w++)
                    {
                        _rank[w] = (uint)total;
                        total += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + (w * 8))));
                    }

                    _rank[^1] = (uint)total;
                    if (total != docsWithValue || total != valueCount)
                    {
                        throw new CorruptIndexException(fileName, "a presence bitmap disagrees with the value count");
                    }

                    at += (int)wordsBytes;
                    break;
                }

            case ColumnLayout.Multi:
                {
                    _offsets = IntStream.Parse(fileName, bytes, at, end);
                    at = _offsets.End;
                    ValidateOffsets(valueCount);
                    break;
                }

            default:
                if (docsWithValue != maxDoc || valueCount != maxDoc)
                {
                    throw new CorruptIndexException(fileName, "a dense column does not hold one value per document");
                }

                break;
        }

        _values = IntStream.Parse(fileName, bytes, at, end);
        at = _values.End;
        if (type == FieldType.GeoPoint)
        {
            _values2 = IntStream.Parse(fileName, bytes, at, end);
            at = _values2.End;
            if (_values2.Count != _values.Count)
            {
                throw new CorruptIndexException(fileName, "the two coordinate sequences of a geo column differ in length");
            }
        }

        if (at != end || _values.Count != valueCount)
        {
            throw new CorruptIndexException(fileName, "a column section does not match its directory entry");
        }
    }

    /// <summary>The field type of the values.</summary>
    public FieldType Type { get; }

    /// <summary>The number of documents that have at least one value.</summary>
    public int DocsWithValue { get; }

    /// <summary>The number of values across all documents.</summary>
    public long TotalValues => _values.Count;

    /// <summary>Whether a document can hold more than one value.</summary>
    public bool IsMultiValued => _layout == ColumnLayout.Multi;

    /// <summary>The smallest raw value of the column (the ordered integer form; see <see cref="ReadValues"/>); 0 when empty.</summary>
    public long MinRaw { get; }

    /// <summary>The largest raw value of the column; 0 when empty.</summary>
    public long MaxRaw { get; }

    /// <summary>The number of distinct keywords of a keyword column; 0 for other types.</summary>
    public int TermCount => _termCount;

    /// <inheritdoc/>
    public bool HasValue(int docId)
    {
        Check(docId);
        return _layout switch
        {
            ColumnLayout.Dense => true,
            ColumnLayout.Optional => ((BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(_wordsAt + ((docId >> 6) * 8))) >> (docId & 63)) & 1) != 0,
            _ => _offsets.Get(docId + 1) > _offsets.Get(docId),
        };
    }

    /// <inheritdoc/>
    public int ValueCount(int docId)
    {
        Check(docId);
        Range(docId, out int start, out int end);
        return end - start;
    }

    /// <summary>The index in the column's value sequence of the document's first value, for use with <see cref="ReadValues"/>.</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <returns>The index; for a document without values, the index where its values would start.</returns>
    public int FirstValueIndex(int docId)
    {
        Check(docId);
        Range(docId, out int start, out _);
        return start;
    }

    /// <inheritdoc/>
    public long GetInt64(int docId, int index = 0)
    {
        if (!_isInteger)
        {
            throw new InvalidOperationException($"Column '{_fileName}' holds {Type} values, not integers.");
        }

        return _values.Get(ValueIndex(docId, index));
    }

    /// <inheritdoc/>
    public double GetFloat64(int docId, int index = 0)
    {
        if (Type != FieldType.Fractional)
        {
            throw new InvalidOperationException($"Column '{_fileName}' holds {Type} values, not floating-point numbers.");
        }

        return SortableBits.Decode(_values.Get(ValueIndex(docId, index)));
    }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> GetKeyword(int docId, int index = 0)
    {
        if (Type != FieldType.Keyword)
        {
            throw new InvalidOperationException($"Column '{_fileName}' holds {Type} values, not keywords.");
        }

        return GetTerm(OrdinalAt(ValueIndex(docId, index)));
    }

    /// <inheritdoc/>
    public (double Latitude, double Longitude) GetGeoPoint(int docId, int index = 0)
    {
        if (Type != FieldType.GeoPoint)
        {
            throw new InvalidOperationException($"Column '{_fileName}' holds {Type} values, not geo points.");
        }

        int v = ValueIndex(docId, index);
        return (SortableBits.Decode(_values.Get(v)), SortableBits.Decode(_values2.Get(v)));
    }

    /// <summary>The ordinal of a document's keyword in the segment's sorted dictionary (the facet and sort key).</summary>
    /// <param name="docId">The segment-local document id.</param>
    /// <param name="index">Which of the document's values.</param>
    /// <returns>The ordinal, in <c>[0, TermCount)</c>.</returns>
    public int GetOrdinal(int docId, int index = 0)
    {
        if (Type != FieldType.Keyword)
        {
            throw new InvalidOperationException($"Column '{_fileName}' holds {Type} values, not keywords.");
        }

        return OrdinalAt(ValueIndex(docId, index));
    }

    /// <summary>A dictionary term by ordinal, valid for the life of the reader.</summary>
    /// <param name="ordinal">The ordinal, in <c>[0, TermCount)</c>.</param>
    /// <returns>The UTF-8 bytes.</returns>
    public ReadOnlySpan<byte> GetTerm(int ordinal)
    {
        if ((uint)ordinal >= (uint)_termCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        int from = (int)BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(_termOffsetsAt + (ordinal * 4)));
        int to = (int)BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(_termOffsetsAt + (ordinal * 4) + 4));
        return _bytes.AsSpan(_termBytesAt + from, to - from);
    }

    /// <summary>Finds a keyword in the dictionary by binary search.</summary>
    /// <param name="term">The UTF-8 bytes.</param>
    /// <returns>The ordinal when found; otherwise the bitwise complement of the ordinal of the first larger term (so it is negative).</returns>
    public int FindOrdinal(ReadOnlySpan<byte> term)
    {
        int lo = 0;
        int hi = _termCount - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int c = GetTerm(mid).SequenceCompareTo(term);
            if (c == 0)
            {
                return mid;
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }

    /// <summary>
    /// Decodes a run of the column's raw values: the stored integer for integers, dates and bools, the <see cref="SortableBits"/> form
    /// for doubles and the latitude of geo points, the ordinal for keywords. For a single-valued dense column the value index is the
    /// document id. Decodes in bulk and allocates nothing.
    /// </summary>
    /// <param name="firstValue">The index of the first value, from <see cref="FirstValueIndex"/> or a document id of a dense column.</param>
    /// <param name="destination">Receives <c>destination.Length</c> values.</param>
    public void ReadValues(int firstValue, Span<long> destination) => _values.Decode(firstValue, destination);

    /// <summary>The second coordinate (longitude, <see cref="SortableBits"/> form) of a geo column's values, decoded in bulk.</summary>
    /// <param name="firstValue">The index of the first value.</param>
    /// <param name="destination">Receives the values.</param>
    public void ReadSecondValues(int firstValue, Span<long> destination)
    {
        if (Type != FieldType.GeoPoint)
        {
            throw new InvalidOperationException($"Column '{_fileName}' has no second coordinate.");
        }

        _values2.Decode(firstValue, destination);
    }

    internal long RawValue(int docId, int index) => _values.Get(ValueIndex(docId, index));

    internal long RawSecond(int docId, int index) => _values2.Get(ValueIndex(docId, index));

    internal int OrdinalAt(int valueIndex)
    {
        long ord = _values.Get(valueIndex);
        if ((ulong)ord >= (ulong)_termCount)
        {
            throw new CorruptIndexException(_fileName, "a keyword ordinal points past the dictionary");
        }

        return (int)ord;
    }

    private void Check(int docId)
    {
        if ((uint)docId >= (uint)_maxDoc)
        {
            throw new ArgumentOutOfRangeException(nameof(docId), docId, "The document id is outside the segment.");
        }
    }

    private int ValueIndex(int docId, int index)
    {
        Check(docId);
        Range(docId, out int start, out int end);
        if ((uint)index >= (uint)(end - start))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The document has no such value.");
        }

        return start + index;
    }

    private void Range(int docId, out int start, out int end)
    {
        switch (_layout)
        {
            case ColumnLayout.Dense:
                start = docId;
                end = docId + 1;
                return;
            case ColumnLayout.Optional:
                {
                    int w = docId >> 6;
                    ulong word = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(_wordsAt + (w * 8)));
                    int bit = docId & 63;
                    start = (int)_rank[w] + BitOperations.PopCount(word & ((1UL << bit) - 1));
                    end = start + (int)((word >> bit) & 1);
                    return;
                }

            default:
                start = (int)_offsets.Get(docId);
                end = (int)_offsets.Get(docId + 1);
                return;
        }
    }

    private void ValidateDictionary(uint termBytes)
    {
        ReadOnlySpan<byte> table = _bytes.AsSpan(_termOffsetsAt, (_termCount + 1) * 4);
        if (BinaryPrimitives.ReadUInt32LittleEndian(table) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(table[(_termCount * 4)..]) != termBytes)
        {
            throw new CorruptIndexException(_fileName, "a keyword dictionary's offsets do not span its bytes");
        }

        uint previous = 0;
        for (int i = 1; i <= _termCount; i++)
        {
            uint at = BinaryPrimitives.ReadUInt32LittleEndian(table[(i * 4)..]);
            if (at < previous)
            {
                throw new CorruptIndexException(_fileName, "a keyword dictionary's offsets decrease");
            }

            previous = at;
        }

        for (int i = 1; i < _termCount; i++)
        {
            if (GetTerm(i - 1).SequenceCompareTo(GetTerm(i)) >= 0)
            {
                throw new CorruptIndexException(_fileName, "the keywords of a dictionary are not in strictly increasing order");
            }
        }
    }

    // A multi-valued column's offsets must start at 0, never decrease and end at the value count; then every run is in range.
    private void ValidateOffsets(long valueCount)
    {
        if (_offsets.Count != _maxDoc + 1)
        {
            throw new CorruptIndexException(_fileName, "the document offsets of a multi-valued column do not cover the segment");
        }

        Span<long> buffer = stackalloc long[IntStreamFormat.BlockSize]; // one block of offsets, fixed size
        long previous = 0;
        for (int at = 0; at < _offsets.Count; at += buffer.Length)
        {
            Span<long> run = buffer[..Math.Min(buffer.Length, _offsets.Count - at)];
            _offsets.Decode(at, run);
            foreach (long o in run)
            {
                if (o < previous)
                {
                    throw new CorruptIndexException(_fileName, "the document offsets of a multi-valued column decrease");
                }

                previous = o;
            }
        }

        if (_offsets.Get(0) != 0 || previous != valueCount)
        {
            throw new CorruptIndexException(_fileName, "the document offsets of a multi-valued column do not span its values");
        }
    }
}
