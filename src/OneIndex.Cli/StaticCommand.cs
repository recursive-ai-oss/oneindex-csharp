namespace OneIndex.Cli;

internal static class StaticCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var a = new Arguments(args, ["--chunk-kb"], []);
        a.Expect(2, "oneindex static build <index> <out> [--chunk-kb N]");
        error.WriteLine("oneindex: static build is not available yet.");
        return 1;
    }
}
