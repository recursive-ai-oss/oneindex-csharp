using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OneIndex.Files;
using OneIndex.StaticIndexes;

namespace OneIndex.Cli.Tests;

/// <summary>Packs the tool, installs it from the local pack and drives it as a user would.</summary>
public sealed class CliToolTests : IDisposable
{
    private static readonly string RepoRoot = FindRoot();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "oneindex-cli-" + Guid.NewGuid().ToString("N"));

    private static string FindRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, true);
        }
    }

    private static (int Code, string Out, string Err) Run(string exe, string args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? Environment.CurrentDirectory };
        using Process p = Process.Start(psi)!;
        Task<string> o = p.StandardOutput.ReadToEndAsync();
        Task<string> e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result, e.Result);
    }

    [Fact]
    public void IndexQueryAndStaticBuildFromThePackedTool()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string feed = Path.Combine(_work, "feed");
        string tools = Path.Combine(_work, "tools");
        Directory.CreateDirectory(feed);
        (int pack, string po, string pe) = Run(dotnet, $"pack \"{Path.Combine(RepoRoot, "src", "OneIndex.Cli", "OneIndex.Cli.csproj")}\" -c Release -o \"{feed}\" -p:EnablePackageValidation=false -v quiet");
        Assert.True(pack == 0, po + pe);
        string nupkg = Directory.EnumerateFiles(feed, "RecursiveAI.OneIndex.Cli.*.nupkg").Single();
        string version = Path.GetFileNameWithoutExtension(nupkg)["RecursiveAI.OneIndex.Cli.".Length..];
        (int install, string io, string ie) = Run(dotnet, $"tool install RecursiveAI.OneIndex.Cli --tool-path \"{tools}\" --add-source \"{feed}\" --version {version}");
        Assert.True(install == 0, io + ie);
        string exe = Path.Combine(tools, OperatingSystem.IsWindows() ? "oneindex.exe" : "oneindex");

        string docs = Path.Combine(_work, "docs");
        Directory.CreateDirectory(Path.Combine(docs, "sub"));
        File.WriteAllText(Path.Combine(docs, "fox.md"), "# Animals\n\nThe quick brown fox jumps over the lazy dog.\n");
        File.WriteAllText(Path.Combine(docs, "sub", "invoice.txt"), "Invoice 42 is overdue for the customer Acme Corporation.\n");
        File.WriteAllText(Path.Combine(docs, "page.html"), "<html><body><h1>Garden</h1><p>Tulips bloom in the garden every spring.</p></body></html>");
        string index = Path.Combine(_work, "index");

        (int c, string o, string e) = Run(exe, $"index \"{docs}\" --index \"{index}\"");
        Assert.True(c == 0, o + e);
        Assert.Contains("3 files", o, StringComparison.Ordinal);

        (c, o, e) = Run(exe, $"query \"{index}\" fox");
        Assert.Equal(0, c);
        Assert.Contains("fox.md", o, StringComparison.Ordinal);
        Assert.DoesNotContain("invoice.txt", o, StringComparison.Ordinal);

        (c, o, e) = Run(exe, $"query \"{index}\" overdue --json --filter \"Format = text\"");
        Assert.Equal(0, c);
        using (JsonDocument json = JsonDocument.Parse(o))
        {
            JsonElement hit = json.RootElement.GetProperty("hits")[0];
            Assert.EndsWith("invoice.txt", hit.GetProperty("citation").GetProperty("file").GetString(), StringComparison.Ordinal);
            Assert.True(hit.GetProperty("citation").GetProperty("end").GetInt32() > 0);
        }

        (c, o, e) = Run(exe, $"query \"{index}\" tulips --files");
        Assert.Contains("page.html", o, StringComparison.Ordinal);

        // The tool resumes: a second run finds everything unchanged.
        (c, o, e) = Run(exe, $"index \"{docs}\" --index \"{index}\"");
        Assert.Equal(0, c);
        Assert.Contains("3 unchanged", o, StringComparison.Ordinal);

        string site = Path.Combine(_work, "site");
        (c, o, e) = Run(exe, $"static build \"{index}\" \"{site}\" --chunk-kb 4");
        Assert.True(c == 0, o + e);
        Assert.True(File.Exists(Path.Combine(site, "static.json")));
        StaticIndex<FilePassage> remote = StaticIndex.OpenAsync<FilePassage>(new DirectoryStaticSource(site)).AsTask().Result;
        List<string> files = remote.QueryAsync(i => i.Passages("acme").Take(5).Execute().Select(static h => h.Cite().File).ToList()).AsTask().Result;
        Assert.Single(files);
        Assert.EndsWith("invoice.txt", files[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MistakesGetOneLineAndAnExitCodeNotAStackTrace()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string dll = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "..", ".."), "OneIndex.Cli.dll", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
        Assert.True(dll.Length > 0, "the CLI build output was not found");
        string Cli(string args, out string stderr, out string stdout)
        {
            (int code, stdout, stderr) = Run(dotnet, $"\"{dll}\" {args}");
            return code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal("2", Cli("frobnicate", out string err, out _));
        Assert.DoesNotContain("   at ", err, StringComparison.Ordinal);
        Assert.Equal("1", Cli($"index \"{_work}/missing\"", out err, out _));
        Assert.DoesNotContain("   at ", err, StringComparison.Ordinal);
        Assert.Equal("1", Cli($"query \"{_work}/missing\" x", out err, out _));
        Assert.Equal("2", Cli("query", out err, out _));
        Assert.Equal("2", Cli("index --bogus x", out err, out _));
        Assert.Equal("0", Cli("--help", out _, out string help));
        Assert.Contains("oneindex index", help, StringComparison.Ordinal);
    }
}
