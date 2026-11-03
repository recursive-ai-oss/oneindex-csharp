using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// Opens the files a commit lists. Most are whole storage files; the parts of a small segment share one pack file (see
/// docs/segments.md), and each of those is read through a window that starts at its recorded offset and ends at its recorded length.
/// </summary>
internal static class PackedFiles
{
    /// <summary>The file-type tag of a pack file ("OIXK"): the frame, then the framed part files one after another.</summary>
    internal const string MagicTag = "OIXK";

    /// <summary>The pack file format version.</summary>
    internal const int FormatVersion = 1;

    /// <summary>Opens a listed file for reading.</summary>
    /// <exception cref="FileNotFoundException">The storage file is missing.</exception>
    /// <exception cref="CorruptIndexException">A pack file is shorter than its listed part reaches.</exception>
    internal static IRangeReader Open(IStorage storage, SegmentFile file, CancellationToken cancellationToken)
        => Wrap(storage.OpenRead(file.FileName, cancellationToken), file);

    /// <summary>The asynchronous twin of <see cref="Open"/>.</summary>
    internal static async ValueTask<IRangeReader> OpenAsync(IStorage storage, SegmentFile file, CancellationToken cancellationToken)
        => Wrap(await storage.OpenReadAsync(file.FileName, cancellationToken).ConfigureAwait(false), file);

    private static IRangeReader Wrap(IRangeReader inner, SegmentFile file)
    {
        if (file.Offset == 0)
        {
            return inner;
        }

        if (inner.Length < file.Offset || inner.Length - file.Offset < file.Length)
        {
            long actual = inner.Length;
            inner.Dispose();
            throw new CorruptIndexException(file.FileName, $"pack of {actual} bytes ends before the part '{file.Part}' ({file.Length} bytes at offset {file.Offset})");
        }

        return new WindowReader(inner, file.Offset, file.Length);
    }

    private sealed class WindowReader(IRangeReader inner, long offset, long length) : IRangeReader
    {
        public string Name => inner.Name;

        public long Length => length;

        public int Read(long position, Span<byte> destination, CancellationToken cancellationToken = default)
        {
            if (position < 0 || position >= length)
            {
                return 0;
            }

            int count = (int)Math.Min(destination.Length, length - position);
            return inner.Read(offset + position, destination[..count], cancellationToken);
        }

        public ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (position < 0 || position >= length)
            {
                return ValueTask.FromResult(0);
            }

            int count = (int)Math.Min(destination.Length, length - position);
            return inner.ReadAsync(offset + position, destination[..count], cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }
}
