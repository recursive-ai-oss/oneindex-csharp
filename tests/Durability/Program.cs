using System.Diagnostics;
using System.Globalization;
using OneIndex.Durability;

try
{
    return Run(args);
}
catch (CampaignDirectoryException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

static int Run(string[] args)
{
    string command = args.Length > 0 ? args[0] : "help";
    string Option(string name, string fallback)
    {
        int i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    int Number(string name, int fallback) => int.Parse(Option(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

    switch (command)
    {
        case "sweep":
        {
            int[] workloads = [.. Enumerable.Range(Number("first-seed", 1), Number("workloads", 3))];
            int[] crashSeeds = [.. Enumerable.Range(1, Number("crash-seeds", 4))];
            var clock = Stopwatch.StartNew();
            FaultSweep.Result result = FaultSweep.Run(workloads, crashSeeds, Number("ops", 80));
            Console.WriteLine($"sweep: {result.Schedules} schedules (workload seeds {string.Join(",", workloads)}, crash seeds {string.Join(",", crashSeeds)}, up to {result.OperationsPerRun} storage operations per run), {result.Failures} failures, {clock.Elapsed.TotalSeconds:F1} s");
            foreach (string m in result.Messages.Take(20))
            {
                Console.WriteLine("  " + m);
            }

            return result.Failures == 0 ? 0 : 1;
        }

        case "evolve-sweep":
        {
            int[] workloads = [.. Enumerable.Range(Number("first-seed", 1), Number("workloads", 2))];
            int[] crashSeeds = [.. Enumerable.Range(1, Number("crash-seeds", 2))];
            var clock = Stopwatch.StartNew();
            FaultSweep.Result result = EvolutionSweep.Run(workloads, crashSeeds, Number("ops", 60));
            Console.WriteLine($"evolve-sweep: {result.Schedules} schedules (workload seeds {string.Join(",", workloads)}, crash seeds {string.Join(",", crashSeeds)}, up to {result.OperationsPerRun} storage operations per run), {result.Failures} failures, {clock.Elapsed.TotalSeconds:F1} s");
            foreach (string m in result.Messages.Take(20))
            {
                Console.WriteLine("  " + m);
            }

            return result.Failures == 0 ? 0 : 1;
        }

        case "sparse-child":
            return SparseKill.RunChild(Option("dir", "."), Number("seed", 1));

        case "sparse-kill":
        {
            string sparseRoot = Option("dir", Path.Combine(Path.GetTempPath(), "oneindex-sparse-kill"));
            string[] sparseChild = SelfCommand.Get();
            var sparseClock = Stopwatch.StartNew();
            SparseKill.Result sr = SparseKill.Run(sparseRoot, Number("kills", 100), Number("seed", 1), Number("max-delay-ms", 600), sparseChild, Option("clear", "false") == "true");
            Console.WriteLine($"sparse-kill: {sr.Kills} kills (seed {Number("seed", 1)}), {sr.Commits} commits acknowledged, {sr.KilledDuringCommit} kills inside a commit call, {sr.Documents} documents checked, {sr.Failures} failures, {sparseClock.Elapsed.TotalSeconds:F0} s");
            foreach (string m in sr.Messages.Take(20))
            {
                Console.WriteLine("  " + m);
            }

            return sr.Failures == 0 ? 0 : 1;
        }

        case "embed-child":
            return EmbedKill.RunChild(Option("dir", "."), Number("seed", 1));

        case "embed-kill":
        {
            string root = Option("dir", Path.Combine(Path.GetTempPath(), "oneindex-embed-kill"));
            string[] childCommand = SelfCommand.Get();
            var clock = Stopwatch.StartNew();
            EmbedKill.Result r = EmbedKill.Run(root, Number("kills", 100), Number("seed", 1), Number("max-delay-ms", 600), childCommand, Option("clear", "false") == "true");
            Console.WriteLine($"embed-kill: {r.Kills} kills (seed {Number("seed", 1)}), {r.Commits} commits acknowledged, {r.KilledDuringCommit} kills inside a commit call, {r.Documents} documents checked, {r.Failures} failures, {clock.Elapsed.TotalSeconds:F0} s");
            foreach (string m in r.Messages.Take(20))
            {
                Console.WriteLine("  " + m);
            }

            return r.Failures == 0 ? 0 : 1;
        }

        case "child":
            return KillHarness.RunChild(Option("dir", "."), Number("seed", 1), Number("ops", 200_000), Option("expect", "") is { Length: > 0 } e ? e : null, Number("pace-ms", 0), Number("evolve", 0));

        case "reader":
            return ProcessHarness.RunReader(Option("dir", "."), Option("expect", "."), Number("seed", 1), Number("seconds", 0));

        case "procs":
            return ProcessHarness.Run(
                Option("dir", Path.Combine(Path.GetTempPath(), "oneindex-procs")),
                Number("seed", 1),
                Number("readers", 4),
                Number("steady-seconds", 20),
                Number("kills", 200),
                Number("max-delay-ms", 800),
                Number("pace-ms", 2),
                SelfCommand.Get());

        case "kill":
        {
            string root = Option("dir", Path.Combine(Path.GetTempPath(), "oneindex-kill"));
            int kills = Number("kills", 100);
            int lanes = Number("lanes", 1);
            int seed = Number("seed", 1);
            int maxDelay = Number("max-delay-ms", 600);
            string failures = Option("failures", Path.Combine(root, "failures"));
            bool evolve = Option("evolve", "false") == "true";
            string[] childCommand = SelfCommand.Get();
            var clock = Stopwatch.StartNew();
            var tasks = new List<Task<KillHarness.Summary>>();
            for (int lane = 0; lane < lanes; lane++)
            {
                int laneIndex = lane;
                int perLane = kills / lanes + (laneIndex < kills % lanes ? 1 : 0);
                tasks.Add(Task.Run(() => KillHarness.RunLane(Path.Combine(root, "lane" + laneIndex), failures, perLane, seed + (laneIndex * 7919), maxDelay, childCommand, evolve, Option("clear", "false") == "true")));
            }

            KillHarness.Summary[] all = Task.WhenAll(tasks).GetAwaiter().GetResult();
            int total = all.Sum(s => s.Kills);
            int failed = all.Sum(s => s.Failures);
            Console.WriteLine($"kill: {total} kills in {lanes} lanes (seed {seed}), {all.Sum(s => s.Commits)} commits acknowledged, {all.Sum(s => s.KilledDuringCommit)} kills landed inside a commit call ({all.Sum(s => s.CommitLandedUnacknowledged)} of those commits were already durable), {failed} failures, {clock.Elapsed.TotalSeconds:F0} s");
            foreach (string m in all.SelectMany(s => s.Messages).Take(20))
            {
                Console.WriteLine("  " + m);
            }

            return failed == 0 ? 0 : 1;
        }

        default:
            Console.WriteLine("usage: Durability sweep [--workloads N] [--crash-seeds N] [--ops N] | kill --dir D --kills N [--lanes L] [--seed S] [--clear true] (the directory must be empty unless --clear true) | child --dir D --seed S");
            return 2;
    }
}
