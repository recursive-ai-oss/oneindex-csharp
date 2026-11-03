using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>A window onto a range of an open file, so a reader of one embedded structure sees it as a file of its own. Disposing the window leaves the file open for its owner.</summary>
internal sealed class SliceReader(IRangeReader inner, long offset, long length) : IRangeReader
{
    public string Name => inner.Name;

    public long Length => length;

    public int Read(long position, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        if (position < 0 || position >= length)
        {
            return 0;
        }

        return inner.Read(offset + position, destination[..(int)Math.Min(destination.Length, length - position)], cancellationToken);
    }

    public ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (position < 0 || position >= length)
        {
            return ValueTask.FromResult(0);
        }

        return inner.ReadAsync(offset + position, destination[..(int)Math.Min(destination.Length, length - position)], cancellationToken);
    }

    public void Dispose()
    {
    }
}
