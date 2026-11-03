using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Codecs;

/// <summary>
/// The body of a resident index file (term dictionary, field lengths) that may be filled by page on first touch. A reader that
/// opens a file from storage that charges for every byte read (a static index over HTTP) asks for the ranges it is about to use
/// with <see cref="Ensure"/> and reads them from <see cref="Data"/>; on other storage the file is read whole at open and
/// <see cref="Ensure"/> does nothing. A page that cannot be read (a chunk not fetched yet, a short file) leaves the page unmarked
/// and propagates the exception, so the next call tries again.
/// </summary>
internal sealed class PagedBytes : IDisposable
{
    /// <summary>Bytes per page. Small next to a 16 KiB chunk so one lookup touches few pages; large enough to keep the bitmap tiny.</summary>
    public const int PageSize = 4096;

    private readonly IRangeReader? _source;
    private readonly ulong[]? _loaded;
    private readonly Lock _gate = new();

    private PagedBytes(byte[] data, IRangeReader? source)
    {
        Data = data;
        _source = source;
        if (source is not null)
        {
            _loaded = new ulong[((data.Length + PageSize - 1) / PageSize + 63) / 64];
        }
    }

    /// <summary>The file's bytes; the ranges not yet ensured are zero when the file is paged.</summary>
    public byte[] Data { get; }

    /// <summary>Whether bytes are read on demand.</summary>
    public bool IsPaged => _source is not null;

    /// <summary>Wraps a file that is already completely in memory.</summary>
    public static PagedBytes Whole(byte[] data) => new(data, null);

    /// <summary>Allocates room for a whole file and reads nothing yet; the reader stays open until <see cref="Dispose"/>.</summary>
    public static PagedBytes OnDemand(IRangeReader source, IndexLimits limits)
    {
        if (source.Length > limits.MaxResidentFileBytes)
        {
            throw new CorruptIndexException(source.Name, $"{source.Length} bytes is above the resident file limit of {limits.MaxResidentFileBytes}");
        }

        return new PagedBytes(new byte[source.Length], source);
    }

    /// <summary>Makes the bytes <c>[start, start + length)</c> readable from <see cref="Data"/>. The range is clipped to the file.</summary>
    /// <exception cref="Exception">Whatever the storage throws for a range it cannot serve yet (a static index raises <c>ChunkNotLoadedException</c>).</exception>
    public void Ensure(long start, long length)
    {
        if (_source is null || length <= 0)
        {
            return;
        }

        long end = Math.Min(start + length, Data.Length);
        if (start < 0 || start >= end)
        {
            return;
        }

        int first = (int)(start / PageSize);
        int last = (int)((end - 1) / PageSize);
        for (int page = first; page <= last; page++)
        {
            if ((Volatile.Read(ref _loaded![page >> 6]) & (1UL << (page & 63))) == 0)
            {
                Fill(page);
            }
        }
    }

    private void Fill(int page)
    {
        lock (_gate)
        {
            if ((_loaded![page >> 6] & (1UL << (page & 63))) != 0)
            {
                return;
            }

            int offset = page * PageSize;
            _source!.ReadExactly(offset, Data.AsSpan(offset, Math.Min(PageSize, Data.Length - offset)));
            Volatile.Write(ref _loaded[page >> 6], _loaded[page >> 6] | (1UL << (page & 63)));
        }
    }

    /// <summary>Releases the file.</summary>
    public void Dispose() => _source?.Dispose();
}
