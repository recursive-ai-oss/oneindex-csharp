using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneIndex.Files;
using OneIndex.Files.Ocr;
using OneIndex.Search;

namespace OneIndex.Files.Tool;

/// <summary>
/// Commands for building and checking folder indexes:
/// <c>gen</c> writes a corpus; <c>index</c> indexes a folder; <c>snapshot</c> dumps an index in a comparable form; <c>campaign</c> kills
/// indexing at random points and compares the result with an uninterrupted run; <c>cite-check</c> re-reads cited pages; <c>throughput</c>
/// times the first index per format.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: oneindex-files-tool gen|index|snapshot|campaign|cite-check|throughput [--option value]...");
            return 2;
        }

        var opt = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                string key = args[i][2..];
                opt[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            }
        }

        try
        {
            return args[0] switch
            {
                "gen" => Gen(opt),
                "index" => Index(opt),
                "snapshot" => Snapshot(opt),
                "campaign" => Campaign(opt),
                "cite-check" => CiteCheck(opt),
                "throughput" => Throughput(opt),
                "errors" => Errors(opt),
                _ => Fail("unknown command " + args[0]),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static int Fail(string m)
    {
        Console.Error.WriteLine(m);
        return 2;
    }

    private static string Req(Dictionary<string, string> o, string k) => o.TryGetValue(k, out string? v) ? v : throw new ArgumentException("missing --" + k);

    private static int Int(Dictionary<string, string> o, string k, int d) => o.TryGetValue(k, out string? v) ? int.Parse(v, CultureInfo.InvariantCulture) : d;

    private static int Gen(Dictionary<string, string> o)
    {
        var options = new Corpus.Options
        {
            Seed = Int(o, "seed", 1),
            Files = Int(o, "files", 6000),
            TargetMiB = Int(o, "mib", 1100),
            Font = o.GetValueOrDefault("font"),
            Scans = Int(o, "scans", 12),
            Scale = o.TryGetValue("scale", out string? s) ? double.Parse(s, CultureInfo.InvariantCulture) : 1.0,
        };
        Corpus.Generate(Req(o, "out"), options);
        Console.WriteLine("generated " + Req(o, "out"));
        return 0;
    }

    private static FolderIndexOptions OptionsFrom(Dictionary<string, string> o, string? indexDir)
    {
        var extractors = new List<IFileExtractor> { new TextExtractor(), new HtmlExtractor(), new PdfExtractor() };
        if (o.ContainsKey("ocr"))
        {
            extractors.Add(new OcrImageExtractor());
        }

        return new FolderIndexOptions
        {
            Extractors = extractors,
            MaxDegreeOfParallelism = Int(o, "parallel", 1),
            CommitEveryFiles = Int(o, "commit-files", 200),
            ExcludeDirectories = indexDir is null ? [] : [indexDir],
            FileTimeout = TimeSpan.FromSeconds(Int(o, "timeout", 300)),
        };
    }

    private sealed class Printer(bool quiet) : IProgress<FolderProgress>
    {
        private long _last;

        public void Report(FolderProgress p)
        {
            if (quiet || p.FilesIndexed - _last < 500)
            {
                return;
            }

            _last = p.FilesIndexed;
            Console.Error.WriteLine($"  indexed {p.FilesIndexed}/{p.FilesToIndex}, unchanged {p.FilesUnchanged}, failed {p.FilesFailed}, passages {p.PassagesWritten}, commits {p.Commits}");
        }
    }

    private static int Index(Dictionary<string, string> o)
    {
        string root = Req(o, "root");
        string dir = Req(o, "index");
        using SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(dir);
        FolderIndexResult r = FolderIndexer.Run(index, root, OptionsFrom(o, Path.GetFullPath(dir)), new Printer(o.ContainsKey("quiet")));
        Console.WriteLine($"seen {r.FilesSeen} added {r.Added} updated {r.Updated} renamed {r.Renamed} removed {r.Removed} unchanged {r.Unchanged} failed {r.Failed} passages {r.Passages} MiB {r.BytesRead >> 20} commits {r.Commits} seconds {r.Elapsed.TotalSeconds:F1}");
        return 0;
    }

    // All documents of the index as sorted lines: identifier, kind, format, page, range, a hash of the text and of the boxes.
    private static List<string> Dump(string dir, out int files, out int errors, out int passages)
    {
        using SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(dir);
        var lines = new List<string>();
        files = errors = passages = 0;
        foreach (string kind in new[] { "file", "error", "passage" })
        {
            SortCursor? cursor = null;
            while (true)
            {
                SearchQuery<FilePassage> q = index.Search().Where(p => p.Kind == kind).OrderBy(p => p.Kind);
                if (cursor is not null)
                {
                    q = q.SearchAfter(cursor);
                }

                SearchResults<FilePassage> page = q.Take(1000).Execute();
                foreach (SearchHit<FilePassage> h in page)
                {
                    FilePassage p = h.Document;
                    switch (kind)
                    {
                        case "file":
                            files++;
                            break;
                        case "error":
                            errors++;
                            break;
                        default:
                            passages++;
                            break;
                    }

                    lines.Add(string.Join('\t', p.Id, p.Kind, p.Format, p.Page, p.Start, p.End, p.PassageCount, p.Size, p.Hash, Sha(p.Text ?? p.Error ?? string.Empty), Sha(p.Boxes), Sha(p.Heading)));
                }

                if (page.Count < 1000 || page.NextCursor is null)
                {
                    break;
                }

                cursor = page.NextCursor;
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private static int Errors(Dictionary<string, string> o)
    {
        using SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(Req(o, "index"));
        foreach (SearchHit<FilePassage> h in index.Errors().Take(1000).Execute())
        {
            Console.WriteLine($"{h.Document.FileId}\t{h.Document.Error}");
        }

        return 0;
    }

    private static int Snapshot(Dictionary<string, string> o)
    {
        List<string> lines = Dump(Req(o, "index"), out int files, out int errors, out int passages);
        File.WriteAllLines(Req(o, "out"), lines);
        Console.WriteLine($"file records {files}, error records {errors}, passages {passages}");
        return 0;
    }

    private static int Campaign(Dictionary<string, string> o)
    {
        string root = Path.GetFullPath(Req(o, "root"));
        string work = Path.GetFullPath(Req(o, "work"));
        int kills = Int(o, "kills", 50);
        int seed = Int(o, "seed", 1);
        Directory.CreateDirectory(work);
        string clean = Path.Combine(work, "clean");
        string killed = Path.Combine(work, "killed");
        if (Directory.Exists(clean) || Directory.Exists(killed))
        {
            throw new InvalidOperationException("The work directory already holds indexes; use a fresh one.");
        }

        var sw = Stopwatch.StartNew();
        using (SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(clean))
        {
            FolderIndexResult r = FolderIndexer.Run(index, root, OptionsFrom(o, null), new Printer(true));
            Console.WriteLine($"clean run: {r.Added} files, {r.Passages} passages, {r.Failed} failed, {r.Elapsed.TotalSeconds:F1} s");
        }

        double cleanSeconds = sw.Elapsed.TotalSeconds;
        int minMs = Int(o, "min-ms", 1500);
        int maxMs = Int(o, "max-ms", Math.Max(3000, (int)(cleanSeconds * 1000 / Math.Max(1, kills / 6))));
        Console.WriteLine($"kill delays {minMs}..{maxMs} ms, {kills} kills, seed {seed}");
        var rng = new Random(seed);
        int exited = 0;
        int killedRuns = 0;
        int previous = 0;
        string dll = Environment.GetCommandLineArgs()[0];
        for (int k = 0; k < kills; k++)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add("index");
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(root);
            psi.ArgumentList.Add("--index");
            psi.ArgumentList.Add(killed);
            psi.ArgumentList.Add("--commit-files");
            psi.ArgumentList.Add(Int(o, "kill-commit-files", 25).ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--quiet");
            if (o.ContainsKey("ocr"))
            {
                psi.ArgumentList.Add("--ocr");
            }

            using Process child = Process.Start(psi)!;
            child.OutputDataReceived += (_, _) => { };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            int delay = rng.Next(minMs, maxMs + 1);
            if (child.WaitForExit(delay))
            {
                exited++;
                Console.WriteLine($"  kill {k + 1}: the child finished on its own after {delay} ms (exit {child.ExitCode})");
            }
            else
            {
                child.Kill(true);
                child.WaitForExit();
                killedRuns++;
            }

            // The directory must open, and no commit may be lost: committed files only ever grow while the folder does not change.
            using SearchIndex<FilePassage> probe = FolderIndexer.OpenIndex(killed);
            long fileRecords = probe.Search().Where(p => p.Kind == "file").Take(0).CountExactly().Execute().TotalHits
                + probe.Search().Where(p => p.Kind == "error").Take(0).CountExactly().Execute().TotalHits;
            if (fileRecords < previous)
            {
                throw new InvalidOperationException($"After kill {k + 1} the index holds {fileRecords} file records, fewer than the {previous} before: a commit was lost.");
            }

            previous = (int)fileRecords;
            Console.WriteLine($"  kill {k + 1}: after {delay} ms, reopened, {fileRecords} file records");
        }

        using (SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(killed))
        {
            FolderIndexResult r = FolderIndexer.Run(index, root, OptionsFrom(o, null), new Printer(true));
            Console.WriteLine($"final run: added {r.Added}, unchanged {r.Unchanged}, failed {r.Failed}, {r.Elapsed.TotalSeconds:F1} s");
        }

        List<string> a = Dump(clean, out int cf, out int ce, out int cp);
        List<string> b = Dump(killed, out int kf, out int ke, out int kp);
        Console.WriteLine($"clean: {cf} file records, {ce} error records, {cp} passages; resumed: {kf}, {ke}, {kp}");
        int diffs = 0;
        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        foreach (string line in b)
        {
            if (!setA.Remove(line) && diffs++ < 10)
            {
                Console.WriteLine("  only in resumed: " + line);
            }
        }

        foreach (string line in setA)
        {
            if (diffs++ < 10)
            {
                Console.WriteLine("  only in clean: " + line);
            }
        }

        // Every file once, every file's passages contiguous, no orphan passages.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var recordsById = new Dictionary<string, int>(StringComparer.Ordinal);
        int duplicateRecords = 0;
        foreach (string line in b)
        {
            string[] f = line.Split('\t');
            if (f[1] == "passage")
            {
                string fileId = f[0][..f[0].LastIndexOf('#')];
                counts[fileId] = counts.GetValueOrDefault(fileId) + 1;
            }
            else if (!recordsById.TryAdd(f[0], int.Parse(f[6], CultureInfo.InvariantCulture)))
            {
                duplicateRecords++;
            }
        }

        int wrongCount = 0;
        int orphans = 0;
        foreach ((string rid, int expected) in recordsById)
        {
            string fileId = rid["file:".Length..];
            if (counts.GetValueOrDefault(fileId) != expected)
            {
                wrongCount++;
            }
        }

        foreach (string fileId in counts.Keys)
        {
            if (!recordsById.ContainsKey("file:" + fileId))
            {
                orphans++;
            }
        }

        int expectedErrors = 0;
        string manifest = Corpus.ManifestPathFor(root);
        if (File.Exists(manifest))
        {
            expectedErrors = File.ReadAllLines(manifest).Count(l => l.EndsWith("\terror", StringComparison.Ordinal));
        }

        Console.WriteLine($"kills delivered {killedRuns}, children finished early {exited}");
        Console.WriteLine($"differences between resumed and clean: {diffs}; duplicate file records: {duplicateRecords}; files whose passage count differs from the record: {wrongCount}; orphan passages: {orphans}; corrupt-on-purpose files expected as error documents: {expectedErrors}, error documents (top level): {ke}");
        bool ok = diffs == 0 && duplicateRecords == 0 && wrongCount == 0 && orphans == 0 && ke >= expectedErrors && killedRuns >= Math.Min(kills, 1);
        Console.WriteLine(ok ? "CAMPAIGN OK" : "CAMPAIGN FAILED");
        return ok ? 0 : 1;
    }

    private static int CiteCheck(Dictionary<string, string> o)
    {
        string root = Path.GetFullPath(Req(o, "root"));
        int sample = Int(o, "sample", 1000);
        int seed = Int(o, "seed", 1);
        using SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(Req(o, "index"));
        // Gather the identity of every passage, then take a seeded sample spread over the formats.
        var byFormat = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        SortCursor? cursor = null;
        while (true)
        {
            SearchQuery<FilePassage> q = index.Search().Where(p => p.Kind == "passage").OrderBy(p => p.Kind);
            if (cursor is not null)
            {
                q = q.SearchAfter(cursor);
            }

            SearchResults<FilePassage> page = q.Take(1000).Execute();
            foreach (SearchHit<FilePassage> h in page)
            {
                if (!byFormat.TryGetValue(h.Document.Format, out List<string>? ids))
                {
                    byFormat[h.Document.Format] = ids = [];
                }

                ids.Add(h.Document.Id);
            }

            if (page.Count < 1000 || page.NextCursor is null)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        var rng = new Random(seed);
        var chosen = new List<string>();
        int perFormat = Math.Max(1, sample / byFormat.Count);
        foreach ((string fmt, List<string> ids) in byFormat)
        {
            int take = Math.Min(ids.Count, perFormat);
            chosen.AddRange(ids.OrderBy(_ => rng.Next()).Take(take));
        }

        // Top up to the requested size from the biggest formats.
        var rest = byFormat.Values.SelectMany(v => v).Except(chosen).OrderBy(_ => rng.Next()).Take(Math.Max(0, sample - chosen.Count));
        chosen.AddRange(rest);
        chosen.Sort(StringComparer.Ordinal);

        var stats = new SortedDictionary<string, int[]>(StringComparer.Ordinal); // checked, matched, boxed
        var pdfOptions = OptionsFrom(o, null);
        int shown = 0;
        // Group by file so each source is extracted once.
        foreach (IGrouping<string, string> group in chosen.GroupBy(id => id[..id.LastIndexOf('#')]))
        {
            var cache = new Dictionary<int, string>();
            foreach (string id in group)
            {
                SearchResults<FilePassage> found = index.Search().Where(p => p.Id == id).Take(1).Execute();
                SearchHit<FilePassage>? hit = found.Count > 0 ? found[0] : null;
                if (hit is null)
                {
                    continue;
                }

                Citation c = hit.Cite();
                int[] st = stats.TryGetValue(c.Format, out int[]? existing) ? existing : stats[c.Format] = new int[4];
                st[0]++;
                try
                {
                    if (!cache.TryGetValue(c.Page, out string? pageText))
                    {
                        cache[c.Page] = pageText = c.ReadPageText(root, pdfOptions);
                    }

                    bool match = c.End <= pageText.Length && pageText.Substring(c.Start, c.End - c.Start) == c.Text;
                    if (match)
                    {
                        st[1]++;
                    }
                    else if (shown++ < 15)
                    {
                        Console.WriteLine($"  MISMATCH {c.FileId} page {c.Page} {c.Start}..{c.End}");
                    }

                    if (c.BoxUnits.Length > 0)
                    {
                        bool inside = c.Boxes.Count > 0 && c.Boxes.All(b => b.Right > b.Left && b.Bottom > b.Top && b.Left >= -1 && b.Top >= -1 && b.Right <= c.PageWidth + 1 && b.Bottom <= c.PageHeight + 1);
                        if (inside)
                        {
                            st[2]++;
                        }
                        else if (shown++ < 15)
                        {
                            Console.WriteLine($"  NO/BAD BOX {c.FileId} page {c.Page} boxes {c.Boxes.Count} page {c.PageWidth}x{c.PageHeight}");
                        }
                    }
                }
                catch (Exception e)
                {
                    st[3]++;
                    if (shown++ < 15)
                    {
                        Console.WriteLine($"  ERROR {c.FileId}: {e.GetType().Name}: {e.Message}");
                    }
                }
            }
        }

        int total = 0;
        int matched = 0;
        foreach ((string fmt, int[] st) in stats)
        {
            Console.WriteLine($"{fmt,-10} checked {st[0],5}  text equal {st[1],5}  boxes ok {st[2],5}  errors {st[3],3}");
            total += st[0];
            matched += st[1];
        }

        Console.WriteLine($"TOTAL checked {total} matched {matched}");
        return total == matched ? 0 : 1;
    }

    private static int Throughput(Dictionary<string, string> o)
    {
        string root = Path.GetFullPath(Req(o, "root"));
        string work = Path.GetFullPath(Req(o, "work"));
        Directory.CreateDirectory(work);
        Console.WriteLine($"{"folder",-10} {"files",7} {"MiB",8} {"seconds",8} {"files/s",9} {"MiB/s",8} {"passages",9}");
        foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string idx = Path.Combine(work, "t-" + Path.GetFileName(dir));
            if (Directory.Exists(idx))
            {
                Directory.Delete(idx, true);
            }

            using (SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(idx))
            {
                FolderIndexResult r = FolderIndexer.Run(index, dir, OptionsFrom(o, null));
                double s = Math.Max(r.Elapsed.TotalSeconds, 0.001);
                Console.WriteLine($"{Path.GetFileName(dir),-10} {r.FilesSeen,7} {r.BytesRead / 1048576.0,8:F0} {s,8:F1} {r.FilesSeen / s,9:F0} {r.BytesRead / 1048576.0 / s,8:F1} {r.Passages,9}");
            }

            Directory.Delete(idx, true);
        }

        Console.WriteLine($"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MiB");
        return 0;
    }
}
