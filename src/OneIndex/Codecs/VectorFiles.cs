using System.Buffers.Binary;
using System.Runtime.InteropServices;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>Shared pieces of the vector and graph files: the block size, block checksums and the little-endian guard.</summary>
internal static class VectorFiles
{
    /// <summary>The target size of a block. A block holds a whole number of records, so a record never straddles two blocks.</summary>
    public const int TargetBlockBytes = 64 * 1024;

    static VectorFiles()
    {
        // Vectors and links are written as raw little-endian words and read back by reinterpreting bytes.
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The vector files are read as little-endian words; this platform is big-endian.");
        }
    }

    public static void EnsureLittleEndian()
    {
        // Touching the class runs the static check.
    }

    /// <summary>Records per block for records of <paramref name="recordBytes"/> bytes.</summary>
    public static int RecordsPerBlock(int recordBytes) => Math.Max(1, TargetBlockBytes / recordBytes);

    public static byte[] CrcTable(IReadOnlyList<uint> crcs)
    {
        var bytes = new byte[crcs.Count * 4];
        for (int i = 0; i < crcs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), crcs[i]);
        }

        return bytes;
    }
}

/// <summary>Writes a run of equal-size blocks into a segment file, keeping a CRC-32C of each block so a reader can check one block without reading the file.</summary>
internal sealed class BlockWriter
{
    private readonly SegmentFileBuilder _file;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[] _buffer;
    private int _length;

    public BlockWriter(SegmentFileBuilder file, int blockBytes, CancellationToken cancellationToken)
    {
        VectorFiles.EnsureLittleEndian();
        _file = file;
        _cancellationToken = cancellationToken;
        _buffer = new byte[blockBytes];
        Start = file.Position;
    }

    public long Start { get; }

    public List<uint> Crcs { get; } = [];

    public long BytesWritten { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, _buffer.Length - _length);
            data[..n].CopyTo(_buffer.AsSpan(_length));
            _length += n;
            data = data[n..];
            if (_length == _buffer.Length)
            {
                Flush();
            }
        }
    }

    public void Finish()
    {
        if (_length > 0)
        {
            Flush();
        }
    }

    private void Flush()
    {
        ReadOnlySpan<byte> block = _buffer.AsSpan(0, _length);
        Crcs.Add(Crc32C.Compute(block));
        _file.Write(block, _cancellationToken);
        BytesWritten += _length;
        _length = 0;
    }
}

/// <summary>
/// Reads a run of equal-size blocks of a file on demand through the storage interface and keeps each one once loaded. A block is checked
/// against its CRC-32C when it is read; a mismatch raises <see cref="CorruptIndexException"/> naming the file. After a block is
/// cached, reading it neither allocates nor waits. Several threads may ask for the same block at once; one copy wins.
/// </summary>
internal sealed class BlockCache
{
    private readonly IRangeReader _file;
    private readonly long _dataOffset;
    private readonly long _dataLength;
    private readonly int _blockBytes;
    private readonly uint[] _crcs;
    private readonly byte[]?[] _blocks;
    private readonly Action<byte[], int>? _validate;
    private readonly bool _verify;

    public BlockCache(IRangeReader file, long dataOffset, long dataLength, int blockBytes, uint[] crcs, Action<byte[], int>? validate, IndexLimits limits)
    {
        VectorFiles.EnsureLittleEndian();
        _file = file;
        _dataOffset = dataOffset;
        _dataLength = dataLength;
        _blockBytes = blockBytes;
        _crcs = crcs;
        _validate = validate;
        _verify = !limits.SkipVectorChecksums;
        _blocks = new byte[crcs.Length][];
    }

    public int BlockCount => _blocks.Length;

    public byte[] Get(int block)
    {
        byte[]? b = Volatile.Read(ref _blocks[block]);
        return b ?? Load(block);
    }

    private byte[] Load(int block)
    {
        long start = (long)block * _blockBytes;
        int length = (int)Math.Min(_blockBytes, _dataLength - start);
        var bytes = new byte[length];
        _file.ReadExactly(_dataOffset + start, bytes);
        uint actual = _verify ? Crc32C.Compute(bytes) : _crcs[block];
        if (actual != _crcs[block])
        {
            throw new CorruptIndexException(_file.Name, $"block {block} fails its checksum (stored {_crcs[block]:X8}, computed {actual:X8})");
        }

        _validate?.Invoke(bytes, block);
        return Interlocked.CompareExchange(ref _blocks[block], bytes, null) ?? bytes;
    }

    /// <summary>Reads a table of little-endian words that sits outside the blocks, checking its CRC-32C.</summary>
    public static byte[] ReadChecked(IRangeReader file, long offset, long length, uint expectedCrc, string what, IndexLimits limits)
    {
        var bytes = new byte[length];
        file.ReadExactly(offset, bytes);
        if (!limits.SkipVectorChecksums && Crc32C.Compute(bytes) != expectedCrc)
        {
            throw new CorruptIndexException(file.Name, $"{what} fails its checksum");
        }

        return bytes;
    }

    public static ReadOnlySpan<int> AsInts(byte[] bytes, int offset, int count) => MemoryMarshal.Cast<byte, int>(bytes.AsSpan(offset, count * 4));
}
