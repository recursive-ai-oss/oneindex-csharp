using System.Text;
using OneIndex.Files;
using OneOcr;

namespace OneIndex.Files.Ocr;

/// <summary>
/// Reads scanned pages and photos of text (PNG, JPEG, TIFF, WebP, BMP, GIF) through oneocr. Every frame is a page. The page text is the
/// recognised lines in reading order, a block's lines joined by a line feed and blocks by a blank line; every word keeps its box in pixels,
/// origin at the top left, so a passage can be drawn on the image.
/// </summary>
/// <remarks>
/// Recognition is deterministic: the same image gives the same text and boxes on every machine, so a citation can be re-read. It is also slow
/// next to reading text (seconds per page on one core); give a folder of scans a generous <see cref="FolderIndexOptions.FileTimeout"/>.
/// </remarks>
public sealed class OcrImageExtractor : IFileExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".webp", ".bmp", ".gif" };

    private readonly OcrEngine _engine;
    private readonly OcrOptions? _options;

    /// <summary>Creates the extractor.</summary>
    /// <param name="engine">The engine; load it once (<c>OcrEngine.LoadBundled()</c>) and share it, it is thread-safe.</param>
    /// <param name="options">Recognition options; null for the engine's.</param>
    public OcrImageExtractor(OcrEngine engine, OcrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options;
    }

    /// <summary>Creates the extractor with the models that ship with oneocr.</summary>
    public OcrImageExtractor()
        : this(OcrEngine.LoadBundled())
    {
    }

    /// <inheritdoc />
    public string Name => "ocr-1";

    /// <inheritdoc />
    public bool Accepts(string fileName) => Extensions.Contains(Path.GetExtension(fileName));

    /// <inheritdoc />
    public ExtractedFile Extract(Stream content, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        IReadOnlyList<OcrPage> pages = _engine.OcrPages(content, _options, cancellationToken);
        var units = new List<ExtractedUnit>();
        for (int i = 0; i < pages.Count; i++)
        {
            ExtractedUnit? unit = BuildUnit(pages[i], i + 1);
            if (unit is not null)
            {
                units.Add(unit);
            }
        }

        return new ExtractedFile("image", units);
    }

    private static ExtractedUnit? BuildUnit(OcrPage page, int number)
    {
        var text = new StringBuilder();
        var blocks = new List<TextBlock>();
        var words = new List<WordSpan>();
        int lineNo = 0;
        foreach (OcrBlock block in page.Blocks)
        {
            if (block.Lines.Count == 0)
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
                OcrLine line = block.Lines[l];
                if (l > 0)
                {
                    text.Append('\n');
                }

                int lineStart = text.Length;
                text.Append(line.Text);
                int from = 0;
                foreach (OcrWord w in line.Words)
                {
                    int at = w.Text.Length == 0 ? -1 : line.Text.IndexOf(w.Text, from, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        continue;
                    }

                    from = at + w.Text.Length;
                    words.Add(new WordSpan(lineStart + at, lineStart + from, lineNo, new SourceBox(w.Box.Left, w.Box.Top, w.Box.Right, w.Box.Bottom)));
                }

                lineNo++;
            }

            blocks.Add(new TextBlock(blockStart, text.Length, block.IsTable ? BlockKind.Table : BlockKind.Paragraph));
        }

        if (blocks.Count == 0 || text.Length == 0)
        {
            return null;
        }

        return new ExtractedUnit(number, text.ToString(), blocks) { Words = words, BoxUnits = "px", Width = page.Width, Height = page.Height };
    }
}
