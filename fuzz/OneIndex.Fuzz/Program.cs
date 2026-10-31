using OneIndex.Fuzz;
using SharpFuzz;

// One fuzz executable, one target per input surface. The target is chosen by the ONEINDEX_FUZZ_TARGET environment variable
// because libFuzzer's .NET host starts the program without command-line arguments.
//
// To add a target: write a static method Run(ReadOnlySpan<byte> data) that lets only the library's typed exceptions escape,
// then add one line to Targets below.
//
// Without a libFuzzer host: `replay <target> <file>...` runs saved inputs once each (reproduces a finding), and
// `seeds <target> <out dir>` writes starting inputs for a target.
internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> data);

    private static readonly Dictionary<string, Target> Targets = new(StringComparer.Ordinal)
    {
        ["commit"] = CommitTarget.Run,
    };

    private static int Main(string[] args)
    {
        if (args.Length > 2 && args[0] == "replay")
        {
            Target target = Targets[args[1]];
            foreach (string path in args.Skip(2))
            {
                target(File.ReadAllBytes(path));
                Console.WriteLine("ok " + path);
            }

            return 0;
        }

        if (args.Length > 2 && args[0] == "seeds")
        {
            Directory.CreateDirectory(args[2]);
            CommitTarget.WriteSeeds(args[2]);
            return 0;
        }

        string name = Environment.GetEnvironmentVariable("ONEINDEX_FUZZ_TARGET") ?? "commit";
        Target run = Targets[name];
        Fuzzer.LibFuzzer.Run(span => run(span));
        return 0;
    }
}
