using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Durability;

/// <summary>
/// The fault sweep for a schema change: a seeded workload runs under the first schema, the process "restarts" with the second, the
/// rest of the workload runs while the segments are rebuilt, and the rebuild is completed. A power cut (or a failed operation) lands at
/// every storage operation of that run, for each crash policy and several crash seeds. After each schedule the storage must open and
/// hold the content of the last acknowledged state or of the call that was in flight, whichever schema the surviving commit has,
/// and a writer with the second schema must accept it and finish the rebuild without changing any document.
/// </summary>
public static class EvolutionSweep
{
    public static FaultSweep.Result Run(IReadOnlyList<int> workloadSeeds, IReadOnlyList<int> crashSeeds, int opCount = 60)
    {
        int schedules = 0;
        var messages = new List<string>();
        long maxOps = 0;
        foreach (int ws in workloadSeeds)
        {
            // The default codecs cannot look up keyword values for DeleteByTerm, so deletes by tag become deletes by identifier.
            List<Workload.Op> ops = [.. Workload.Generate(ws, opCount).Select(static o => o.Kind == Workload.Kind.DeleteTag ? o with { Kind = Workload.Kind.DeleteId, Id = "doc" + ((o.Tag.Length * 7) + o.Tag[^1]) % 40 } : o)];
            var reference = new FaultInjectingStorage(new MemoryStorage());
            Execute(ops, reference, new Tracker());
            long total = reference.OperationCount;
            maxOps = Math.Max(maxOps, total);
            foreach (CrashPolicy policy in Enum.GetValues<CrashPolicy>())
            {
                for (long at = 0; at <= total; at++)
                {
                    foreach (int cs in crashSeeds)
                    {
                        schedules++;
                        string? failure = Schedule(ops, new FaultOptions { Seed = cs, CrashAtOperation = at, Policy = policy }, crash: true, policy);
                        if (failure is not null)
                        {
                            messages.Add($"workload {ws} crash@{at} {policy} seed {cs}: {failure}");
                        }
                    }
                }
            }

            for (long at = 0; at <= total; at++)
            {
                foreach (bool powerCut in new[] { false, true })
                {
                    schedules++;
                    string? failure = Schedule(ops, new FaultOptions { Seed = 1, FailAtOperations = [at], Policy = CrashPolicy.Tear }, crash: powerCut, CrashPolicy.Tear);
                    if (failure is not null)
                    {
                        messages.Add($"workload {ws} fail@{at} powerCut={powerCut}: {failure}");
                    }
                }
            }
        }

        return new FaultSweep.Result(schedules, messages.Count, messages, maxOps);
    }

    private sealed class Tracker
    {
        public long AckedGeneration { get; set; }

        public string AckedHash { get; set; } = new Workload.Model().Hash();

        public string? InFlightHash { get; set; }

        /// <summary>The rows of the last acknowledged commit: what a restarted process finds.</summary>
        public SortedDictionary<string, string> CommittedRows { get; } = new(StringComparer.Ordinal);
    }

    private static void Execute(List<Workload.Op> ops, IStorage storage, Tracker t)
    {
        var model = new Workload.Model();
        int half = ops.Count / 2;
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.EvolveOptions()))
        {
            Run(w, ops.Take(half), model, t);
        }

        // The restarted process starts from the last commit; uncommitted operations of the first writer are gone.
        model.Rows.Clear();
        foreach (KeyValuePair<string, string> row in t.CommittedRows)
        {
            model.Rows[row.Key] = row.Value;
        }

        using (IndexWriter w = IndexWriter.Open(storage, Workload.SchemaV2, Workload.EvolveOptions()))
        {
            t.AckedGeneration = w.Generation;
            t.AckedHash = model.Hash();
            t.InFlightHash = null;
            Run(w, ops.Skip(half), model, t);
            t.InFlightHash = model.Hash();
            w.CompleteSchemaEvolution();
            t.AckedGeneration = w.Generation;
            t.AckedHash = model.Hash();
            t.InFlightHash = null;
        }
    }

    private static void Run(IndexWriter w, IEnumerable<Workload.Op> ops, Workload.Model model, Tracker t)
    {
        foreach (Workload.Op op in ops)
        {
            if (op.Kind == Workload.Kind.Commit)
            {
                w.WaitForMerges();
                t.InFlightHash = model.Hash();
                long gen = w.Commit();
                t.AckedGeneration = gen;
                t.AckedHash = model.Hash();
                t.InFlightHash = null;
                t.CommittedRows.Clear();
                foreach (KeyValuePair<string, string> row in model.Rows)
                {
                    t.CommittedRows[row.Key] = row.Value;
                }
            }
            else
            {
                Workload.Apply(w, op);
                model.Apply(op);
            }
        }
    }

    private static string? Schedule(List<Workload.Op> ops, FaultOptions options, bool crash, CrashPolicy policy)
    {
        var disk = new MemoryStorage();
        var fi = new FaultInjectingStorage(disk, options);
        var tracker = new Tracker();
        try
        {
            Execute(ops, fi, tracker);
        }
        catch (IOException)
        {
            // The cut or the failed operation: the writer is abandoned as a dead process would be.
        }

        if (crash)
        {
            fi.Crash(policy);
        }

        return Validate(disk, tracker);
    }

    private static string? Validate(IStorage disk, Tracker t)
    {
        try
        {
            CommitInfo? commit = CommitProtocol.OpenLatest(disk);
            long gen = commit?.Generation ?? 0;
            if (gen < t.AckedGeneration)
            {
                return $"lost commit: generation {gen} is open but {t.AckedGeneration} was acknowledged";
            }

            string empty = new Workload.Model().Hash();
            string hash = empty;
            if (gen > 0)
            {
                using IndexSnapshot snap = IndexSnapshot.Open(disk);
                snap.Verify();
                hash = Workload.HashRows(Workload.ReadContent(snap));
                if (snap.Commit.Segments.Any(s => s.SchemaEpoch > snap.Commit.SchemaEpoch))
                {
                    return "a segment is newer than the commit's schema epoch";
                }
            }

            if (hash != t.AckedHash && hash != t.InFlightHash)
            {
                return $"silently wrong content at generation {gen}: {hash}, acknowledged {t.AckedHash} (generation {t.AckedGeneration}), in flight {t.InFlightHash ?? "none"}";
            }

            // The recovered index takes the second schema, finishes the rebuild and keeps every document.
            using (IndexWriter w = IndexWriter.Open(disk, Workload.SchemaV2, Workload.EvolveOptions()))
            {
                w.CompleteSchemaEvolution();
                if (w.SchemaEvolution.InProgress)
                {
                    return "the rebuild after recovery did not run to completion";
                }
            }

            if (CommitProtocol.OpenLatest(disk) is not null)
            {
                using IndexSnapshot snap = IndexSnapshot.Open(disk);
                snap.Verify();
                if (Workload.HashRows(Workload.ReadContent(snap)) != hash)
                {
                    return "the rebuild after recovery changed the content";
                }
            }

            string? leak = Workload.FindLeaks(disk);
            return leak is null ? null : $"file {leak} is not listed by the newest commit";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }
}
