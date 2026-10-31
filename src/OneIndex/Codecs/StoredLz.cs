using System.Buffers.Binary;

namespace OneIndex.Codecs;

/// <summary>
/// A small byte-oriented LZ77 compressor for the blocks of stored source (docs/stored-source.md). It is written here, not taken from
/// the BCL, because stored files must be byte-for-byte the same on every platform and runtime (the BCL's deflate output depends on
/// the zlib build underneath), must decode in the browser without a native library, and must be fast to decode, since a document
/// fetch decodes a whole block. Format: a sequence of tokens, each a byte whose high nibble is a literal run length and low nibble a
/// match length minus 4 (15 in either nibble: add the following bytes, each 255 meaning "continue"), then the literals, then a
/// 16-bit little-endian match offset (1 to 65,535 bytes back) and the extension of the match length. The last token has only
/// literals. Ziv and Lempel, "A universal algorithm for sequential data compression", IEEE Trans. Information Theory, 1977.
/// </summary>
internal sealed class LzCompressor
{
    private const int HashBits = 15;
    private const int Window = 65_536;
    private const int MinMatch = 4;
    private const int MaxChain = 16;
    private readonly int[] _head = new int[1 << HashBits];
    private readonly int[] _prev = new int[Window];

    public static int MaxCompressedLength(int length) => length + (length / 255) + 16;

    /// <summary>Compresses <paramref name="source"/> and appends the result to <paramref name="destination"/>.</summary>
    public void Compress(ReadOnlySpan<byte> source, ByteBuilder destination)
    {
        Array.Clear(_head);
        int n = source.Length;
        int anchor = 0;
        int i = 0;
        int limit = n - 8; // the last bytes are always literals, so a hash read never runs past the input
        while (i < limit)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(source[i..]);
            int h = (int)((word * 2_654_435_761u) >> (32 - HashBits));
            int bestLength = 0;
            int bestOffset = 0;
            int candidate = _head[h] - 1;
            for (int depth = 0; candidate >= 0 && i - candidate < Window && depth < MaxChain; depth++)
            {
                int length = CommonLength(source, candidate, i);
                if (length > bestLength)
                {
                    bestLength = length;
                    bestOffset = i - candidate;
                }

                int next = _prev[candidate & (Window - 1)] - 1;
                if (next >= candidate)
                {
                    break;
                }

                candidate = next;
            }

            _prev[i & (Window - 1)] = _head[h];
            _head[h] = i + 1;
            if (bestLength < MinMatch)
            {
                i++;
                continue;
            }

            WriteSequence(destination, source[anchor..i], bestLength, bestOffset);
            int end = i + bestLength;
            for (int k = i + 1; k < end && k < limit; k++)
            {
                uint w = BinaryPrimitives.ReadUInt32LittleEndian(source[k..]);
                int hk = (int)((w * 2_654_435_761u) >> (32 - HashBits));
                _prev[k & (Window - 1)] = _head[hk];
                _head[hk] = k + 1;
            }

            i = end;
            anchor = i;
        }

        WriteSequence(destination, source[anchor..], 0, 0);
    }

    private static int CommonLength(ReadOnlySpan<byte> s, int a, int b)
    {
        int max = s.Length - b;
        int len = 0;
        while (len < max && s[a + len] == s[b + len])
        {
            len++;
        }

        return len;
    }

    private static void WriteSequence(ByteBuilder dst, ReadOnlySpan<byte> literals, int matchLength, int offset)
    {
        int lit = literals.Length;
        int ml = matchLength == 0 ? 0 : matchLength - MinMatch;
        dst.WriteByte((byte)((Math.Min(lit, 15) << 4) | Math.Min(ml, 15)));
        if (lit >= 15)
        {
            WriteExtension(dst, lit - 15);
        }

        dst.Write(literals);
        if (matchLength == 0)
        {
            return;
        }

        dst.WriteByte((byte)offset);
        dst.WriteByte((byte)(offset >> 8));
        if (ml >= 15)
        {
            WriteExtension(dst, ml - 15);
        }
    }

    private static void WriteExtension(ByteBuilder dst, int value)
    {
        while (value >= 255)
        {
            dst.WriteByte(255);
            value -= 255;
        }

        dst.WriteByte((byte)value);
    }
}

/// <summary>Decodes <see cref="LzCompressor"/> output, refusing anything that is not exactly a valid stream for the expected size.</summary>
internal static class LzDecompressor
{
    /// <summary>Decodes into <paramref name="destination"/>, which must have exactly the original length.</summary>
    /// <returns><see langword="false"/> if the input is malformed, over- or under-runs, or does not fill the destination exactly.</returns>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int ip = 0;
        int op = 0;
        while (true)
        {
            if (ip >= source.Length)
            {
                return false;
            }

            int token = source[ip++];
            int literals = token >> 4;
            if (literals == 15)
            {
                int b;
                do
                {
                    if (ip >= source.Length)
                    {
                        return false;
                    }

                    b = source[ip++];
                    literals += b;
                    if (literals > destination.Length)
                    {
                        return false;
                    }
                }
                while (b == 255);
            }

            if (literals > source.Length - ip || literals > destination.Length - op)
            {
                return false;
            }

            source.Slice(ip, literals).CopyTo(destination[op..]);
            ip += literals;
            op += literals;
            if (ip == source.Length)
            {
                return op == destination.Length;
            }

            if (source.Length - ip < 2)
            {
                return false;
            }

            int offset = source[ip] | (source[ip + 1] << 8);
            ip += 2;
            if (offset == 0 || offset > op)
            {
                return false;
            }

            int length = (token & 15) + 4;
            if ((token & 15) == 15)
            {
                int b;
                do
                {
                    if (ip >= source.Length)
                    {
                        return false;
                    }

                    b = source[ip++];
                    length += b;
                    if (length > destination.Length)
                    {
                        return false;
                    }
                }
                while (b == 255);
            }

            if (length > destination.Length - op)
            {
                return false;
            }

            if (offset >= length)
            {
                destination.Slice(op - offset, length).CopyTo(destination[op..]);
            }
            else
            {
                for (int k = 0; k < length; k++)
                {
                    destination[op + k] = destination[op - offset + k];
                }
            }

            op += length;
        }
    }

    /// <summary>The most a valid stream of <paramref name="compressedLength"/> bytes can expand to, used to refuse absurd sizes before allocating.</summary>
    public static long MaxExpandedLength(int compressedLength) => ((long)compressedLength * 255) + 64;
}
