using System.Buffers.Binary;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>How a column maps documents to values (docs/columns.md).</summary>
internal enum ColumnLayout : byte
{
    /// <summary>Every document has exactly one value: the value index is the document id.</summary>
    Dense = 0,

    /// <summary>Documents have at most one value: a presence bitmap with ranks gives the value index.</summary>
    Optional = 1,

    /// <summary>Documents have any number of values: an offsets sequence gives each document's run.</summary>
    Multi = 2,
}

/// <summary>Collects one field's values for a segment in document order and writes the column section.</summary>
internal sealed class ColumnBuilder
{
    private readonly int[] _counts;
    private long[] _values = new long[64];
    private long[] _values2 = [];
    private int _count;

    public ColumnBuilder(string name, FieldType type, int maxDoc, byte[][]? dictionary)
    {
        Name = name;
        Type = type;
        MaxDoc = maxDoc;
        Dictionary = dictionary;
        _counts = new int[maxDoc];
        if (type == FieldType.GeoPoint)
        {
            _values2 = new long[64];
        }
    }

    public string Name { get; }

    public FieldType Type { get; }

    public int MaxDoc { get; }

    /// <summary>The sorted distinct keywords of a keyword column; values are ordinals into it.</summary>
    public byte[][]? Dictionary { get; }

    public int ValueCount => _count;

    /// <summary>Adds the next value of <paramref name="doc"/>; documents must not decrease.</summary>
    public void Add(int doc, long value, long second = 0)
    {
        if (_count == _values.Length)
        {
            Array.Resize(ref _values, _values.Length * 2);
            if (_values2.Length != 0)
            {
                Array.Resize(ref _values2, _values.Length);
            }
        }

        _values[_count] = value;
        if (_values2.Length != 0)
        {
            _values2[_count] = second;
        }

        _count++;
        _counts[doc]++;
    }

    /// <summary>Picks the layout from the counts and records <see cref="DocsWithValue"/>.</summary>
    public ColumnLayout ChooseLayout()
    {
        int with = 0;
        int max = 0;
        foreach (int c in _counts)
        {
            if (c > 0)
            {
                with++;
            }

            max = Math.Max(max, c);
        }

        DocsWithValue = with;
        return max > 1 ? ColumnLayout.Multi : with == MaxDoc ? ColumnLayout.Dense : ColumnLayout.Optional;
    }

    public int DocsWithValue { get; private set; }

    public (long Min, long Max) Range()
    {
        if (_count == 0)
        {
            return (0, 0);
        }

        long min = _values[0];
        long max = _values[0];
        for (int i = 1; i < _count; i++)
        {
            min = Math.Min(min, _values[i]);
            max = Math.Max(max, _values[i]);
        }

        return (min, max);
    }

    /// <summary>Appends the column's section (dictionary, document map, values) to <paramref name="destination"/>.</summary>
    public void WriteSection(ColumnLayout layout, ByteBuilder destination)
    {
        if (Dictionary is { } terms)
        {
            long bytes = 0;
            foreach (byte[] t in terms)
            {
                bytes += t.Length;
            }

            if (bytes > int.MaxValue / 2)
            {
                throw new InvalidOperationException($"The keywords of column '{Name}' total {bytes} bytes, past the limit of {int.MaxValue / 2}.");
            }

            destination.WriteUInt32((uint)terms.Length);
            destination.WriteUInt32((uint)bytes);
            uint at = 0;
            foreach (byte[] t in terms)
            {
                destination.WriteUInt32(at);
                at += (uint)t.Length;
            }

            destination.WriteUInt32(at);
            foreach (byte[] t in terms)
            {
                destination.Write(t);
            }
        }

        switch (layout)
        {
            case ColumnLayout.Optional:
                {
                    var words = new ulong[(MaxDoc + 63) >> 6];
                    for (int d = 0; d < MaxDoc; d++)
                    {
                        if (_counts[d] > 0)
                        {
                            words[d >> 6] |= 1UL << (d & 63);
                        }
                    }

                    foreach (ulong w in words)
                    {
                        destination.WriteUInt64(w);
                    }

                    break;
                }

            case ColumnLayout.Multi:
                {
                    var offsets = new long[MaxDoc + 1];
                    for (int d = 0; d < MaxDoc; d++)
                    {
                        offsets[d + 1] = offsets[d] + _counts[d];
                    }

                    IntStreamWriter.Write(offsets, destination);
                    break;
                }
        }

        IntStreamWriter.Write(_values.AsSpan(0, _count), destination);
        if (_values2.Length != 0)
        {
            IntStreamWriter.Write(_values2.AsSpan(0, _count), destination);
        }
    }
}

/// <summary>Field-type conventions of column values: what raw integer a value is stored as.</summary>
internal static class ColumnValues
{
    public static bool IsColumnField(FieldDefinition f)
        => (f.Column || f.Facet) && f.Type is FieldType.Keyword or FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool or FieldType.GeoPoint;

    /// <summary>The raw integer of a numeric, date or bool value. Doubles keep every bit (NaN payloads, the sign of zero) through the order-preserving <see cref="SortableBits"/> mapping.</summary>
    public static long Raw(FieldType type, FieldValue value) => type switch
    {
        FieldType.Fractional => SortableBits.Encode(value.AsFloat64()),
        FieldType.Integral or FieldType.Date => value.AsInt64(),
        FieldType.Bool => value.AsBool() ? 1 : 0,
        _ => throw new ArgumentException($"{type} has no numeric column value.", nameof(type)),
    };

    public static int CompareBytes(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    /// <summary>Sorted distinct UTF-8 keywords.</summary>
    public static byte[][] SortedTerms(IEnumerable<string> distinct)
    {
        var terms = new List<byte[]>();
        foreach (string s in distinct)
        {
            terms.Add(System.Text.Encoding.UTF8.GetBytes(s));
        }

        terms.Sort(CompareBytes);
        return [.. terms];
    }
}

/// <summary>Reads the little-endian words of a resident file without allocating.</summary>
internal static class ResidentFile
{
    public static byte[] Read(IRangeReader reader, IndexLimits limits, CancellationToken cancellationToken)
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

    public static int ReadCount(string name, ref ByteCursor cursor, long max, string what)
    {
        ulong v = cursor.ReadVarUInt();
        if (v > (ulong)max)
        {
            throw new CorruptIndexException(name, $"{what} {v} exceeds {max}");
        }

        return (int)v;
    }

    public static uint U32(byte[] file, int at) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
}

/// <summary>A bounds-checked cursor over a region of a resident file that raises <see cref="CorruptIndexException"/>.</summary>
internal struct ByteCursor
{
    private readonly byte[] _file;
    private readonly string _name;
    private readonly int _end;

    public ByteCursor(string name, byte[] file, int position, int end)
    {
        _name = name;
        _file = file;
        Position = position;
        _end = end;
    }

    public int Position { get; private set; }

    public readonly int Remaining => _end - Position;

    public ulong ReadVarUInt()
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            byte b = ReadByte();
            if (shift == 63 && b > 1)
            {
                throw new CorruptIndexException(_name, "varint overflows 64 bits");
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new CorruptIndexException(_name, "varint is too long");
    }

    public byte ReadByte()
    {
        if (Position >= _end)
        {
            throw new CorruptIndexException(_name, "unexpected end of a table");
        }

        return _file[Position++];
    }

    public long ReadInt64()
    {
        if (Remaining < 8)
        {
            throw new CorruptIndexException(_name, "unexpected end of a table");
        }

        long v = BinaryPrimitives.ReadInt64LittleEndian(_file.AsSpan(Position));
        Position += 8;
        return v;
    }

    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        if (length < 0 || length > Remaining)
        {
            throw new CorruptIndexException(_name, "a length runs past the end of a table");
        }

        ReadOnlySpan<byte> v = _file.AsSpan(Position, length);
        Position += length;
        return v;
    }
}
