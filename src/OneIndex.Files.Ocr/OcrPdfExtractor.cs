using OneIndex.Files;
using OneOcr;
using OneOcr.Pdf;

namespace OneIndex.Files.Ocr;

/// <summary>
/// Reads PDFs whose pages may be scans: each page is its text layer where it has one plus the recognised text of the images on it, with a box
/// in PDF points (origin at the top left) for every word. Put it before the default PDF extractor in <see cref="FolderIndexOptions.Extractors"/>;
/// it replaces that extractor for PDFs rather than falling back to it.
/// </summary>
/// <remarks>Recognition is slow next to reading a text layer (seconds per scanned page); give such a folder a generous <see cref="FolderIndexOptions.FileTimeout"/>.</remarks>
public sealed class OcrPdfExtractor : IFileExtractor
{
    private readonly OcrEngine _engine;
    private readonly PdfOcrOptions? _options;

    /// <summary>Creates the extractor.</summary>
    /// <param name="engine">The engine; load it once (<c>OcrEngine.LoadBundled()</c>) and share it, it is thread-safe.</param>
    /// <param name="options">PDF reading options; null for the defaults (300 dpi, images are read).</param>
    public OcrPdfExtractor(OcrEngine engine, PdfOcrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _options = options;
    }

    /// <summary>Creates the extractor with the models that ship with oneocr.</summary>
    public OcrPdfExtractor()
        : this(OcrEngine.LoadBundled())
    {
    }

    /// <inheritdoc />
    public string Name => "ocr-pdf-1";

    /// <inheritdoc />
    public bool Accepts(string fileName) => string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ExtractedFile Extract(Stream content, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        IReadOnlyList<OcrPage> pages = PdfOcr.Read(content, _engine, _options, cancellationToken);
        var units = new List<ExtractedUnit>();
        for (int i = 0; i < pages.Count; i++)
        {
            ExtractedUnit? unit = OcrImageExtractor.BuildUnit(pages[i], i + 1, "pt");
            if (unit is not null)
            {
                units.Add(unit);
            }
        }

        return new ExtractedFile("pdf", units);
    }
}
