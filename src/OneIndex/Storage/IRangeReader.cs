namespace OneIndex.Storage;

/// <summary>
/// Read access to one file by byte range. Readers ask for ranges into their own buffers; nothing in the engine assumes the file
/// is memory-mapped or even local (an HTTP range backend implements the same interface). The length is fixed when the file is
/// opened. Methods may be called from several threads at once.
/// </summary>
public interface IRangeReader : IDisposable
{
    /// <summary>The file name.</summary>
    string Name { get; }

    /// <summary>The length of the file in bytes when it was opened.</summary>
    long Length { get; }

    /// <summary>Reads up to <c>destination.Length</c> bytes starting at <paramref name="offset"/>.</summary>
    /// <param name="offset">The file offset; must not be negative.</param>
    /// <param name="destination">The buffer to fill.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes read. Fewer than requested only when the range reaches the end of the file; 0 at or past the end.</returns>
    int Read(long offset, Span<byte> destination, CancellationToken cancellationToken = default);

    /// <summary>The asynchronous twin of <see cref="Read"/>.</summary>
    /// <param name="offset">The file offset; must not be negative.</param>
    /// <param name="destination">The buffer to fill; it must stay valid until the task completes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes read, as for <see cref="Read"/>.</returns>
    ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default);
}

/// <summary>Helpers over <see cref="IRangeReader"/>.</summary>
public static class RangeReaderExtensions
{
    /// <summary>Fills <paramref name="destination"/> exactly, treating a short read as corruption.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="offset">The file offset.</param>
    /// <param name="destination">The buffer to fill completely.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="CorruptIndexException">The range extends past the end of the file.</exception>
    public static void ReadExactly(this IRangeReader reader, long offset, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (offset < 0 || offset > reader.Length || destination.Length > reader.Length - offset)
        {
            throw new CorruptIndexException(reader.Name, $"range {offset}+{destination.Length} lies outside the file of {reader.Length} bytes");
        }

        int total = 0;
        while (total < destination.Length)
        {
            int n = reader.Read(offset + total, destination[total..], cancellationToken);
            if (n <= 0)
            {
                throw new CorruptIndexException(reader.Name, $"file ended at {offset + total}, expected {reader.Length} bytes");
            }

            total += n;
        }
    }

    /// <summary>The asynchronous twin of <see cref="ReadExactly"/>.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="offset">The file offset.</param>
    /// <param name="destination">The buffer to fill completely.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the buffer is full.</returns>
    /// <exception cref="CorruptIndexException">The range extends past the end of the file.</exception>
    public static async ValueTask ReadExactlyAsync(this IRangeReader reader, long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (offset < 0 || offset > reader.Length || destination.Length > reader.Length - offset)
        {
            throw new CorruptIndexException(reader.Name, $"range {offset}+{destination.Length} lies outside the file of {reader.Length} bytes");
        }

        int total = 0;
        while (total < destination.Length)
        {
            int n = await reader.ReadAsync(offset + total, destination[total..], cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new CorruptIndexException(reader.Name, $"file ended at {offset + total}, expected {reader.Length} bytes");
            }

            total += n;
        }
    }
}
