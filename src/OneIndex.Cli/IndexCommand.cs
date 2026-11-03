using OneIndex.Files;
using OneIndex.Files.Ocr;
using OneIndex.Models;
using OneOcr;

namespace OneIndex.Cli;

internal static class IndexCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var a = new Arguments(args, ["--index"], ["--watch", "--embed", "--ocr"]);
        a.Expect(1, "oneindex index <folder> [--index <dir>] [--watch] [--embed] [--ocr]");
        string root = Path.GetFullPath(a.Positionals[0]);
        if (!Directory.Exists(root))
        {
            error.WriteLine($"oneindex: the folder '{root}' does not exist.");
            return 1;
        }

        string indexPath = Path.GetFullPath(a.Get("--index") ?? Path.Combine(root, ".oneindex"));
        BgeSmallEmbedder? embedder = a.Has("--embed") ? BgeSmallEmbedder.Load() : null;
        FolderIndexOptions options = Options(a.Has("--ocr"), indexPath, root);
        using SearchIndex<FilePassage> index = FolderIndexer.OpenIndex(indexPath, embedder);

        var progress = new ConsoleProgress(output);
        FolderIndexResult r = FolderIndexer.Run(index, root, options, progress, ct);
        progress.Finish();
        output.WriteLine($"{r.FilesSeen} files: {r.Added} added, {r.Updated} updated, {r.Renamed} renamed, {r.Removed} removed, {r.Unchanged} unchanged, {r.Failed} unreadable; {r.Passages} passages in {r.Elapsed.TotalSeconds:0.0} s. Index: {indexPath}");

        if (a.Has("--watch"))
        {
            using var watcher = new FolderWatcher(index, root, options);
            watcher.Scanned += s => output.WriteLine($"rescan: {s.Added} added, {s.Updated} updated, {s.Removed} removed, {s.Failed} unreadable");
            watcher.Failed += e => error.WriteLine($"oneindex: watch: {e.Message}");
            watcher.Start();
            output.WriteLine("Watching for changes; press Ctrl+C to stop.");
            try
            {
                Task.Delay(Timeout.Infinite, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C is the normal way out of watching.
            }

            watcher.WaitForIdleAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return 0;
    }

    private static FolderIndexOptions Options(bool ocr, string indexPath, string root)
    {
        // The index may live inside the folder; never index it.
        string rel = Path.GetRelativePath(root, indexPath);
        IReadOnlyList<string> exclude = rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? [] : [rel];
        if (!ocr)
        {
            return new FolderIndexOptions { ExcludeDirectories = exclude };
        }

        OcrEngine engine = OcrEngine.LoadBundled();
        return new FolderIndexOptions
        {
            ExcludeDirectories = exclude,
            FileTimeout = TimeSpan.FromMinutes(30),
            Extractors = [new TextExtractor(), new HtmlExtractor(), new OcrPdfExtractor(engine), new OcrImageExtractor(engine)],
        };
    }

    private sealed class ConsoleProgress(TextWriter output) : IProgress<FolderProgress>
    {
        private readonly bool _tty = !Console.IsOutputRedirected && ReferenceEquals(output, Console.Out);
        private long _lastReported;

        public void Report(FolderProgress p)
        {
            if (_tty)
            {
                string name = p.Current is null ? string.Empty : Path.GetFileName(p.Current);
                output.Write($"\r{p.FilesIndexed}/{p.FilesToIndex} files, {p.PassagesWritten} passages  {name,-40}".PadRight(Math.Max(0, Console.WindowWidth - 1)));
            }
            else if (p.FilesIndexed - _lastReported >= 100)
            {
                _lastReported = p.FilesIndexed;
                output.WriteLine($"{p.FilesIndexed}/{p.FilesToIndex} files, {p.PassagesWritten} passages");
            }
        }

        public void Finish()
        {
            if (_tty)
            {
                output.WriteLine();
            }
        }
    }
}
