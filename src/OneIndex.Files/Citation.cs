using System.Globalization;

namespace OneIndex.Files;

/// <summary>
/// Where a passage came from: enough to open the file and show the exact text, and for PDF and scanned pages to draw it.
/// </summary>
/// <remarks>
/// <see cref="Start"/> and <see cref="End"/> count UTF-16 characters of the page's extracted text (the text of the whole file for formats without
/// pages), and <c>pageText[Start..End]</c> is exactly <see cref="Text"/>. <see cref="ReadSourceText"/> extracts the page again and returns that
/// substring, which is how a citation is checked. For HTML the text is the extractor's rendering of the page (see <see cref="HtmlExtractor"/>),
/// not the markup.
/// </remarks>
public sealed record Citation
{
    /// <summary>The file's path below the indexed root, with <c>/</c> separators and no leading slash. For an archive entry, the archive's path.</summary>
    public required string File { get; init; }

    /// <summary>The path of the entry inside the archive (nested archives joined by <c>!/</c>), or null when the passage is from a plain file.</summary>
    public string? Entry { get; init; }

    /// <summary>The file identifier of the index (<see cref="FilePassage.FileId"/>).</summary>
    public required string FileId { get; init; }

    /// <summary>The file's own name (the entry's name for an archive entry).</summary>
    public required string FileName { get; init; }

    /// <summary>The format the passage was read as.</summary>
    public required string Format { get; init; }

    /// <summary>The page, counted from 1; 0 when the format has no pages.</summary>
    public int Page { get; init; }

    /// <summary>First character of the passage in the page's extracted text.</summary>
    public int Start { get; init; }

    /// <summary>One past the last character of the passage in the page's extracted text.</summary>
    public int End { get; init; }

    /// <summary>One box per line of text the passage covers, origin at the top left; empty for formats without geometry (PDF and scanned pages have it).</summary>
    public IReadOnlyList<SourceBox> Boxes { get; init; } = [];

    /// <summary><c>pt</c> for PDF, <c>px</c> for images, empty without geometry.</summary>
    public string BoxUnits { get; init; } = string.Empty;

    /// <summary>Width of the page in <see cref="BoxUnits"/>.</summary>
    public double PageWidth { get; init; }

    /// <summary>Height of the page in <see cref="BoxUnits"/>.</summary>
    public double PageHeight { get; init; }

    /// <summary>The headings above the passage.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>The passage text.</summary>
    public required string Text { get; init; }

    /// <summary>Builds the citation of a passage document.</summary>
    /// <param name="passage">A document of kind <see cref="FilePassage.KindPassage"/>.</param>
    /// <exception cref="ArgumentException">The document is a file record.</exception>
    public static Citation From(FilePassage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);
        if (passage.Kind != FilePassage.KindPassage)
        {
            throw new ArgumentException("Only a passage can be cited.", nameof(passage));
        }

        int cut = passage.FileId.IndexOf("!/", StringComparison.Ordinal);
        string top = cut < 0 ? passage.FileId : passage.FileId[..cut];
        return new Citation
        {
            File = top.TrimStart('/'),
            Entry = cut < 0 ? null : passage.FileId[(cut + 2)..],
            FileId = passage.FileId,
            FileName = passage.FileName,
            Format = passage.Format,
            Page = passage.Page,
            Start = passage.Start,
            End = passage.End,
            Boxes = ParseBoxes(passage.Boxes),
            BoxUnits = passage.BoxUnits,
            PageWidth = passage.PageWidth,
            PageHeight = passage.PageHeight,
            Heading = passage.Heading,
            Text = passage.Text ?? string.Empty,
        };
    }

    /// <summary>Extracts the cited page again from the file under <paramref name="root"/> and returns the characters <see cref="Start"/> to <see cref="End"/>.</summary>
    /// <param name="root">The folder that was indexed.</param>
    /// <param name="options">The options the folder was indexed with (extractors matter); null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>The source text of the passage; equal to <see cref="Text"/> while the file is unchanged.</returns>
    /// <exception cref="FileNotFoundException">The file, the archive entry or the page no longer exists.</exception>
    public string ReadSourceText(string root, FolderIndexOptions? options = null, CancellationToken cancellationToken = default)
    {
        string page = ReadPageText(root, options, cancellationToken);
        if (Start < 0 || End > page.Length || End < Start)
        {
            throw new InvalidDataException($"The range {Start}..{End} is outside the {page.Length} characters now on the page: the file has changed.");
        }

        return page.Substring(Start, End - Start);
    }

    /// <summary>Extracts the cited page again and returns all of its text.</summary>
    /// <param name="root">The folder that was indexed.</param>
    /// <param name="options">The options the folder was indexed with; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>The page's extracted text.</returns>
    public string ReadPageText(string root, FolderIndexOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var pipe = new FolderPipeline(options ?? new FolderIndexOptions());
        string path = Path.Combine(Path.GetFullPath(root), File.Replace('/', Path.DirectorySeparatorChar));
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Stream content = fs;
        string name = Path.GetFileName(path);
        try
        {
            if (Entry is not null)
            {
                content = pipe.OpenEntry(fs, name, Entry, cancellationToken);
                name = FileName;
            }

            ExtractedUnit? unit = pipe.ReadUnit(content, name, Page, cancellationToken);
            return unit?.Text ?? throw new FileNotFoundException($"'{FileId}' has no page {Page}.");
        }
        finally
        {
            if (!ReferenceEquals(content, fs))
            {
                content.Dispose();
            }
        }
    }

    private static List<SourceBox> ParseBoxes(string boxes)
    {
        var list = new List<SourceBox>();
        if (boxes.Length == 0)
        {
            return list;
        }

        foreach (string part in boxes.Split(';'))
        {
            string[] v = part.Split(',');
            if (v.Length == 4)
            {
                list.Add(new SourceBox(
                    double.Parse(v[0], CultureInfo.InvariantCulture),
                    double.Parse(v[1], CultureInfo.InvariantCulture),
                    double.Parse(v[2], CultureInfo.InvariantCulture),
                    double.Parse(v[3], CultureInfo.InvariantCulture)));
            }
        }

        return list;
    }
}

/// <summary>Searching and citing in a passage index.</summary>
public static class FolderSearch
{
    /// <summary>Searches the passages for forgiving user text. File records and error documents never match.</summary>
    /// <param name="index">The passage index.</param>
    /// <param name="text">User text; null matches every passage.</param>
    /// <returns>A search to refine with <c>Where</c>, <c>Distinct(p =&gt; p.FileId)</c> (one hit per file), <c>Take</c> and the rest.</returns>
    public static SearchQuery<FilePassage> Passages(this SearchIndex<FilePassage> index, string? text)
    {
        ArgumentNullException.ThrowIfNull(index);
        return index.Search(text).Where(p => p.Kind == "passage");
    }

    /// <summary>The error documents: files that could not be read, with the reason in <see cref="FilePassage.Error"/>.</summary>
    /// <param name="index">The passage index.</param>
    public static SearchQuery<FilePassage> Errors(this SearchIndex<FilePassage> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return index.Search().Where(p => p.Kind == "error");
    }

    /// <summary>The citation of a hit: file, entry, page, character range and boxes.</summary>
    /// <param name="hit">A hit of <see cref="Passages"/>.</param>
    /// <returns>The citation.</returns>
    public static Citation Cite(this SearchHit<FilePassage> hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return Citation.From(hit.Document);
    }
}
