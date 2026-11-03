namespace OneIndex.Files;

/// <summary>
/// What a folder index stores: one document per passage of text, plus one record per file that says what became of it
/// (<see cref="KindFile"/>, <see cref="KindError"/>). Search the passages with <see cref="FolderSearch.Passages"/>; the file records
/// exist so that a rescan can tell which files are already indexed and which changed.
/// </summary>
/// <remarks>
/// Identifiers are deterministic. A file's identifier (<see cref="FileId"/>) is its path below the indexed root with <c>/</c> separators and a
/// leading <c>/</c>; an archive entry is addressed as <c>/archive.zip!/inner/path.txt</c>. A passage is <c>FileId#ordinal</c> and the record of a
/// file is <c>file:FileId</c>, so the three kinds never collide. Everything of one file (its passages and its record, and for an archive those of
/// its entries) goes into the index in one commit: after a crash a file is either complete or absent.
/// <para>The index holds the extracted text of your files. It is as sensitive as the files, and it stays on this machine: nothing here
/// uses the network.</para>
/// </remarks>
public class FilePassage
{
    /// <summary>A passage of a file's text.</summary>
    public const string KindPassage = "passage";

    /// <summary>The record of an indexed file (including files with no text to index).</summary>
    public const string KindFile = "file";

    /// <summary>The record of a file that could not be read; <see cref="Error"/> says why.</summary>
    public const string KindError = "error";

    /// <summary>Dimensions of the passage embedding. Matches the packaged default embedder; a passage index opened with an embedder of another size is refused.</summary>
    public const int EmbeddingDimensions = 384;

    /// <summary>The document identifier: <c>FileId#ordinal</c> for a passage, <c>file:FileId</c> for a file record.</summary>
    [SearchId]
    public string Id { get; set; } = string.Empty;

    /// <summary><see cref="KindPassage"/>, <see cref="KindFile"/> or <see cref="KindError"/>.</summary>
    [Filterable, Sortable]
    public string Kind { get; set; } = KindPassage;

    /// <summary>The identifier of the file the passage belongs to (see the type remarks). Collapse hits per file with <c>Distinct(p =&gt; p.FileId)</c>.</summary>
    [Filterable, Facet]
    public string FileId { get; set; } = string.Empty;

    /// <summary>The top-level file that holds this one: the file itself, or the outermost archive of an archive entry.</summary>
    [Filterable]
    public string Container { get; set; } = string.Empty;

    /// <summary>The file's name without its directory, searched with a low weight so a query can find a document by its title.</summary>
    [FullText]
    public string FileName { get; set; } = string.Empty;

    /// <summary>The headings above the passage, outermost first, joined with <c> &gt; </c>. Empty when there are none.</summary>
    [FullText(Boost = 2)]
    public string Heading { get; set; } = string.Empty;

    /// <summary>The passage text: exactly the characters <see cref="Start"/> to <see cref="End"/> of the page's (or file's) extracted text. Null on file records.</summary>
    [FullText]
    public string? Text { get; set; }

    /// <summary>The format the file was read as: <c>text</c>, <c>markdown</c>, <c>html</c>, <c>pdf</c>, <c>image</c>, <c>archive</c> or <c>binary</c>.</summary>
    [Filterable, Facet]
    public string Format { get; set; } = string.Empty;

    /// <summary>The page number, counted from 1; 0 for formats without pages.</summary>
    [Filterable]
    public int Page { get; set; }

    /// <summary>Position of the passage among the file's passages, counted from 0.</summary>
    public int Ordinal { get; set; }

    /// <summary>First character of the passage in the page's extracted text (UTF-16 code units).</summary>
    public int Start { get; set; }

    /// <summary>One past the last character of the passage in the page's extracted text (UTF-16 code units).</summary>
    public int End { get; set; }

    /// <summary>Where the passage sits on the page, as <c>left,top,right,bottom</c> boxes separated by <c>;</c>, one per line of text, in <see cref="BoxUnits"/> with the origin at the top left. Empty for formats without geometry. Read it through <see cref="Citation.Boxes"/>.</summary>
    public string Boxes { get; set; } = string.Empty;

    /// <summary><c>pt</c> (PDF points) or <c>px</c> (image pixels); empty without geometry.</summary>
    public string BoxUnits { get; set; } = string.Empty;

    /// <summary>Width of the page in <see cref="BoxUnits"/>; 0 without geometry.</summary>
    public double PageWidth { get; set; }

    /// <summary>Height of the page in <see cref="BoxUnits"/>; 0 without geometry.</summary>
    public double PageHeight { get; set; }

    /// <summary>Size of the file in bytes. File records only.</summary>
    public long Size { get; set; }

    /// <summary>Last write time of the file, in UTC ticks. File records only.</summary>
    public long ModifiedTicks { get; set; }

    /// <summary>SHA-256 of the file's bytes, lower-case hex. File records only; it confirms a change and finds renames.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>How many passages the file has. File records only.</summary>
    public int PassageCount { get; set; }

    /// <summary>Identifies the extraction and chunking settings the file was indexed with; a different value makes a rescan index the file again. File records only.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Why the file could not be read. Error records only.</summary>
    [FullText]
    public string? Error { get; set; }

    /// <summary>The passage embedding, filled when the index has an embedder.</summary>
    [Vector(From = nameof(Text), Dimensions = EmbeddingDimensions)]
    public float[]? Embedding { get; set; }
}
