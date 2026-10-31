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
        ["analysis"] = AnalysisTarget.Run,
        ["terms"] = TextIndexTarget.RunTerms,
        ["postings"] = TextIndexTarget.RunPostings,
        ["segment"] = SegmentTarget.Run,
        ["query"] = QueryTarget.Run,
        ["columns"] = DocValuesTarget.RunColumns,
        ["points"] = DocValuesTarget.RunPoints,
        ["stored"] = DocValuesTarget.RunStored,
        ["usertext"] = UserTextTarget.Run,
        ["strict"] = QueryTextTargets.RunStrict,
        ["filter"] = QueryTextTargets.RunFilter,
        ["queryjson"] = QueryTextTargets.RunJson,
        ["dynamic"] = DynamicTarget.Run,
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
            switch (args[1])
            {
                case "analysis":
                    AnalysisTarget.WriteSeeds(args[2]);
                    break;
                case "terms":
                    TextIndexTarget.WriteSeeds(args[2], terms: true);
                    break;
                case "postings":
                    TextIndexTarget.WriteSeeds(args[2], terms: false);
                    break;
                case "segment":
                    SegmentTarget.WriteSeeds(args[2]);
                    break;
                case "query":
                    QueryTarget.WriteSeeds(args[2]);
                    break;
                case "columns":
                    DocValuesTarget.WriteSeeds(args[2], 0);
                    break;
                case "points":
                    DocValuesTarget.WriteSeeds(args[2], 1);
                    break;
                case "stored":
                    DocValuesTarget.WriteSeeds(args[2], 2);
                    break;
                case "usertext":
                    UserTextTarget.WriteSeeds(args[2]);
                    break;
                case "strict":
                    QueryTextTargets.WriteSeeds(args[2], 0);
                    break;
                case "filter":
                    QueryTextTargets.WriteSeeds(args[2], 1);
                    break;
                case "queryjson":
                    QueryTextTargets.WriteSeeds(args[2], 2);
                    break;
                case "dynamic":
                    DynamicTarget.WriteSeeds(args[2]);
                    break;
                default:
                    CommitTarget.WriteSeeds(args[2]);
                    break;
            }

            return 0;
        }

        string name = Environment.GetEnvironmentVariable("ONEINDEX_FUZZ_TARGET") ?? "commit";
        Target run = Targets[name];
        Fuzzer.LibFuzzer.Run(span => run(span));
        return 0;
    }
}
