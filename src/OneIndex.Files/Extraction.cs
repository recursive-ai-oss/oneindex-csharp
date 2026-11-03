namespace OneIndex.Files;

/// <summary>The structural role of a block of text.</summary>
public enum BlockKind
{
    /// <summary>Running text.</summary>
    Paragraph = 0,

    /// <summary>A heading; <see cref="TextBlock.Level"/> is 1 (outermost) to 6.</summary>
    Heading = 1,

    /// <summary>A table or a run of table rows; the chunker splits it only between lines.</summary>
    Table = 2,

    /// <summary>A list item.</summary>
    ListItem = 3,

    /// <summary>Preformatted text or code; the chunker splits it only between lines.</summary>
    Code = 4,
}

/// <summary>A block of a unit's text: the characters <see cref="Start"/> to <see cref="End"/> of <see cref="ExtractedUnit.Text"/>.</summary>
/// <param name="Start">First character.</param>
/// <param name="End">One past the last character.</param>
/// <param name="Kind">What the block is.</param>
/// <param name="Level">Heading level, 1 to 6; 0 for other blocks.</param>
public readonly record struct TextBlock(int Start, int End, BlockKind Kind, int Level = 0);

/// <summary>A box in page units with the origin at the top left and y growing downwards.</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
public readonly record struct SourceBox(double Left, double Top, double Right, double Bottom)
{
    /// <summary>The smallest box that holds both.</summary>
    public static SourceBox Union(SourceBox a, SourceBox b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}

/// <summary>A word of a page with its place in the unit's text and on the page.</summary>
/// <param name="Start">First character of the word in <see cref="ExtractedUnit.Text"/>.</param>
/// <param name="End">One past its last character.</param>
/// <param name="Line">Index of the line the word is on; words of one line are boxed together.</param>
/// <param name="Box">Where the word is on the page.</param>
public readonly record struct WordSpan(int Start, int End, int Line, SourceBox Box);

/// <summary>
/// One citable unit of a file: a page of a PDF, a scanned image, or the whole text of a file without pages. Passages never span units, so a
/// citation is always (file, unit, character range).
/// </summary>
public sealed class ExtractedUnit
{
    /// <summary>Creates a unit.</summary>
    /// <param name="page">The page number counted from 1, or 0 when the format has no pages.</param>
    /// <param name="text">The unit's text. Passages are substrings of it.</param>
    /// <param name="blocks">Blocks in text order; they must not overlap and must lie inside <paramref name="text"/>.</param>
    public ExtractedUnit(int page, string text, IReadOnlyList<TextBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(blocks);
        Page = page;
        Text = text;
        Blocks = blocks;
    }

    /// <summary>The page number counted from 1, or 0 when the format has no pages.</summary>
    public int Page { get; }

    /// <summary>The unit's text.</summary>
    public string Text { get; }

    /// <summary>The blocks, in text order.</summary>
    public IReadOnlyList<TextBlock> Blocks { get; }

    /// <summary>Words with page geometry, in text order; empty for formats without geometry.</summary>
    public IReadOnlyList<WordSpan> Words { get; init; } = [];

    /// <summary><c>pt</c> or <c>px</c> when <see cref="Words"/> has geometry, else empty.</summary>
    public string BoxUnits { get; init; } = string.Empty;

    /// <summary>Page width in <see cref="BoxUnits"/>.</summary>
    public double Width { get; init; }

    /// <summary>Page height in <see cref="BoxUnits"/>.</summary>
    public double Height { get; init; }
}

/// <summary>What an extractor read from one file.</summary>
public sealed class ExtractedFile
{
    /// <summary>Creates a result.</summary>
    /// <param name="format">The format name stored on the passages (<c>pdf</c>, <c>html</c>, ...).</param>
    /// <param name="units">The citable units in order.</param>
    public ExtractedFile(string format, IReadOnlyList<ExtractedUnit> units)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(units);
        Format = format;
        Units = units;
    }

    /// <summary>The format name.</summary>
    public string Format { get; }

    /// <summary>The units in order; may be empty for a file with no text (a scan without OCR, an empty file).</summary>
    public IReadOnlyList<ExtractedUnit> Units { get; }
}

/// <summary>Reads one kind of file into citable units. Implementations must be deterministic: the same bytes give the same text and boxes.</summary>
public interface IFileExtractor
{
    /// <summary>A short stable name, part of the settings fingerprint (include a version when the output can change).</summary>
    string Name { get; }

    /// <summary>Whether this extractor reads files with this name (decided by the extension; the content is not opened).</summary>
    /// <param name="fileName">The file's name without its directory.</param>
    bool Accepts(string fileName);

    /// <summary>Reads the file.</summary>
    /// <param name="content">The file's bytes; seekable.</param>
    /// <param name="fileName">The file's name without its directory.</param>
    /// <param name="cancellationToken">Cancelled when the per-file time limit passes or the run is cancelled.</param>
    /// <returns>The units. Throws for a file that cannot be read; the indexer records an error document.</returns>
    ExtractedFile Extract(Stream content, string fileName, CancellationToken cancellationToken);
}
