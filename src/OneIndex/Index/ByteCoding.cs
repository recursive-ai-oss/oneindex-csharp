using System.Buffers.Binary;
using System.Text;

namespace OneIndex.Index;

/// <summary>Thrown inside the format readers when input is malformed; converted to the public typed exception at the boundary.</summary>
internal sealed class MalformedInputException(string message) : Exception(message);

/// <summary>A growable byte buffer with the primitive encodings of the commit and schema formats (little-endian, LEB128 varints).</summary>
internal sealed class ByteSink
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private byte[] _buffer = new byte[256];
    private int _length;

    public int Length => _length;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    public void WriteVarUInt(ulong value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteVarUInt((ulong)value.Length);
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    public void WriteString(string value)
    {
        int n = Utf8.GetByteCount(value);
        WriteVarUInt((ulong)n);
        Ensure(n);
        _length += Utf8.GetBytes(value, _buffer.AsSpan(_length));
    }

    private void Ensure(int extra)
    {
        if (_buffer.Length - _length < extra)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + extra));
        }
    }
}

/// <summary>A bounds-checked cursor over bytes, in the style of <see cref="System.Text.Json.Utf8JsonReader"/>: every read validates before it advances.</summary>
internal ref struct ByteSource(ReadOnlySpan<byte> data)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte()
    {
        if (_position >= _data.Length)
        {
            throw new MalformedInputException("unexpected end of data");
        }

        return _data[_position++];
    }

    public uint ReadUInt32()
    {
        if (Remaining < 4)
        {
            throw new MalformedInputException("unexpected end of data");
        }

        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += 4;
        return v;
    }

    public ulong ReadUInt64()
    {
        if (Remaining < 8)
        {
            throw new MalformedInputException("unexpected end of data");
        }

        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
        _position += 8;
        return v;
    }

    public ulong ReadVarUInt()
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            byte b = ReadByte();
            if (shift == 63 && b > 1)
            {
                throw new MalformedInputException("varint overflows 64 bits");
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                // The writer never emits a trailing zero group, so accepting one would make two byte strings mean the same value.
                if (b == 0 && shift > 0)
                {
                    throw new MalformedInputException("varint is not in its shortest form");
                }

                return result;
            }
        }

        throw new MalformedInputException("varint is too long");
    }

    /// <summary>Reads a varint that is a count or length: it must not exceed <paramref name="max"/> (and so cannot drive an allocation past the limit).</summary>
    public int ReadCount(long max, string what)
    {
        ulong v = ReadVarUInt();
        if (v > (ulong)max)
        {
            throw new MalformedInputException($"{what} {v} exceeds the limit of {max}");
        }

        return (int)v;
    }

    public ReadOnlySpan<byte> ReadBytes(int maxLength, string what)
    {
        int n = ReadCount(maxLength, what);
        if (n > Remaining)
        {
            throw new MalformedInputException($"{what} of {n} bytes runs past the end of the data");
        }

        ReadOnlySpan<byte> v = _data.Slice(_position, n);
        _position += n;
        return v;
    }

    public string ReadString(int maxBytes, string what)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(maxBytes, what);
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MalformedInputException($"{what} is not valid UTF-8");
        }
    }
}
