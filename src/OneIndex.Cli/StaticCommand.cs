using OneIndex.Files;
using OneIndex.StaticIndexes;

namespace OneIndex.Cli;

internal static class StaticCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var a = new Arguments(args, ["--chunk-kb"], []);
        a.Expect(2, "oneindex static build <index> <out> [--chunk-kb N]");
        string index = Path.GetFullPath(a.Positionals[0]);
        string outDir = Path.GetFullPath(a.Positionals[1]);
        if (!Directory.Exists(index))
        {
            error.WriteLine($"oneindex: the index '{index}' does not exist.");
            return 1;
        }

        int chunkKb = a.GetInt("--chunk-kb", 16, 1, 65536);
        StaticManifest m = StaticIndexBuilder.Build<FilePassage>(index, outDir, new StaticBuildOptions { ChunkBytes = chunkKb * 1024 }, ct);
        long bytes = m.Files.Sum(static f => f.Size);
        output.WriteLine($"Static index: {m.Files.Count} files, {bytes:N0} bytes in chunks of {chunkKb} KiB, written to {outDir}");
        output.WriteLine("Serve the folder with any static file server; open it with StaticIndex.OpenAsync over an HttpStaticSource.");
        return 0;
    }
}
