namespace OneIndex.Files;

/// <summary>Settings for indexing a folder. Every value has a default that works for a document folder.</summary>
public sealed class FolderIndexOptions
{
    /// <summary>
    /// Extractors tried in order for a file name. Default: <see cref="TextExtractor"/>, <see cref="HtmlExtractor"/>, <see cref="PdfExtractor"/>.
    /// Add the OCR extractor from <c>RecursiveAI.OneIndex.Files.Ocr</c> to read scans. Archives are read by the indexer itself, entry by entry.
    /// </summary>
    public IReadOnlyList<IFileExtractor>? Extractors { get; init; }

    /// <summary>Passage sizes. Default 256 tokens, at most 384.</summary>
    public ChunkOptions Chunking { get; init; } = new();

    /// <summary>Extra ignore lines in the <c>.gitignore</c> style, applied from the root and before the rules files.</summary>
    public IReadOnlyList<string> IgnorePatterns { get; init; } = [];

    /// <summary>Apply <see cref="IgnoreRules.Defaults"/> (version-control folders, dependency folders, editor litter). Default true.</summary>
    public bool UseDefaultIgnores { get; init; } = true;

    /// <summary>Read <c>.gitignore</c> and <c>.oneindexignore</c> files in the folders walked. Default true.</summary>
    public bool UseIgnoreFiles { get; init; } = true;

    /// <summary>Directories to leave out, by full path; use it for the index directory when it lies inside the root.</summary>
    public IReadOnlyList<string> ExcludeDirectories { get; init; } = [];

    /// <summary>Largest file indexed, in bytes; a larger one becomes an error document. Default 1 GiB.</summary>
    public long MaxFileBytes { get; init; } = 1L << 30;

    /// <summary>Time one file may take to read, extract and chunk, archives included; then it becomes an error document. Default 5 minutes.</summary>
    public TimeSpan FileTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Commit after this many files. Default 200.</summary>
    public int CommitEveryFiles { get; init; } = 200;

    /// <summary>Commit after this many bytes of files have been read. Default 256 MiB.</summary>
    public long CommitEveryBytes { get; init; } = 256L << 20;

    /// <summary>
    /// Files extracted at once. 1 (the default) runs everything on the calling thread. More runs extraction on pool threads in groups, while the
    /// index is written in file order on the calling thread, so the index is identical for every value.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>Deepest archive inside archive that is opened. Default 4; a deeper one is an error document.</summary>
    public int MaxArchiveDepth { get; init; } = 4;

    /// <summary>Largest archive entry read into memory, in bytes. Default 256 MiB.</summary>
    public long MaxArchiveEntryBytes { get; init; } = 256L << 20;

    /// <summary>Total bytes read from one archive. Default 4 GiB.</summary>
    public long MaxArchiveBytes { get; init; } = 4L << 30;

    /// <summary>Entries read from one archive. Default 100,000.</summary>
    public int MaxArchiveEntries { get; init; } = 100_000;

    /// <summary>The clock for the file time limit and the watcher. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        Chunking.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDegreeOfParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommitEveryFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommitEveryBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxArchiveDepth, 0);
        if (FileTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FileTimeout), "The file time limit must be positive.");
        }
    }
}

/// <summary>Where a run stands. Reported after every file.</summary>
/// <param name="FilesSeen">Files found under the root so far (archive entries not counted).</param>
/// <param name="FilesToIndex">Files this run has to read: new and changed ones. Known once the walk is done.</param>
/// <param name="FilesIndexed">Files read and indexed so far (error documents included).</param>
/// <param name="FilesUnchanged">Files left alone because size and time, or the hash, say they are as indexed.</param>
/// <param name="FilesFailed">Files that became error documents so far.</param>
/// <param name="PassagesWritten">Passages written so far.</param>
/// <param name="BytesRead">Bytes of files read so far.</param>
/// <param name="Commits">Commits so far.</param>
/// <param name="Current">The file being read, or null.</param>
public sealed record FolderProgress(
    long FilesSeen, long FilesToIndex, long FilesIndexed, long FilesUnchanged, long FilesFailed, long PassagesWritten, long BytesRead, long Commits, string? Current);

/// <summary>What a run changed.</summary>
public sealed record FolderIndexResult
{
    /// <summary>Files found under the root.</summary>
    public long FilesSeen { get; init; }

    /// <summary>New files indexed.</summary>
    public long Added { get; init; }

    /// <summary>Changed files indexed again.</summary>
    public long Updated { get; init; }

    /// <summary>Files found at a new path by their hash and moved without being read again.</summary>
    public long Renamed { get; init; }

    /// <summary>Files gone from the root and removed from the index.</summary>
    public long Removed { get; init; }

    /// <summary>Files left alone.</summary>
    public long Unchanged { get; init; }

    /// <summary>Files, among those indexed, that became error documents (unreadable, too large, timed out).</summary>
    public long Failed { get; init; }

    /// <summary>Passages written.</summary>
    public long Passages { get; init; }

    /// <summary>Bytes of files read.</summary>
    public long BytesRead { get; init; }

    /// <summary>Commits made.</summary>
    public long Commits { get; init; }

    /// <summary>Time the run took, by the options' clock.</summary>
    public TimeSpan Elapsed { get; init; }
}
