using System.Security.Cryptography;
using System.Text;
using OneIndex.Files.Tool;
using OneIndex.Search;

namespace OneIndex.Files.Tests;

public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oneindex-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name) => System.IO.Path.Combine(Path, name.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

public static class Dumps
{
    // Every document as a comparable line; the order is the identifier's.
    public static List<string> Of(SearchIndex<FilePassage> index)
    {
        var lines = new List<string>();
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
                    string body = p.Text ?? p.Error ?? string.Empty;
                    lines.Add(string.Join('\t', p.Id, p.Kind, p.Format, p.Page, p.Start, p.End, p.PassageCount, p.Hash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..12], p.Boxes, p.Heading));
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
}

public class FolderIndexerTests : IDisposable
{
    private readonly TempFolder _t = new();

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private string Make(string rel, string content)
    {
        string p = _t.Sub("root/" + rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private string Root => _t.Sub("root");

    private static FolderIndexOptions Quick(int parallel = 1) => new() { MaxDegreeOfParallelism = parallel, CommitEveryFiles = 5 };

    [Fact]
    public void ASmallMixedFolderIsSearchableAndEveryPassageCitesItsSource()
    {
        Corpus.Small(Root);
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        var options = new FolderIndexOptions { CommitEveryFiles = 50 };
        FolderIndexResult r = FolderIndexer.Run(index, Root, options);
        Assert.True(r.Added > 100);
        Assert.True(r.Passages > 1000);

        // Files that the rules leave out are not there.
        Assert.Empty(index.Passages("needleskipped"));
        Assert.Empty(index.Passages("needleignored"));
        Assert.Empty(index.Passages("needleinscript"));

        // A search returns passages, and Distinct gives one hit per file.
        List<SearchHit<FilePassage>> all = index.Passages(null).Take(5000).ToList();
        string word = all.First(h => h.Document.Format == "text").Document.Text!.Split(' ')[3].Trim(',', '.', '?');
        SearchResults<FilePassage> hits = index.Passages(word).Distinct(p => p.FileId).Take(50).Execute();
        Assert.NotEmpty(hits);
        Assert.Equal(hits.Count, hits.Select(h => h.Document.FileId).Distinct().Count());

        // Archive entries, also inside an archive in an archive, are addressed with "!/".
        Assert.Contains(all, h => h.Document.FileId.Contains(".zip!/notes/", StringComparison.Ordinal));
        Assert.Contains(index.Passages(null).Where(p => p.Format == "markdown").Take(5000).ToList(), h => h.Document.FileId.Contains(".zip!/inner/nested.zip!/", StringComparison.Ordinal));

        // Corrupt files are error documents with a reason.
        List<SearchHit<FilePassage>> errors = index.Errors().Take(100).ToList();
        Assert.True(errors.Count >= 9);
        Assert.All(errors, e => Assert.False(string.IsNullOrEmpty(e.Document.Error)));

        // The cited range of the re-read page equals the passage, for a sample over all formats.
        int checkedCount = 0;
        int step = Math.Max(1, all.Count / 400);
        for (int i = 0; i < all.Count; i += step)
        {
            Citation c = all[i].Cite();
            Assert.Equal(c.Text, c.ReadSourceText(Root, options));
            if (c.Format == "pdf")
            {
                Assert.NotEmpty(c.Boxes);
                Assert.All(c.Boxes, b => Assert.True(b.Left >= 0 && b.Right <= c.PageWidth + 1 && b.Top >= 0 && b.Bottom <= c.PageHeight + 1 && b.Right > b.Left));
                Assert.Equal("pt", c.BoxUnits);
                Assert.True(c.Page >= 1);
            }

            checkedCount++;
        }

        Assert.True(checkedCount >= 300);
    }

    [Fact]
    public void ARescanAppliesExactlyTheChanges()
    {
        string a = Make("a.txt", "alpha text about apples.\n");
        Make("b.md", "# Bees\n\nBees make honey.\n");
        Make("c.html", "<p>charlie html page</p>");
        Make("d.txt", "delta stays the same.\n");
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        FolderIndexResult first = FolderIndexer.Run(index, Root, Quick());
        Assert.Equal(4, first.Added);
        Assert.Equal(0, first.Updated);

        FolderIndexResult none = FolderIndexer.Run(index, Root, Quick());
        Assert.Equal(4, none.Unchanged);
        Assert.Equal(0, none.Added + none.Updated + none.Removed + none.Renamed);
        Assert.Equal(0, none.BytesRead);

        // Modify a, rename b (same bytes), delete c, add e, touch d (new time, same bytes).
        File.WriteAllText(a, "alpha text about oranges now.\n");
        File.Move(_t.Sub("root/b.md"), _t.Sub("root/bees.md"));
        File.Delete(_t.Sub("root/c.html"));
        Make("e.txt", "echo is new.\n");
        File.SetLastWriteTimeUtc(_t.Sub("root/d.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        FolderIndexResult second = FolderIndexer.Run(index, Root, Quick());
        Assert.Equal(1, second.Added);
        Assert.Equal(1, second.Updated);
        Assert.Equal(1, second.Renamed);
        Assert.Equal(1, second.Removed);
        Assert.Equal(1, second.Unchanged);

        Assert.Empty(index.Passages("apples"));
        Assert.Single(index.Passages("oranges"));
        Assert.Empty(index.Passages("charlie"));
        SearchHit<FilePassage> bees = Assert.Single(index.Passages("honey"));
        Assert.Equal("/bees.md", bees.Document.FileId);
        Assert.Equal("/bees.md#0", bees.Document.Id);
        Assert.Equal("bees.md", bees.Document.FileName);
        Assert.Equal("Bees", bees.Document.Heading);
        Assert.Single(index.Passages("echo"));
        Assert.Single(index.Passages("delta"));
        Assert.Equal("# Bees\n\nBees make honey.", bees.Cite().ReadSourceText(Root));

        // After all this the index equals one built from scratch.
        using SearchIndex<FilePassage> fresh = SearchIndex.InMemory<FilePassage>();
        FolderIndexer.Run(fresh, Root, Quick());
        Assert.Equal(Dumps.Of(fresh), Dumps.Of(index));
    }

    [Fact]
    public void AFileThatShrinksToFewerPassagesLeavesNoStalePassages()
    {
        string big = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"Paragraph number {i} " + string.Concat(Enumerable.Repeat("filler words go here ", 30))));
        string p = Make("long.txt", big);
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        FolderIndexer.Run(index, Root, Quick());
        Assert.True(index.Passages(null).Where(x => x.FileId == "/long.txt").Take(500).ToList().Count > 5);
        File.WriteAllText(p, "Short now.\n");
        FolderIndexer.Run(index, Root, Quick());
        Assert.Single(index.Passages(null).Where(x => x.FileId == "/long.txt").Take(500).ToList());
        Assert.Equal(1, index.Search().Where(x => x.FileId == "/long.txt" && x.Kind == "file").CountExactly().Execute().TotalHits);
    }

    [Fact]
    public void AnInterruptedRunResumesToTheSameIndexAsAnUninterruptedOne()
    {
        Corpus.Small(Root, seed: 11);
        using SearchIndex<FilePassage> clean = SearchIndex.InMemory<FilePassage>();
        FolderIndexer.Run(clean, Root, Quick());
        List<string> expected = Dumps.Of(clean);

        string dir = _t.Sub("idx");
        for (int cutAfter = 12; cutAfter < 260; cutAfter += 100)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            using (SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(dir))
            {
                using var cts = new CancellationTokenSource();
                int seen = 0;
                var progress = new Progress0(p =>
                {
                    if (++seen == cutAfter)
                    {
                        cts.Cancel();
                    }
                });
                Assert.ThrowsAny<OperationCanceledException>(() => FolderIndexer.Run(index, Root, Quick(), progress, cts.Token));
            }

            // Reopen (as after a crash) and finish; nothing is read twice that was committed.
            using (SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(dir))
            {
                long committed = index.Search().Where(p => p.Kind == "file" || p.Kind == "error").CountExactly().Take(0).Execute().TotalHits;
                Assert.True(committed > 0);
                FolderIndexResult r = FolderIndexer.Run(index, Root, Quick());
                // What was committed is not read again: every file is either unchanged or newly added, and some were committed.
                Assert.True(r.Unchanged > 0, $"{r.Unchanged} unchanged of {committed} committed");
                Assert.Equal(r.FilesSeen, r.Unchanged + r.Added);
                Assert.Equal(expected, Dumps.Of(index));
            }
        }
    }

    private sealed class Progress0(Action<FolderProgress> a) : IProgress<FolderProgress>
    {
        public void Report(FolderProgress value) => a(value);
    }

    [Fact]
    public void ParallelExtractionGivesTheSameIndex()
    {
        Corpus.Small(Root, seed: 3);
        using SearchIndex<FilePassage> one = SearchIndex.InMemory<FilePassage>();
        using SearchIndex<FilePassage> four = SearchIndex.InMemory<FilePassage>();
        FolderIndexer.Run(one, Root, Quick(1));
        FolderIndexer.Run(four, Root, Quick(4));
        Assert.Equal(Dumps.Of(one), Dumps.Of(four));
    }

    private sealed class HangingExtractor : IFileExtractor
    {
        public string Name => "hang-1";

        public bool Accepts(string fileName) => fileName.EndsWith(".hang", StringComparison.Ordinal);

        public ExtractedFile Extract(Stream content, string fileName, CancellationToken cancellationToken)
        {
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            return new ExtractedFile("hang", []);
        }
    }

    [Fact]
    public void AFileThatTakesTooLongBecomesAnErrorDocumentAndTheRunGoesOn()
    {
        Make("slow.hang", "x");
        Make("fine.txt", "this one is fine\n");
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        var options = new FolderIndexOptions { Extractors = [new HangingExtractor(), new TextExtractor()], FileTimeout = TimeSpan.FromMilliseconds(300) };
        FolderIndexResult r = FolderIndexer.Run(index, Root, options);
        Assert.Equal(1, r.Failed);
        SearchHit<FilePassage> error = Assert.Single(index.Errors().Take(10).ToList());
        Assert.Equal("/slow.hang", error.Document.FileId);
        Assert.Contains("time limit", error.Document.Error, StringComparison.Ordinal);
        Assert.Single(index.Passages("fine"));

        // The error is not retried while the file is as it was.
        Assert.Equal(2, FolderIndexer.Run(index, Root, options).Unchanged);
    }

    [Fact]
    public void ALargeFileOverTheLimitIsAnErrorDocument()
    {
        Make("big.txt", new string('a', 5000));
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        FolderIndexer.Run(index, Root, new FolderIndexOptions { MaxFileBytes = 1000 });
        Assert.Contains("byte limit", Assert.Single(index.Errors().Take(5).ToList()).Document.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWatcherFollowsAddsEditsRenamesAndDeletes()
    {
        Make("one.txt", "first document about kiwis.\n");
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        using var watcher = new FolderWatcher(index, Root, Quick(), debounce: TimeSpan.FromMilliseconds(100), rescanEvery: TimeSpan.FromMinutes(5));
        watcher.Start();
        Wait(watcher);
        Assert.Single(index.Passages("kiwis"));

        Make("two.txt", "second document about mangoes.\n");
        Wait(watcher, () => index.Passages("mangoes").Count() == 1);
        File.WriteAllText(_t.Sub("root/one.txt"), "first document about papayas.\n");
        Wait(watcher, () => index.Passages("papayas").Count() == 1);
        Assert.Empty(index.Passages("kiwis"));
        File.Move(_t.Sub("root/two.txt"), _t.Sub("root/renamed.txt"));
        Wait(watcher, () => index.Passages(null).Where(p => p.FileId == "/renamed.txt").Count() == 1);
        File.Delete(_t.Sub("root/one.txt"));
        Wait(watcher, () => index.Passages("papayas").Count() == 0);
        Assert.Single(index.Passages("mangoes"));
    }

    private static void Wait(FolderWatcher w, Func<bool>? until = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            w.WaitForIdleAsync().GetAwaiter().GetResult();
            if (until is null || until())
            {
                return;
            }

            Thread.Sleep(50);
        }

        Assert.Fail("the watcher did not reach the expected state in 30 seconds");
    }

    [Fact]
    public void IgnoreFilesAndDefaultsKeepFilesOut()
    {
        Make(".gitignore", "secret/\n*.bak\n");
        Make("secret/plan.txt", "needle in secret\n");
        Make("notes.bak", "needle in backup\n");
        Make("sub/.oneindexignore", "private.txt\n");
        Make("sub/private.txt", "needle in private\n");
        Make("sub/public.txt", "needle in public\n");
        Make("node_modules/lib/readme.txt", "needle in module\n");
        using SearchIndex<FilePassage> index = SearchIndex.InMemory<FilePassage>();
        FolderIndexer.Run(index, Root, Quick());
        Assert.Equal(["/.gitignore#0", "/sub/.oneindexignore#0", "/sub/public.txt#0"], index.Passages(null).Take(20).ToList().Select(h => h.Document.Id).Order(StringComparer.Ordinal));
    }
}
