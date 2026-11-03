using System.Text;
using OnePdf;
using OnePdf.Text;

namespace OneIndex.Files;

/// <summary>
/// Reads PDF files through OnePDF. Each page with text is a unit. The page text is built from OnePDF's blocks in reading order: the lines of a
/// block joined by a line feed, blocks separated by a blank line, repeated headers and footers (flagged by OnePDF across the document) left out.
/// Words keep their boxes, converted to points with the origin at the top left, so a passage can be drawn on the page.
/// </summary>
/// <remarks>
/// A page of a scan with no text layer has no unit; index scans with the OCR package. Text is extracted for the whole document in one pass,
/// because OnePDF decides what is a header or a footer by comparing pages, so a single page cannot be re-read on its own with the same result.
/// </remarks>
public sealed class PdfExtractor : IFileExtractor
{
    private readonly long _maxBytes;

    /// <summary>Creates the extractor.</summary>
    /// <param name="maxBytes">Largest file read. Default 256 MiB.</param>
    public PdfExtractor(long maxBytes = 256L << 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
    }

    /// <inheritdoc />
    public string Name => "pdf-1";

    /// <inheritdoc />
    public bool Accepts(string fileName) => Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ExtractedFile Extract(Stream content, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        long length = content.CanSeek ? content.Length - content.Position : 0;
        if (length > _maxBytes)
        {
            throw new InvalidDataException($"The file has {length} bytes, more than the {_maxBytes} byte limit for PDF.");
        }

        using PdfDocument doc = PdfDocument.Open(content, new PdfOpenOptions { LeaveOpen = true });
        IReadOnlyList<PdfPageText> pages = doc.ExtractText(
            new PdfTextOptions { MaxDegreeOfParallelism = 1, RetainGlyphs = false },
            cancellationToken);
        var units = new List<ExtractedUnit>();
        foreach (PdfPageText page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractedUnit? unit = BuildUnit(page);
            if (unit is not null)
            {
                units.Add(unit);
            }
        }

        return new ExtractedFile("pdf", units);
    }

    private static ExtractedUnit? BuildUnit(PdfPageText page)
    {
        var text = new StringBuilder();
        var blocks = new List<TextBlock>();
        var words = new List<WordSpan>();
        double height = page.Height;
        int lineNo = 0;
        foreach (PdfTextBlock block in page.Blocks)
        {
            if (block.Role != PdfTextRole.Body || block.Lines.Count == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            int blockStart = text.Length;
            for (int l = 0; l < block.Lines.Count; l++)
            {
                PdfTextLine line = block.Lines[l];
                if (l > 0)
                {
                    text.Append('\n');
                }

                int lineStart = text.Length;
                text.Append(line.Text);
                int searchFrom = 0;
                foreach (PdfTextWord word in line.Words)
                {
                    string w = word.Text;
                    int at = line.Text.IndexOf(w, searchFrom, StringComparison.Ordinal);
                    if (at < 0 || w.Length == 0)
                    {
                        continue;
                    }

                    searchFrom = at + w.Length;
                    PdfRectangle r = word.Bounds;
                    words.Add(new WordSpan(lineStart + at, lineStart + at + w.Length, lineNo, new SourceBox(r.Left, height - r.Top, r.Right, height - r.Bottom)));
                }

                lineNo++;
            }

            blocks.Add(new TextBlock(blockStart, text.Length, block.IsTable ? BlockKind.Table : BlockKind.Paragraph));
        }

        if (blocks.Count == 0)
        {
            return null;
        }

        return new ExtractedUnit(page.PageIndex + 1, text.ToString(), blocks)
        {
            Words = words,
            BoxUnits = "pt",
            Width = page.Width,
            Height = page.Height,
        };
    }
}
