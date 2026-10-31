using System.Buffers.Binary;
using System.Numerics;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// The layout shared by every integer sequence in the column and point files (see docs/columns.md): blocks of 1,024 values, each
/// stored in the smallest of three forms. Random access reads one 24-byte block record and a few data bytes, so no block ever has
/// to be decoded to read one value.
/// </summary>
internal static class IntStreamFormat
{
    public const int BlockShift = 10;
    public const int BlockSize = 1 << BlockShift;
    public const int RecordSize = 24;
    public const int HeaderSize = 12;
    public const int Padding = 8;
    public const byte Constant = 0;
    public const byte Packed = 1;
    public const byte Table = 2;
    public const int MaxTableEntries = 256;

    public static int BitsFor(ulong maxValue) => 64 - BitOperations.LeadingZeroCount(maxValue);

    public static long PackedBytes(int count, int width) => ((long)count * width + 7) >> 3;
}

/// <summary>Writes an integer sequence in the <see cref="IntStreamFormat"/> layout.</summary>
internal static class IntStreamWriter
{
    /// <summary>Appends the stream for <paramref name="values"/>. The choice per block is the smallest of: constant, frame of reference with a common divisor, or a table of up to 256 distinct values.</summary>
    public static void Write(ReadOnlySpan<long> values, ByteBuilder destination)
    {
        int count = values.Length;
        int blocks = (count + IntStreamFormat.BlockSize - 1) >> IntStreamFormat.BlockShift;
        var records = new byte[blocks * IntStreamFormat.RecordSize];
        var data = new ByteBuilder(Math.Max(64, count));
        long[] sorted = new long[IntStreamFormat.BlockSize];
        byte[] pack = new byte[(IntStreamFormat.BlockSize * 8) + IntStreamFormat.Padding];

        for (int b = 0; b < blocks; b++)
        {
            ReadOnlySpan<long> block = values.Slice(b << IntStreamFormat.BlockShift, Math.Min(IntStreamFormat.BlockSize, count - (b << IntStreamFormat.BlockShift)));
            Span<byte> record = records.AsSpan(b * IntStreamFormat.RecordSize, IntStreamFormat.RecordSize);
            long min = block[0];
            long max = block[0];
            foreach (long v in block)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)data.Length);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], min);
            if (min == max)
            {
                record[0] = IntStreamFormat.Constant;
                continue;
            }

            ulong gcd = 0;
            foreach (long v in block)
            {
                gcd = Gcd(gcd, unchecked((ulong)(v - min)));
                if (gcd == 1)
                {
                    break;
                }
            }

            int width = IntStreamFormat.BitsFor(unchecked((ulong)(max - min)) / gcd);
            long packedBytes = IntStreamFormat.PackedBytes(block.Length, width);

            // Table: only worth trying when the range is wide (otherwise packing is already narrow).
            int distinct = width > 8 ? CountDistinct(block, sorted) : int.MaxValue;
            if (distinct <= IntStreamFormat.MaxTableEntries && distinct >= 2)
            {
                int tableWidth = IntStreamFormat.BitsFor((ulong)(distinct - 1));
                long tableBytes = (8L * distinct) + IntStreamFormat.PackedBytes(block.Length, tableWidth);
                if (tableBytes < packedBytes)
                {
                    record[0] = IntStreamFormat.Table;
                    record[1] = (byte)tableWidth;
                    BinaryPrimitives.WriteInt64LittleEndian(record[16..], distinct);
                    for (int i = 0; i < distinct; i++)
                    {
                        data.WriteUInt64((ulong)sorted[i]);
                    }

                    int length = (int)IntStreamFormat.PackedBytes(block.Length, tableWidth);
                    Array.Clear(pack, 0, length + IntStreamFormat.Padding);
                    for (int i = 0; i < block.Length; i++)
                    {
                        PackedBits.Put(pack, i, tableWidth, (ulong)Array.BinarySearch(sorted, 0, distinct, block[i]));
                    }

                    data.Write(pack.AsSpan(0, length));
                    continue;
                }
            }

            record[0] = IntStreamFormat.Packed;
            record[1] = (byte)width;
            BinaryPrimitives.WriteInt64LittleEndian(record[16..], unchecked((long)gcd));
            Array.Clear(pack, 0, (int)packedBytes + IntStreamFormat.Padding);
            for (int i = 0; i < block.Length; i++)
            {
                PackedBits.Put(pack, i, width, unchecked((ulong)(block[i] - min)) / gcd);
            }

            data.Write(pack.AsSpan(0, (int)packedBytes));
        }

        destination.WriteUInt32((uint)count);
        destination.WriteUInt32((uint)blocks);
        destination.WriteUInt32((uint)(data.Length + IntStreamFormat.Padding));
        destination.Write(records);
        destination.Write(data.Span);
        destination.Reserve(IntStreamFormat.Padding);
    }

    private static int CountDistinct(ReadOnlySpan<long> block, long[] sorted)
    {
        block.CopyTo(sorted);
        Array.Sort(sorted, 0, block.Length);
        int n = 1;
        for (int i = 1; i < block.Length; i++)
        {
            if (sorted[i] != sorted[n - 1])
            {
                sorted[n++] = sorted[i];
                if (n > IntStreamFormat.MaxTableEntries)
                {
                    return n;
                }
            }
        }

        return n;
    }

    private static ulong Gcd(ulong a, ulong b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}

/// <summary>Fixed-width bit fields packed little-endian: field i occupies bits [i*width, (i+1)*width) of the buffer.</summary>
internal static class PackedBits
{
    /// <summary>Writes field <paramref name="index"/> into a zeroed buffer that has at least 8 spare bytes after the last field.</summary>
    public static void Put(byte[] buffer, int index, int width, ulong value)
    {
        long bit = (long)index * width;
        int at = (int)(bit >> 3);
        int shift = (int)(bit & 7);
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(at)) | (value << shift);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(at), low);
        if (shift + width > 64)
        {
            buffer[at + 8] |= (byte)(value >> (64 - shift));
        }
    }

    /// <summary>Reads field <paramref name="index"/>. The caller guarantees 8 readable bytes after the first byte of the field (the writers pad), so the 64-bit load and the ninth byte stay inside the data.</summary>
    public static ulong Get(ReadOnlySpan<byte> data, int index, int width)
    {
        long bit = (long)index * width;
        int p = (int)(bit >> 3);
        int shift = (int)(bit & 7);
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data[p..]) >> shift;
        if (shift + width > 64)
        {
            v |= (ulong)data[p + 8] << (64 - shift);
        }

        return width == 64 ? v : v & ((1UL << width) - 1);
    }
}

/// <summary>
/// A validated integer sequence inside a resident file. All structure is checked once in <see cref="Parse"/>; reading afterwards
/// is a few loads and no allocation.
/// </summary>
internal readonly struct IntStream
{
    private readonly byte[] _file;
    private readonly string _name;
    private readonly int _records;
    private readonly int _data;

    private IntStream(string name, byte[] file, int count, int records, int data, int end)
    {
        _name = name;
        _file = file;
        Count = count;
        _records = records;
        _data = data;
        End = end;
    }

    /// <summary>The number of values.</summary>
    public int Count { get; }

    /// <summary>The file offset just past the stream.</summary>
    public int End { get; }

    /// <summary>Reads and checks the stream that starts at <paramref name="offset"/>; it must end by <paramref name="limit"/>.</summary>
    public static IntStream Parse(string name, byte[] file, int offset, int limit)
    {
        if (offset < 0 || limit > file.Length || limit - offset < IntStreamFormat.HeaderSize)
        {
            throw new CorruptIndexException(name, "an integer sequence starts past the end of its section");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4));
        uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 8));
        if (count > int.MaxValue || blocks != ((long)count + IntStreamFormat.BlockSize - 1) >> IntStreamFormat.BlockShift)
        {
            throw new CorruptIndexException(name, $"an integer sequence of {count} values claims {blocks} blocks");
        }

        long recordsBytes = (long)blocks * IntStreamFormat.RecordSize;
        long total = IntStreamFormat.HeaderSize + recordsBytes + dataLength;
        if (dataLength < IntStreamFormat.Padding || total > limit - offset)
        {
            throw new CorruptIndexException(name, "an integer sequence is longer than its section");
        }

        int records = offset + IntStreamFormat.HeaderSize;
        int data = (int)(records + recordsBytes);
        long usable = dataLength - IntStreamFormat.Padding;
        for (int b = 0; b < (int)blocks; b++)
        {
            ReadOnlySpan<byte> r = file.AsSpan(records + (b * IntStreamFormat.RecordSize), IntStreamFormat.RecordSize);
            int m = (int)Math.Min(IntStreamFormat.BlockSize, count - ((long)b << IntStreamFormat.BlockShift));
            uint at = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
            long aux = BinaryPrimitives.ReadInt64LittleEndian(r[16..]);
            long needed;
            switch (r[0])
            {
                case IntStreamFormat.Constant:
                    needed = 0;
                    break;
                case IntStreamFormat.Packed:
                    if (r[1] is < 1 or > 64 || aux == 0)
                    {
                        throw new CorruptIndexException(name, $"block {b} has an invalid packed record");
                    }

                    needed = IntStreamFormat.PackedBytes(m, r[1]);
                    break;
                case IntStreamFormat.Table:
                    if (aux is < 2 or > IntStreamFormat.MaxTableEntries || r[1] != IntStreamFormat.BitsFor((ulong)(aux - 1)))
                    {
                        throw new CorruptIndexException(name, $"block {b} has an invalid table record");
                    }

                    needed = (8 * aux) + IntStreamFormat.PackedBytes(m, r[1]);
                    break;
                default:
                    throw new CorruptIndexException(name, $"block {b} has unknown encoding {r[0]}");
            }

            if (at + needed > usable)
            {
                throw new CorruptIndexException(name, $"block {b} reads past the end of its data");
            }
        }

        return new IntStream(name, file, (int)count, records, data, (int)(offset + total));
    }

    /// <summary>Reads value <paramref name="index"/>; <paramref name="index"/> must be below <see cref="Count"/>.</summary>
    public long Get(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int block = index >> IntStreamFormat.BlockShift;
        ReadOnlySpan<byte> r = _file.AsSpan(_records + (block * IntStreamFormat.RecordSize), IntStreamFormat.RecordSize);
        long b = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
        int j = index & (IntStreamFormat.BlockSize - 1);
        switch (r[0])
        {
            case IntStreamFormat.Constant:
                return b;
            case IntStreamFormat.Packed:
                {
                    int at = _data + (int)BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
                    ulong gcd = BinaryPrimitives.ReadUInt64LittleEndian(r[16..]);
                    return unchecked((long)((ulong)b + (gcd * Unpack(at, j, r[1]))));
                }

            default:
                {
                    int at = _data + (int)BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
                    int n = (int)BinaryPrimitives.ReadInt64LittleEndian(r[16..]);
                    ulong slot = Unpack(at + (8 * n), j, r[1]);
                    if (slot >= (ulong)n)
                    {
                        throw new CorruptIndexException(_name, "a table index points past its table");
                    }

                    return BinaryPrimitives.ReadInt64LittleEndian(_file.AsSpan(at + ((int)slot * 8)));
                }
        }
    }

    /// <summary>Decodes <c>destination.Length</c> values starting at <paramref name="start"/> (sequential scan, one block record per 1,024 values).</summary>
    public void Decode(int start, Span<long> destination)
    {
        if (start < 0 || destination.Length > Count - start)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        int done = 0;
        while (done < destination.Length)
        {
            int index = start + done;
            int block = index >> IntStreamFormat.BlockShift;
            int j = index & (IntStreamFormat.BlockSize - 1);
            int run = Math.Min(IntStreamFormat.BlockSize - j, destination.Length - done);
            ReadOnlySpan<byte> r = _file.AsSpan(_records + (block * IntStreamFormat.RecordSize), IntStreamFormat.RecordSize);
            long b = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
            Span<long> d = destination.Slice(done, run);
            switch (r[0])
            {
                case IntStreamFormat.Constant:
                    d.Fill(b);
                    break;
                case IntStreamFormat.Packed:
                    {
                        int at = _data + (int)BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
                        ulong gcd = BinaryPrimitives.ReadUInt64LittleEndian(r[16..]);
                        int w = r[1];
                        for (int i = 0; i < d.Length; i++)
                        {
                            d[i] = unchecked((long)((ulong)b + (gcd * Unpack(at, j + i, w))));
                        }

                        break;
                    }

                default:
                    {
                        int at = _data + (int)BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
                        int n = (int)BinaryPrimitives.ReadInt64LittleEndian(r[16..]);
                        int w = r[1];
                        for (int i = 0; i < d.Length; i++)
                        {
                            ulong slot = Unpack(at + (8 * n), j + i, w);
                            if (slot >= (ulong)n)
                            {
                                throw new CorruptIndexException(_name, "a table index points past its table");
                            }

                            d[i] = BinaryPrimitives.ReadInt64LittleEndian(_file.AsSpan(at + ((int)slot * 8)));
                        }

                        break;
                    }
            }

            done += run;
        }
    }

    private ulong Unpack(int at, int index, int width) => PackedBits.Get(_file.AsSpan(at), index, width);
}
