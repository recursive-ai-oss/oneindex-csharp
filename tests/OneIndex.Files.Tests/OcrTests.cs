using OneIndex.Files.Ocr;
using OneIndex.Files.Tool;
using OneIndex.Search;

namespace OneIndex.Files.Tests;

public class OcrTests : IDisposable
{
    private const string Font = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    private readonly TempFolder _t = new();

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AScannedPageBecomesCitedPassagesWithBoxesInPixels()
    {
        string font = Environment.GetEnvironmentVariable("ONEINDEX_TEST_FONT") ?? Font;
        if (!File.Exists(font))
        {
            return; // no font to draw the page with on this machine
        }

        string root = _t.Sub("root");
        Corpus.Small(root, seed: 5, font: font);
        var options = new FolderIndexOptions { Extractors = [new TextExtractor(), new OcrImageExtractor()] };
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        FolderIndexer.Run(index, Path.Combine(root, "scans"), options);
        List<SearchHit<FilePassage>> passages = index.Passages(null).Where(p => p.Format == "image").Take(100).ToList();
        Assert.NotEmpty(passages);
        foreach (SearchHit<FilePassage> h in passages)
        {
            Citation c = h.Cite();
            Assert.Equal("px", c.BoxUnits);
            Assert.Equal(1, c.Page);
            Assert.NotEmpty(c.Boxes);
            Assert.All(c.Boxes, b => Assert.True(b.Right > b.Left && b.Bottom > b.Top && b.Left >= 0 && b.Right <= c.PageWidth + 1 && b.Bottom <= c.PageHeight + 1));
            string reread = c.ReadSourceText(Path.Combine(root, "scans"), options);
            Assert.Equal(c.Text, reread);
        }
    }

    [Fact]
    public void AScannedPdfPageBecomesCitedPassagesWithBoxesInPoints()
    {
        string font = Environment.GetEnvironmentVariable("ONEINDEX_TEST_FONT") ?? Font;
        if (!File.Exists(font))
        {
            return; // no font to draw the page with on this machine
        }

        string root = _t.Sub("root");
        Corpus.Small(root, seed: 5, font: font);
        string scans = Path.Combine(root, "scans");
        string png = Directory.GetFiles(scans, "*.png").Order(StringComparer.Ordinal).First();
        string pdfRoot = _t.Sub("pdfs");
        Directory.CreateDirectory(pdfRoot);
        using (var doc = OnePdf.PdfDocument.CreateEmpty())
        {
            OnePdf.PdfCreatedImage image = doc.AddPngImage(File.ReadAllBytes(png));
            OnePdf.PdfPage page = doc.InsertBlankPage(0, 612, 792);
            using (OnePdf.PdfPageContentBuilder b = page.BeginContent())
            {
                b.DrawImage(image, 36, 300, 540, 343);
                b.Commit();
            }

            using var ms = new MemoryStream();
            doc.Save(ms, new OnePdf.PdfSaveOptions { DocumentId = new byte[16] });
            File.WriteAllBytes(Path.Combine(pdfRoot, "scan.pdf"), ms.ToArray());
        }

        var options = new FolderIndexOptions { Extractors = [new TextExtractor(), new OcrPdfExtractor()], FileTimeout = TimeSpan.FromMinutes(5) };
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        FolderIndexResult run = FolderIndexer.Run(index, pdfRoot, options);
        List<SearchHit<FilePassage>> passages = index.Passages(null).Where(p => p.Format == "pdf").Take(100).ToList();
        Assert.True(passages.Count > 0, run + " / " + string.Join(" | ", index.Errors().Take(5).Select(e => e.Document.Error)));
        foreach (SearchHit<FilePassage> h in passages)
        {
            Citation c = h.Cite();
            Assert.Equal("pt", c.BoxUnits);
            Assert.Equal(1, c.Page);
            Assert.NotEmpty(c.Boxes);
            Assert.All(c.Boxes, b => Assert.True(b.Right > b.Left && b.Bottom > b.Top && b.Left >= 0 && b.Right <= c.PageWidth + 1 && b.Bottom <= c.PageHeight + 1));
        }
    }
}
