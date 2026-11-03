using System.Buffers.Binary;
using System.IO.Compression;

namespace OneIndex.Analysis.Asian;

/// <summary>Thrown when a dictionary file is malformed, truncated, fails its checksum or exceeds <see cref="DictionaryLimits"/>.</summary>
public sealed class DictionaryFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong with the file.</param>
    public DictionaryFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>Ceilings the dictionary readers enforce before allocating, so a hostile file cannot exhaust memory.</summary>
/// <param name="MaxSectionBytes">The largest decompressed section, in bytes. Default 256 MiB.</param>
/// <param name="MaxEntries">The most words one dictionary may list. Default 16 million.</param>
public sealed record DictionaryLimits(int MaxSectionBytes = 256 << 20, int MaxEntries = 16 << 20)
{
    /// <summary>The default limits.</summary>
    public static DictionaryLimits Default { get; } = new();
}

/// <summary>
/// The container every OneIndex dictionary file uses: an 8-byte magic, a section directory, the sections (stored raw or as raw
/// DEFLATE), and a footer holding a CRC-32C of everything before it and a closing magic. All integers are little-endian. The
/// reader checks the footer first, then every offset and length against the file size and the limits, before reading a section.
/// Layout: magic "OIDICT\0" + version(1) | kind u32 | sectionCount u32 | sectionCount x (id u32, offset u64, storedLength u32,
/// rawLength u32, codec u32: 0 raw, 1 deflate) | section bytes | crc32c u32 | "OIDEND\0\1".
/// </summary>
internal static class DictionaryContainer
{
    private static ReadOnlySpan<byte> Magic => "OIDICT\u0000\u0001"u8;

    private static ReadOnlySpan<byte> EndMagic => "OIDEND\u0000\u0001"u8;

    private const int HeaderSize = 16;
    private const int EntrySize = 24;
    private const int FooterSize = 12;

    internal static byte[] Write(uint kind, IReadOnlyList<(uint Id, byte[] Data)> sections, bool compress = true)
    {
        var stored = new List<(uint Id, byte[] Data, int Raw, uint Codec)>();
        foreach ((uint id, byte[] data) in sections)
        {
            if (compress)
            {
                using var ms = new MemoryStream();
                using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    z.Write(data);
                }

                if (ms.Length < data.Length)
                {
                    stored.Add((id, ms.ToArray(), data.Length, 1));
                    continue;
                }
            }

            stored.Add((id, data, data.Length, 0));
        }

        long offset = HeaderSize + (long)stored.Count * EntrySize;
        long total = offset;
        foreach (var s in stored)
        {
            total += s.Data.Length;
        }

        byte[] file = new byte[total + FooterSize];
        Magic.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), kind);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)stored.Count);
        int p = HeaderSize;
        foreach (var s in stored)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(p), s.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(p + 4), (ulong)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(p + 12), (uint)s.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(p + 16), (uint)s.Raw);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(p + 20), s.Codec);
            s.Data.CopyTo(file, (int)offset);
            offset += s.Data.Length;
            p += EntrySize;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan((int)total), Crc32C.Compute(file.AsSpan(0, (int)total)));
        EndMagic.CopyTo(file.AsSpan((int)total + 4));
        return file;
    }

    /// <summary>
    /// Words are searched by narrowing a sorted range one character at a time, so the lists must be non-empty words in UTF-16 code unit
    /// order (repeats allowed); a checksummed file that breaks this would send the search past a word's end.
    /// </summary>
    internal static void RequireSortedWords(ReadOnlySpan<char> chars, ReadOnlySpan<int> offsets)
    {
        for (int i = 0; i + 1 < offsets.Length; i++)
        {
            ReadOnlySpan<char> word = chars[offsets[i]..offsets[i + 1]];
            if (word.Length == 0)
            {
                throw new DictionaryFormatException("The dictionary lists an empty word.");
            }

            if (i > 0 && chars[offsets[i - 1]..offsets[i]].SequenceCompareTo(word) > 0)
            {
                throw new DictionaryFormatException("The dictionary's words are not in order.");
            }
        }
    }

    /// <summary>Validates the file and returns the decompressed sections by id.</summary>
    internal static Dictionary<uint, byte[]> Read(ReadOnlySpan<byte> file, uint expectedKind, DictionaryLimits limits)
    {
        if (file.Length < HeaderSize + FooterSize)
        {
            throw new DictionaryFormatException("The dictionary file is truncated.");
        }

        if (!file[..8].SequenceEqual(Magic))
        {
            throw new DictionaryFormatException("Not an OneIndex dictionary file (bad magic or unsupported version).");
        }

        if (!file[^8..].SequenceEqual(EndMagic))
        {
            throw new DictionaryFormatException("The dictionary file has no closing magic (truncated or damaged).");
        }

        int body = file.Length - FooterSize;
        if (BinaryPrimitives.ReadUInt32LittleEndian(file[body..]) != Crc32C.Compute(file[..body]))
        {
            throw new DictionaryFormatException("The dictionary file fails its checksum.");
        }

        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        if (kind != expectedKind)
        {
            throw new DictionaryFormatException($"The dictionary is of kind {kind}, expected {expectedKind}.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        if (count > 64 || HeaderSize + (long)count * EntrySize > body)
        {
            throw new DictionaryFormatException("The dictionary's section directory is invalid.");
        }

        var result = new Dictionary<uint, byte[]>();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = file.Slice(HeaderSize + i * EntrySize, EntrySize);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(e);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(e[4..]);
            uint storedLength = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            uint codec = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
            if (offset < (ulong)(HeaderSize + (long)count * EntrySize) || offset > (ulong)body || storedLength > (ulong)body - offset)
            {
                throw new DictionaryFormatException($"Section {id} lies outside the file.");
            }

            if (rawLength > limits.MaxSectionBytes)
            {
                throw new DictionaryFormatException($"Section {id} is larger than the limit of {limits.MaxSectionBytes} bytes.");
            }

            if (result.ContainsKey(id))
            {
                throw new DictionaryFormatException($"Section {id} is listed twice.");
            }

            ReadOnlySpan<byte> data = file.Slice((int)offset, (int)storedLength);
            if (codec == 0)
            {
                if (storedLength != rawLength)
                {
                    throw new DictionaryFormatException($"Section {id} has inconsistent lengths.");
                }

                result[id] = data.ToArray();
            }
            else if (codec == 1)
            {
                byte[] raw = new byte[rawLength];
                try
                {
                    using var z = new DeflateStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
                    z.ReadExactly(raw);
                    if (z.ReadByte() != -1)
                    {
                        throw new DictionaryFormatException($"Section {id} is longer than declared.");
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                    throw new DictionaryFormatException($"Section {id} does not decompress to its declared length.");
                }

                result[id] = raw;
            }
            else
            {
                throw new DictionaryFormatException($"Section {id} uses an unknown codec {codec}.");
            }
        }

        return result;
    }
}

/// <summary>CRC-32C (Castagnoli, polynomial 0x1EDC6F41 reflected), table driven; the checksum of every dictionary file.</summary>
internal static class Crc32C
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0x82F63B78u : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }
}
