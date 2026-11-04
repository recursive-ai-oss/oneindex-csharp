using OneIndex.Storage;

namespace OneIndex.Cli;

/// <summary>Entry point of the <c>oneindex</c> tool. Exit codes: 0 success, 1 the command failed (bad folder, damaged index, ...), 2 a usage mistake, 130 cancelled.</summary>
internal static class Program
{
    internal const string Usage = """
        oneindex: index folders, search them and build static indexes.

        Usage:
          oneindex index <folder> [--index <dir>] [--watch] [--embed] [--ocr]
              Reads every file under <folder> (text, Markdown, HTML, PDF, archives; scans with --ocr)
              and keeps a searchable passage index in <dir> (default: <folder>/.oneindex).
              Safe to stop and run again: it resumes. --watch keeps the index current until Ctrl+C.
              --embed also computes passage embeddings (slower; enables --hybrid in query).
          oneindex query <index> "<text>" [--limit N] [--filter "<filter>"] [--hybrid] [--json] [--files]
              Prints the best passages with citations (file, page, character range).
              --filter uses the filter-string syntax, for example: Format = pdf AND Page > 2
              --files shows one hit per file. --json writes one JSON object with the hits.
          oneindex static build <index> <out> [--chunk-kb N] [--scattered-kb N]
              Writes a static index of a passage index into <out> for browsers and plain file servers.
          oneindex --version | --help

        Exit codes: 0 done, 1 the command failed, 2 wrong usage.
        """;

    private static int Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return Run(args, Console.Out, Console.Error, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("oneindex: cancelled.");
            return 130;
        }
    }

    /// <summary>Runs a command line. Kept separate from <c>Main</c> so tests can call it with their own writers.</summary>
    // The package version (for example 0.4.0-preview.2): the informational version without the build metadata after '+'. The assembly version
    // is four numbers and drops the pre-release label.
    internal static string PackageVersion()
    {
        string? v = (System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) as System.Reflection.AssemblyInformationalVersionAttribute[])?.FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrEmpty(v))
        {
            return typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? v : v[..plus];
    }

    internal static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            error.WriteLine(Usage);
            return 2;
        }

        try
        {
            switch (args[0])
            {
                case "--version" or "-v":
                    output.WriteLine(PackageVersion());
                    return 0;
                case "--help" or "-h" or "help":
                    output.WriteLine(Usage);
                    return 0;
                case "index":
                    return IndexCommand.Run(args[1..], output, error, cancellationToken);
                case "query":
                    return QueryCommand.Run(args[1..], output, error, cancellationToken);
                case "static" when args.Length > 1 && args[1] == "build":
                    return StaticCommand.Run(args[2..], output, error, cancellationToken);
                default:
                    error.WriteLine($"oneindex: unknown command '{args[0]}'. Run 'oneindex --help'.");
                    return 2;
            }
        }
        catch (UsageException e)
        {
            error.WriteLine($"oneindex: {e.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CorruptIndexException or InvalidOperationException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            // A user mistake or a damaged file is reported in one line, never as a stack trace.
            error.WriteLine($"oneindex: {e.Message}");
            return 1;
        }
    }
}

/// <summary>A wrong command line.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Splits a command line into positionals and options (<c>--name value</c> or <c>--flag</c>).</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    public Arguments(string[] args, string[] valueOptions, string[] flags)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (Array.IndexOf(flags, a) >= 0)
                {
                    _options[a] = null;
                }
                else if (Array.IndexOf(valueOptions, a) >= 0)
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new UsageException($"{a} needs a value.");
                    }

                    _options[a] = args[++i];
                }
                else
                {
                    throw new UsageException($"unknown option {a}.");
                }
            }
            else
            {
                Positionals.Add(a);
            }
        }
    }

    public List<string> Positionals { get; } = [];

    public bool Has(string flag) => _options.ContainsKey(flag);

    public string? Get(string name) => _options.TryGetValue(name, out string? v) ? v : null;

    public int GetInt(string name, int fallback, int min, int max)
    {
        string? v = Get(name);
        if (v is null)
        {
            return fallback;
        }

        if (!int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) || n < min || n > max)
        {
            throw new UsageException($"{name} must be a whole number from {min} to {max}.");
        }

        return n;
    }

    public void Expect(int count, string usage)
    {
        if (Positionals.Count != count)
        {
            throw new UsageException($"expected: {usage}");
        }
    }
}
