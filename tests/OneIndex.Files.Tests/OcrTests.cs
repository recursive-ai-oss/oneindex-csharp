using OneIndex.Files.Ocr;
using OneIndex.Files.Tool;

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
}
