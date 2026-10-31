using System.Buffers.Binary;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// The frame every index file shares: an 8-byte header (a 4-byte file-type magic and a 4-byte format version, both little-endian)
/// and a 12-byte footer (footer magic, checksum algorithm, CRC-32C of every byte before the checksum). A reader checks the
/// footer before it trusts any offset in the file.
/// </summary>
public static class IndexFile
{
    /// <summary>The header length in bytes.</summary>
    public const int HeaderLength = 8;

    /// <summary>The footer length in bytes.</summary>
    public const int FooterLength = 12;

    /// <summary>The value of the footer's magic field (ASCII "OIXF" read little-endian).</summary>
    public const uint FooterMagic = 0x4658494F;

    /// <summary>The checksum algorithm id of CRC-32C.</summary>
    public const uint AlgorithmCrc32C = 1;

    /// <summary>Builds a file-type magic from four ASCII characters.</summary>
    /// <param name="tag">Four ASCII characters, for example <c>"OIXC"</c>.</param>
    /// <returns>The magic as written in the header.</returns>
    public static uint Magic(ReadOnlySpan<char> tag)
    {
        if (tag.Length != 4)
        {
            throw new ArgumentException("A magic is exactly four characters.", nameof(tag));
        }

        return (uint)tag[0] | ((uint)tag[1] << 8) | ((uint)tag[2] << 16) | ((uint)tag[3] << 24);
    }

    /// <summary>Checks a whole file held in memory: header magic, version no newer than supported, footer and checksum.</summary>
    /// <param name="fileName">The file name, for the exception.</param>
    /// <param name="file">The complete file.</param>
    /// <param name="magic">The expected file-type magic.</param>
    /// <param name="maxVersion">The newest format version this reader understands.</param>
    /// <returns>The format version found in the header.</returns>
    /// <exception cref="CorruptIndexException">The file is too short, has the wrong magic or fails its checksum.</exception>
    /// <exception cref="UnsupportedFormatException">The version is newer than <paramref name="maxVersion"/>.</exception>
    public static int Validate(string fileName, ReadOnlySpan<byte> file, uint magic, int maxVersion)
    {
        if (file.Length < HeaderLength + FooterLength)
        {
            throw new CorruptIndexException(fileName, $"{file.Length} bytes is shorter than a header and footer");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(file) != magic)
        {
            throw new CorruptIndexException(fileName, "wrong magic: not a file of this type");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        ReadOnlySpan<byte> footer = file[^FooterLength..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != FooterMagic)
        {
            throw new CorruptIndexException(fileName, "footer magic missing: the file is truncated or overwritten");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != AlgorithmCrc32C)
        {
            throw new UnsupportedFormatException(fileName, "unknown checksum algorithm");
        }

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
        uint actual = Crc32C.Compute(file[..^4]);
        if (stored != actual)
        {
            throw new CorruptIndexException(fileName, $"checksum mismatch (stored {stored:X8}, computed {actual:X8})");
        }

        if (version == 0 || version > (uint)maxVersion)
        {
            throw new UnsupportedFormatException(fileName, $"format version {version} is not supported (newest understood: {maxVersion})");
        }

        return (int)version;
    }

    /// <summary>
    /// Reads and checks only the footer of a file, which is enough to tell that it is complete and is the file a commit recorded.
    /// </summary>
    /// <param name="reader">The open file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checksum stored in the footer.</returns>
    /// <exception cref="CorruptIndexException">The file is too short or its footer is not a footer.</exception>
    public static uint ReadFooterChecksum(IRangeReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.Length < HeaderLength + FooterLength)
        {
            throw new CorruptIndexException(reader.Name, $"{reader.Length} bytes is shorter than a header and footer");
        }

        Span<byte> footer = stackalloc byte[FooterLength]; // fixed 12-byte buffer
        reader.ReadExactly(reader.Length - FooterLength, footer, cancellationToken);
        return ParseFooter(reader.Name, footer);
    }

    /// <summary>The asynchronous twin of <see cref="ReadFooterChecksum"/>.</summary>
    /// <param name="reader">The open file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The checksum stored in the footer.</returns>
    public static async ValueTask<uint> ReadFooterChecksumAsync(IRangeReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.Length < HeaderLength + FooterLength)
        {
            throw new CorruptIndexException(reader.Name, $"{reader.Length} bytes is shorter than a header and footer");
        }

        var footer = new byte[FooterLength];
        await reader.ReadExactlyAsync(reader.Length - FooterLength, footer, cancellationToken).ConfigureAwait(false);
        return ParseFooter(reader.Name, footer);
    }

    /// <summary>Reads the whole file and checks its checksum against its footer.</summary>
    /// <param name="reader">The open file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verified checksum.</returns>
    /// <exception cref="CorruptIndexException">The footer is wrong or the checksum does not match the bytes.</exception>
    public static uint VerifyChecksum(IRangeReader reader, CancellationToken cancellationToken = default)
    {
        uint stored = ReadFooterChecksum(reader, cancellationToken);
        var buffer = new byte[64 * 1024];
        long end = reader.Length - 4;
        uint crc = 0;
        for (long offset = 0; offset < end;)
        {
            int n = (int)Math.Min(buffer.Length, end - offset);
            reader.ReadExactly(offset, buffer.AsSpan(0, n), cancellationToken);
            crc = Crc32C.Update(crc, buffer.AsSpan(0, n));
            offset += n;
        }

        if (crc != stored)
        {
            throw new CorruptIndexException(reader.Name, $"checksum mismatch (stored {stored:X8}, computed {crc:X8})");
        }

        return stored;
    }

    private static uint ParseFooter(string fileName, ReadOnlySpan<byte> footer)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != FooterMagic)
        {
            throw new CorruptIndexException(fileName, "footer magic missing: the file is truncated or overwritten");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) != AlgorithmCrc32C)
        {
            throw new UnsupportedFormatException(fileName, "unknown checksum algorithm");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]);
    }
}

/// <summary>What <see cref="IndexFileWriter.Finish"/> learned about the file it completed.</summary>
/// <param name="Length">The file length in bytes, footer included.</param>
/// <param name="Checksum">The CRC-32C stored in the footer.</param>
public readonly record struct IndexFileSummary(long Length, uint Checksum);

/// <summary>
/// Writes one index file in the shared frame: the header goes out first, the body through <see cref="Write"/>, and
/// <see cref="Finish"/> appends the footer and fsyncs. The checksum is accumulated as the bytes pass, so no second pass is needed.
/// </summary>
public sealed class IndexFileWriter : IDisposable, IAsyncDisposable
{
    private readonly IStorageWriter _writer;
    private uint _crc;
    private bool _finished;

    /// <summary>Starts a file by writing its header.</summary>
    /// <param name="writer">The new file's writer; this object takes ownership and disposes it.</param>
    /// <param name="magic">The file-type magic (see <see cref="IndexFile.Magic"/>).</param>
    /// <param name="version">The format version, at least 1.</param>
    public IndexFileWriter(IStorageWriter writer, uint magic, int version)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        Span<byte> header = stackalloc byte[IndexFile.HeaderLength]; // fixed 8-byte buffer
        BinaryPrimitives.WriteUInt32LittleEndian(header, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)version);
        Append(header);
    }

    /// <summary>The number of bytes written so far, header included.</summary>
    public long Position => _writer.Position;

    /// <summary>Appends body bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _writer.Write(data, cancellationToken);
        _crc = Crc32C.Update(_crc, data);
    }

    /// <summary>The asynchronous twin of <see cref="Write"/>.</summary>
    /// <param name="data">The bytes; they must stay valid until the task completes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are accepted.</returns>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        await _writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        _crc = Crc32C.Update(_crc, data.Span);
    }

    /// <summary>Appends the footer, fsyncs the file and closes it. The name is durable only after <see cref="IStorage.SyncDirectory"/>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The final length and checksum, to record in a commit.</returns>
    public IndexFileSummary Finish(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        Span<byte> footer = stackalloc byte[IndexFile.FooterLength]; // fixed 12-byte buffer
        BinaryPrimitives.WriteUInt32LittleEndian(footer, IndexFile.FooterMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], IndexFile.AlgorithmCrc32C);
        uint crc = Crc32C.Update(_crc, footer[..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], crc);
        _writer.Write(footer, cancellationToken);
        _writer.Sync(cancellationToken);
        long length = _writer.Position;
        _finished = true;
        _writer.Dispose();
        return new IndexFileSummary(length, crc);
    }

    /// <summary>The asynchronous twin of <see cref="Finish"/>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The final length and checksum, to record in a commit.</returns>
    public async ValueTask<IndexFileSummary> FinishAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var footer = new byte[IndexFile.FooterLength];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, IndexFile.FooterMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(4), IndexFile.AlgorithmCrc32C);
        uint crc = Crc32C.Update(_crc, footer.AsSpan(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(8), crc);
        await _writer.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
        await _writer.SyncAsync(cancellationToken).ConfigureAwait(false);
        long length = _writer.Position;
        _finished = true;
        await _writer.DisposeAsync().ConfigureAwait(false);
        return new IndexFileSummary(length, crc);
    }

    /// <summary>Abandons the file if <see cref="Finish"/> was not called; the partial file is left for the caller to delete.</summary>
    public void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            _writer.Dispose();
        }
    }

    /// <summary>Abandons the file if <see cref="FinishAsync"/> was not called.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (_finished)
        {
            return ValueTask.CompletedTask;
        }

        _finished = true;
        return _writer.DisposeAsync();
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        _writer.Write(data);
        _crc = Crc32C.Update(_crc, data);
    }
}
