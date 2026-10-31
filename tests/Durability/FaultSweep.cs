using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Durability;

/// <summary>
/// Runs the index writer under the fault injector with a power cut (or a failed operation) at every storage operation of a
/// seeded workload, for each crash policy and several crash seeds. After each schedule the storage must open without error and hold
/// exactly one acknowledged commit state: the last one the writer returned from, or the one being written when the cut came.
/// </summary>
public static class FaultSweep
{
    public sealed record Result(int Schedules, int Failures, List<string> Messages, long OperationsPerRun);

    public static Result Run(IReadOnlyList<int> workloadSeeds, IReadOnlyList<int> crashSeeds, int opCount = 80, bool includeFailedOps = true)
    {
        int schedules = 0;
        var messages = new List<string>();
        long maxOps = 0;
        foreach (int ws in workloadSeeds)
        {
            List<Workload.Op> ops = Workload.Generate(ws, opCount);
            (Dictionary<long, string> stateByGen, long total) = Reference(ops);
            maxOps = Math.Max(maxOps, total);
            foreach (CrashPolicy policy in Enum.GetValues<CrashPolicy>())
            {
                for (long at = 0; at <= total; at++)
                {
                    foreach (int cs in crashSeeds)
                    {
                        schedules++;
                        string? failure = RunSchedule(ops, stateByGen, new FaultOptions { Seed = cs, CrashAtOperation = at, Policy = policy }, crash: true, policy);
                        if (failure is not null)
                        {
                            messages.Add($"workload {ws} crash@{at} {policy} seed {cs}: {failure}");
                        }
                    }
                }
            }

            if (includeFailedOps)
            {
                for (long at = 0; at <= total; at++)
                {
                    foreach (bool powerCut in new[] { false, true })
                    {
                        schedules++;
                        string? failure = RunSchedule(ops, stateByGen, new FaultOptions { Seed = 1, FailAtOperations = [at], Policy = CrashPolicy.Tear }, crash: powerCut, CrashPolicy.Tear);
                        if (failure is not null)
                        {
                            messages.Add($"workload {ws} fail@{at} powerCut={powerCut}: {failure}");
                        }
                    }
                }
            }
        }

        return new Result(schedules, messages.Count, messages, maxOps);
    }

    /// <summary>Runs the script without faults: the content after each generation, and how many storage operations it takes.</summary>
    private static (Dictionary<long, string> StateByGen, long Operations) Reference(List<Workload.Op> ops)
    {
        var fi = new FaultInjectingStorage(new MemoryStorage());
        var states = new Dictionary<long, string>();
        var model = new Workload.Model();
        using (IndexWriter w = IndexWriter.Open(fi, Workload.Schema, Workload.Options()))
        {
            Drive(w, ops, model, (gen, hash) => states[gen] = hash);
        }

        return (states, fi.OperationCount);
    }

    private static void Drive(IndexWriter writer, List<Workload.Op> ops, Workload.Model model, Action<long, string> acknowledged)
    {
        foreach (Workload.Op op in ops)
        {
            if (op.Kind == Workload.Kind.Commit)
            {
                writer.WaitForMerges();
                long gen = writer.Commit();
                acknowledged(gen, model.Hash());
            }
            else
            {
                Workload.Apply(writer, op);
                model.Apply(op);
            }
        }
    }

    private static string? RunSchedule(List<Workload.Op> ops, Dictionary<long, string> stateByGen, FaultOptions options, bool crash, CrashPolicy policy)
    {
        var disk = new MemoryStorage();
        var fi = new FaultInjectingStorage(disk, options);
        long acked = 0;
        var model = new Workload.Model();
        try
        {
            using IndexWriter w = IndexWriter.Open(fi, Workload.Schema, Workload.Options());
            Drive(w, ops, model, (gen, _) => acked = gen);
        }
        catch (IOException)
        {
            // The cut or the failed operation: the writer is abandoned as a dead process would be.
        }

        if (crash)
        {
            fi.Crash(policy);
        }

        // The wrapper now holds the surviving bytes in the disk it wraps; recovery runs on the disk itself, as after a reboot.
        return Validate(disk, stateByGen, acked);
    }

    private static string? Validate(IStorage fi, Dictionary<long, string> stateByGen, long acked)
    {
        try
        {
            CommitInfo? commit = CommitProtocol.OpenLatest(fi);
            long gen = commit?.Generation ?? 0;
            if (gen < acked)
            {
                return $"lost commit: generation {gen} is open but {acked} was acknowledged";
            }

            if (gen > acked + 1 || (gen > 0 && !stateByGen.ContainsKey(gen)))
            {
                return $"generation {gen} was never written (acknowledged {acked})";
            }

            string expected = gen == 0 ? new Workload.Model().Hash() : stateByGen[gen];
            if (gen > 0)
            {
                using IndexSnapshot snap = IndexSnapshot.Open(fi, Workload.Codecs);
                snap.Verify();
                string actual = Workload.HashRows(Workload.ReadContent(snap));
                if (actual != expected)
                {
                    return $"silently wrong content at generation {gen}";
                }
            }

            // The recovered index must accept the writer again and leave no garbage.
            using (IndexWriter w = IndexWriter.Open(fi, Workload.Schema, Workload.Options()))
            {
                w.Upsert(Workload.ToDocument(new Workload.Op(Workload.Kind.Upsert, "after-recovery", "t0", "x", 1)));
                w.WaitForMerges();
                long next = w.Commit();
                if (next != gen + 1)
                {
                    return $"commit after recovery gave generation {next}, expected {gen + 1}";
                }
            }

            using (IndexSnapshot snap = IndexSnapshot.Open(fi, Workload.Codecs))
            {
                SortedDictionary<string, string> rows = Workload.ReadContent(snap);
                if (!rows.ContainsKey("after-recovery"))
                {
                    return "document added after recovery is missing";
                }
            }

            string? leak = Workload.FindLeaks(fi);
            return leak is null ? null : $"file {leak} is not listed by the newest commit";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }
}
