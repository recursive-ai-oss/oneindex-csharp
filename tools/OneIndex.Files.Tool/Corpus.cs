using System.Globalization;
using System.Numerics;
using System.Text;
using OneImage;
using OneImage.Text;
using OnePdf;
using RecursiveAI.OneArchive;

namespace OneIndex.Files.Tool;

/// <summary>
/// Writes a mixed folder of documents from a seed: text, Markdown, HTML, PDF, archives (nested), scanned pages, filler binaries and corrupt
/// files. The same seed and options always give the same bytes, so two machines index the same folder. All text is made of invented words.
/// </summary>
public static class Corpus
{
    private static readonly string[] Syllables =
    [
        "ba", "be", "bi", "bo", "bu", "ca", "ce", "co", "cu", "da", "de", "di", "do", "du", "fa", "fe", "fi", "fo", "ga", "ge", "go", "ha", "he", "hi", "ho",
        "ja", "jo", "ka", "ke", "ki", "ko", "la", "le", "li", "lo", "lu", "ma", "me", "mi", "mo", "mu", "na", "ne", "ni", "no", "nu", "pa", "pe", "pi", "po", "pu",
        "ra", "re", "ri", "ro", "ru", "sa", "se", "si", "so", "su", "ta", "te", "ti", "to", "tu", "va", "ve", "vi", "vo", "wa", "we", "xi", "za", "ze", "zo",
        "an", "en", "in", "on", "un", "ar", "er", "ir", "or", "ur", "al", "el", "il", "ol", "st", "tr", "pr", "gr", "cl", "pl",
    ];

    private static readonly string[] Accented = ["café", "naïve", "résumé", "jalapeño", "Zürich", "smörgåsbord", "日本語のテキスト", "データベース", "検索エンジン", "Привет мир", "Ελληνικά"];

    /// <summary>What to write.</summary>
    public sealed class Options
    {
        /// <summary>Seed of every random choice.</summary>
        public int Seed { get; set; } = 1;

        /// <summary>Number of structured files (text, Markdown, HTML, PDF, archives, scans, corrupt); the filler binaries come on top.</summary>
        public int Files { get; set; } = 6000;

        /// <summary>Total size to reach in MiB; filler binaries make up what the documents do not.</summary>
        public int TargetMiB { get; set; } = 1100;

        /// <summary>A TrueType font for the scanned pages; without one no scans are written.</summary>
        public string? Font { get; set; }

        /// <summary>Scanned pages to write.</summary>
        public int Scans { get; set; } = 12;

        /// <summary>Scale of document sizes: 1.0 gives text files of about 100 KB.</summary>
        public double Scale { get; set; } = 1.0;
    }

    /// <summary>Writes the folder and a manifest listing which files are corrupt on purpose.</summary>
    public static void Generate(string root, Options o)
    {
        Directory.CreateDirectory(root);
        var manifest = new List<string>();
        long bytes = 0;
        int n = o.Files;
        int nText = n * 28 / 100, nMd = n * 20 / 100, nHtml = n * 15 / 100, nPdf = Math.Max(1, n * 8 / 100), nZip = n * 5 / 100, nCorrupt = Math.Max(12, n / 100);
        int nSmall = n - nText - nMd - nHtml - nPdf - nZip - nCorrupt - o.Scans;
        bytes += Many(root, "text", nText, o, ".txt", (rng, i) => Encoding.UTF8.GetBytes(PlainText(rng, (int)(100_000 * o.Scale), i)));
        bytes += Many(root, "md", nMd, o, ".md", (rng, i) => Encoding.UTF8.GetBytes(Markdown(rng, (int)(80_000 * o.Scale))));
        bytes += Many(root, "web", nHtml, o, ".html", (rng, i) => Encoding.UTF8.GetBytes(Html(rng, (int)(90_000 * o.Scale))));
        bytes += Many(root, "notes", Math.Max(0, nSmall), o, ".txt", (rng, i) => Encoding.UTF8.GetBytes(PlainText(rng, 1500 + rng.Next(3000), i)));
        bytes += Many(root, "pdf", nPdf, o, ".pdf", (rng, i) => Pdf(rng, 3 + rng.Next((int)(25 * o.Scale) + 1), i));
        bytes += Many(root, "archives", nZip, o, ".zip", (rng, i) => Zip(rng, o, 6 + rng.Next(30), depth: i % 3 == 0 ? 1 : 0));
        bytes += Odd(root, o);
        if (o.Font is not null)
        {
            for (int i = 0; i < o.Scans; i++)
            {
                string path = Path.Combine(root, "scans", $"scan-{i:D3}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Scan(new Random(Hash(o.Seed, "scan", i)), o.Font, path);
                bytes += new FileInfo(path).Length;
            }
        }

        bytes += Corrupt(root, o, nCorrupt, manifest);

        // Filler: random bytes with names that no extractor reads as text.
        long target = (long)o.TargetMiB << 20;
        var filler = new Random(Hash(o.Seed, "filler", 0));
        string dir = Path.Combine(root, "blobs");
        Directory.CreateDirectory(dir);
        for (int i = 0; bytes < target; i++)
        {
            byte[] b = new byte[Math.Min(target - bytes, 3_000_000 + filler.Next(2_000_000))];
            filler.NextBytes(b);
            b[0] = 0; // a NUL marks it as binary for the sniffer
            File.WriteAllBytes(Path.Combine(dir, $"blob-{i:D4}.bin"), b);
            bytes += b.Length;
        }

        File.WriteAllLines(Path.Combine(root, "..", Path.GetFileName(Path.GetFullPath(root)) + ".manifest.txt"), manifest);
    }

    private static int Hash(int seed, string kind, int i)
    {
        unchecked
        {
            int h = seed * 486187739 + i * 16777619;
            foreach (char c in kind)
            {
                h = (h ^ c) * 16777619;
            }

            return h;
        }
    }

    private static long Many(string root, string dir, int count, Options o, string ext, Func<Random, int, byte[]> make)
    {
        long total = 0;
        string d = Path.Combine(root, dir);
        Directory.CreateDirectory(d);
        for (int i = 0; i < count; i++)
        {
            var rng = new Random(Hash(o.Seed, dir, i));
            // Spread files over subfolders so directory listings stay small.
            string sub = Path.Combine(d, $"d{i / 100:D3}");
            Directory.CreateDirectory(sub);
            byte[] b = make(rng, i);
            File.WriteAllBytes(Path.Combine(sub, $"{Word(rng)}-{i:D5}{ext}"), b);
            total += b.Length;
        }

        return total;
    }

    // Word k of the vocabulary: syllables chosen by the digits of k, so the vocabulary is the same everywhere.
    private static string Word(Random rng) => WordAt((int)(6000 * Math.Pow(rng.NextDouble(), 2.5)));

    private static string WordAt(int k)
    {
        var sb = new StringBuilder();
        int v = k + 7;
        int parts = 1 + (k % 3);
        for (int p = 0; p <= parts; p++)
        {
            sb.Append(Syllables[v % Syllables.Length]);
            v = v / Syllables.Length + p * 13 + 5;
        }

        return sb.ToString();
    }

    private static string Sentence(Random rng)
    {
        int n = 6 + rng.Next(14);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            string w = rng.Next(60) == 0 ? Accented[rng.Next(Accented.Length)] : Word(rng);
            if (i == 0)
            {
                w = char.ToUpperInvariant(w[0]) + w[1..];
            }
            else
            {
                sb.Append(rng.Next(14) == 0 ? ", " : " ");
            }

            sb.Append(w);
        }

        return sb.Append(rng.Next(12) == 0 ? "? " : ". ").ToString();
    }

    private static string Paragraph(Random rng)
    {
        int n = 2 + rng.Next(5);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append(Sentence(rng));
        }

        return sb.ToString().TrimEnd();
    }

    private static string PlainText(Random rng, int size, int index)
    {
        var sb = new StringBuilder();
        string nl = index % 7 == 0 ? "\r\n" : "\n";
        while (sb.Length < size)
        {
            sb.Append(Paragraph(rng)).Append(nl).Append(nl);
        }

        return sb.ToString();
    }

    private static string Markdown(Random rng, int size)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Title(rng)).Append("\n\n");
        while (sb.Length < size)
        {
            switch (rng.Next(10))
            {
                case 0:
                    sb.Append("## ").Append(Title(rng)).Append("\n\n");
                    break;
                case 1:
                    sb.Append("### ").Append(Title(rng)).Append("\n\n");
                    break;
                case 2:
                    for (int i = 0, k = 2 + rng.Next(5); i < k; i++)
                    {
                        sb.Append("- ").Append(Sentence(rng).TrimEnd()).Append('\n');
                    }

                    sb.Append('\n');
                    break;
                case 3:
                    sb.Append("| name | value | note |\n|---|---|---|\n");
                    for (int i = 0, k = 3 + rng.Next(8); i < k; i++)
                    {
                        sb.Append("| ").Append(Word(rng)).Append(" | ").Append(rng.Next(10000)).Append(" | ").Append(Word(rng)).Append(" |\n");
                    }

                    sb.Append('\n');
                    break;
                case 4:
                    sb.Append("```\n");
                    for (int i = 0, k = 2 + rng.Next(8); i < k; i++)
                    {
                        sb.Append(Word(rng)).Append(" = ").Append(rng.Next(1000)).Append(";\n");
                    }

                    sb.Append("```\n\n");
                    break;
                default:
                    sb.Append(Paragraph(rng)).Append("\n\n");
                    break;
            }
        }

        return sb.ToString();
    }

    private static string Title(Random rng) => char.ToUpperInvariant(Word(rng)[0]) + Word(rng)[1..] + " " + Word(rng);

    private static string Html(Random rng, int size)
    {
        var sb = new StringBuilder("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(Title(rng))
            .Append("</title><style>p { color: red; } .x { display:none }</style><script>var needleinscript = 1; function f() { return '<p>no</p>'; }</script></head><body>\n<nav><a href=\"/\">home</a></nav>\n");
        while (sb.Length < size)
        {
            switch (rng.Next(8))
            {
                case 0:
                    sb.Append("<h2>").Append(Title(rng)).Append("</h2>\n");
                    break;
                case 1:
                    sb.Append("<h3>").Append(Title(rng)).Append("</h3>\n");
                    break;
                case 2:
                    sb.Append("<ul>");
                    for (int i = 0, k = 2 + rng.Next(5); i < k; i++)
                    {
                        sb.Append("<li>").Append(Sentence(rng).TrimEnd()).Append("</li>");
                    }

                    sb.Append("</ul>\n");
                    break;
                case 3:
                    sb.Append("<table><tr><th>name</th><th>value</th></tr>");
                    for (int i = 0, k = 3 + rng.Next(6); i < k; i++)
                    {
                        sb.Append("<tr><td>").Append(Word(rng)).Append("</td><td>").Append(rng.Next(10000)).Append("</td></tr>");
                    }

                    sb.Append("</table>\n");
                    break;
                case 4:
                    sb.Append("<p>").Append(Sentence(rng)).Append("<br>").Append(Sentence(rng)).Append(" &amp; <b>").Append(Word(rng)).Append("</b> &eacute;</p>\n");
                    break;
                default:
                    sb.Append("<p>").Append(Paragraph(rng)).Append("</p>\n");
                    break;
            }
        }

        return sb.Append("</body></html>").ToString();
    }

    private static byte[] Pdf(Random rng, int pages, int index)
    {
        using var doc = PdfDocument.CreateEmpty();
        PdfCreatedFont font = doc.AddStandardFont("Helvetica");
        PdfCreatedFont bold = doc.AddStandardFont("Helvetica-Bold");
        string running = Title(rng);
        for (int p = 0; p < pages; p++)
        {
            PdfPage page = doc.InsertBlankPage(p, 612, 792);
            using PdfPageContentBuilder b = page.BeginContent();
            b.SetFillGray(0);
            b.SetFont(font, 8);
            b.DrawText(running, 72, 760);
            b.DrawText("Page " + (p + 1).ToString(CultureInfo.InvariantCulture), 300, 36);
            b.SetFont(bold, 16);
            b.DrawText(Title(rng), 72, 710);
            b.SetFont(font, 11);
            double y = 690;
            while (y > 120)
            {
                string para = Paragraph(rng);
                string left = b.DrawTextBox(para, 72, y, 468, 90, PdfTextAlignment.Left);
                _ = left;
                y -= 100;
            }

            b.Commit();
        }

        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16] });
        return ms.ToArray();
    }

    private static byte[] Zip(Random rng, Options o, int entries, int depth)
    {
        using var ms = new MemoryStream();
        var builder = Archive.Create(ms, new ArchiveCreateOptions { Format = ArchiveFormat.Zip, Normalize = true });
        for (int i = 0; i < entries; i++)
        {
            int kind = rng.Next(4);
            string name;
            byte[] data;
            switch (kind)
            {
                case 0:
                    name = $"notes/{Word(rng)}-{i}.txt";
                    data = Encoding.UTF8.GetBytes(PlainText(rng, 4000 + rng.Next(30000), i));
                    break;
                case 1:
                    name = $"docs/{Word(rng)}-{i}.md";
                    data = Encoding.UTF8.GetBytes(Markdown(rng, 3000 + rng.Next(20000)));
                    break;
                case 2:
                    name = $"site/{Word(rng)}-{i}.html";
                    data = Encoding.UTF8.GetBytes(Html(rng, 3000 + rng.Next(20000)));
                    break;
                default:
                    name = $"data/{Word(rng)}-{i}.bin";
                    data = new byte[500 + rng.Next(5000)];
                    rng.NextBytes(data);
                    data[0] = 0;
                    break;
            }

            builder.Add(new ArchiveEntry { Name = name, Size = data.Length }, data);
        }

        if (depth > 0)
        {
            byte[] inner = Zip(rng, o, 4 + rng.Next(8), depth - 1);
            builder.Add(new ArchiveEntry { Name = "inner/nested.zip", Size = inner.Length }, inner);
        }

        builder.Save();
        return ms.ToArray();
    }

    // A few files that exercise the odd corners of reading: encodings, line ends, a tar.gz, an empty file.
    private static long Odd(string root, Options o)
    {
        string d = Path.Combine(root, "odd");
        Directory.CreateDirectory(d);
        var rng = new Random(Hash(o.Seed, "odd", 0));
        long total = 0;
        void Put(string name, byte[] b)
        {
            File.WriteAllBytes(Path.Combine(d, name), b);
            total += b.Length;
        }

        string text = PlainText(rng, 6000, 1);
        Put("utf16.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]);
        Put("latin1.txt", Encoding.Latin1.GetBytes("caf\u00e9 cr\u00e8me br\u00fbl\u00e9e " + text));
        Put("bom.md", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Markdown(rng, 4000))]);
        Put("empty.txt", []);
        Put("README", Encoding.UTF8.GetBytes(Paragraph(rng) + "\n"));
        Put("one-long-line.txt", Encoding.UTF8.GetBytes(string.Join(' ', Enumerable.Range(0, 6000).Select(_ => Word(rng)))));
        Put("cjk.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("日本語のテキストを検索します。全文検索エンジンは文書を索引にします。", 120))));
        Put("ignored.tmp", Encoding.UTF8.GetBytes("needleignored"));
        File.WriteAllText(Path.Combine(d, ".gitignore"), "skipped-*\n");
        Put("skipped-by-gitignore.txt", Encoding.UTF8.GetBytes("needleskipped"));
        return total;
    }

    private static long Corrupt(string root, Options o, int count, List<string> manifest)
    {
        string d = Path.Combine(root, "corrupt");
        Directory.CreateDirectory(d);
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            var rng = new Random(Hash(o.Seed, "corrupt", i));
            string ext = (i % 4) switch { 0 => ".pdf", 1 => ".zip", 2 => ".png", _ => ".html" };
            string path = Path.Combine(d, $"bad-{i:D3}{ext}");
            byte[] b;
            if (ext == ".html")
            {
                // The HTML parser accepts almost anything; a page that nests thousands of elements hits its limit instead.
                b = Encoding.UTF8.GetBytes("<div>" + string.Concat(Enumerable.Repeat("<b><i>", 3000)) + "unclosed " + Paragraph(rng));
                manifest.Add(Path.GetRelativePath(root, path).Replace('\\', '/') + "\terror");
            }
            else
            {
                b = new byte[2000 + rng.Next(8000)];
                rng.NextBytes(b);
                manifest.Add(Path.GetRelativePath(root, path).Replace('\\', '/') + "\terror");
            }

            File.WriteAllBytes(path, b);
            total += b.Length;
        }

        // Truncated copies of valid files: the reader may repair them or refuse them; both are acceptable, a crash or a hang is not.
        byte[] pdf = Pdf(new Random(Hash(o.Seed, "trunc", 0)), 4, 0);
        File.WriteAllBytes(Path.Combine(d, "truncated.pdf"), pdf[..(pdf.Length / 2)]);
        byte[] zip = Zip(new Random(Hash(o.Seed, "trunc", 1)), o, 10, 0);
        File.WriteAllBytes(Path.Combine(d, "truncated.zip"), zip[..(zip.Length * 2 / 3)]);
        total += pdf.Length / 2 + zip.Length * 2 / 3;
        return total;
    }

    private static void Scan(Random rng, string fontFile, string path)
    {
        const int width = 1100;
        const int height = 700;
        var px = new Rgba32[width * height];
        Array.Fill(px, new Rgba32(255, 255, 255, 255));
        Image image = Image.FromPixels<Rgba32>(px, width, height);
        var family = new FontCollection().Add(fontFile);
        Font f = family.CreateFont(30);
        float y = 50;
        for (int line = 0; line < 12; line++)
        {
            var sb = new StringBuilder();
            for (int w = 0; w < 7; w++)
            {
                sb.Append(Word(rng)).Append(' ');
            }

            image = image.DrawText(sb.ToString().TrimEnd(), new TextOptions(f) { Origin = new Vector2(60, y) }, new Rgba32(0, 0, 0, 255));
            y += 50;
        }

        image.Save(path);
    }

    /// <summary>Writes a small mixed folder for tests.</summary>
    public static void Small(string root, int seed = 7, string? font = null) =>
        Generate(root, new Options { Seed = seed, Files = 120, TargetMiB = 0, Font = font, Scans = font is null ? 0 : 2, Scale = 0.1 });

    internal static string ManifestPathFor(string root) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, Path.GetFileName(Path.GetFullPath(root)) + ".manifest.txt");
}
