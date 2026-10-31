using System.Buffers.Binary;
using System.Numerics;

namespace OneIndex.Codecs;

/// <summary>A growable byte buffer for building codec output: raw appends, LEB128 varints and little-endian integers.</summary>
internal sealed class ByteBuilder
{
    private byte[] _buffer;
    private int _length;

    public ByteBuilder(int capacity = 256) => _buffer = new byte[Math.Max(16, capacity)];

    public int Length => _length;

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    public void Clear() => _length = 0;

    public void WriteByte(byte value)
    {
        if (_length == _buffer.Length)
        {
            Grow(1);
        }

        _buffer[_length++] = value;
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        if (_buffer.Length - _length < value.Length)
        {
            Grow(value.Length);
        }

        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Reserves <paramref name="count"/> zeroed bytes and returns them for the caller to fill.</summary>
    public Span<byte> Reserve(int count)
    {
        if (_buffer.Length - _length < count)
        {
            Grow(count);
        }

        Span<byte> span = _buffer.AsSpan(_length, count);
        span.Clear();
        _length += count;
        return span;
    }

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

    public void WriteVarUInt(ulong value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    private void Grow(int extra) => Array.Resize(ref _buffer, (int)Math.Min(Array.MaxLength, Math.Max((long)_buffer.Length * 2, (long)_length + extra)));
}

/// <summary>
/// Fixed-width bit packing of small unsigned integers, least significant bit first, the way frame-of-reference postings
/// compression stores a block of deltas with one width for the whole block (Zukowski et al. 2006; Lemire and Boytsov 2015, §2).
/// </summary>
internal static class BitPacking
{
    public static int BitsRequired(uint max) => 32 - BitOperations.LeadingZeroCount(max);

    public static int PackedLength(int count, int width) => (int)(((long)count * width + 7) >> 3);

    /// <summary>Packs <paramref name="values"/> into <paramref name="destination"/>, which must be zeroed and at least <see cref="PackedLength"/> long.</summary>
    public static void Pack(ReadOnlySpan<uint> values, int width, Span<byte> destination)
    {
        if (width == 0)
        {
            return;
        }

        ulong acc = 0;
        int bits = 0;
        int pos = 0;
        foreach (uint v in values)
        {
            acc |= (ulong)v << bits;
            bits += width;
            while (bits >= 8)
            {
                destination[pos++] = (byte)acc;
                acc >>= 8;
                bits -= 8;
            }
        }

        if (bits > 0)
        {
            destination[pos] = (byte)acc;
        }
    }

    /// <summary>Unpacks <c>destination.Length</c> values; <paramref name="source"/> must hold at least <see cref="PackedLength"/> bytes.</summary>
    public static void Unpack(ReadOnlySpan<byte> source, int width, Span<uint> destination)
    {
        if (width == 0)
        {
            destination.Clear();
            return;
        }

        ulong mask = (1UL << width) - 1;
        ulong acc = 0;
        int bits = 0;
        int pos = 0;
        for (int i = 0; i < destination.Length; i++)
        {
            while (bits < width)
            {
                acc |= (ulong)source[pos++] << bits;
                bits += 8;
            }

            destination[i] = (uint)(acc & mask);
            acc >>= width;
            bits -= width;
        }
    }
}
