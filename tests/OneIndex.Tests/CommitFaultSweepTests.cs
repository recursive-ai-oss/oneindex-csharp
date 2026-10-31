using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>
/// Runs the commit protocol under a power failure at every storage operation of a four-commit workload, with every crash policy
/// and several seeds, then checks the two promises: the storage always opens, and no acknowledged commit is lost.
/// </summary>
public class CommitFaultSweepTests
{
    private const int Commits = 4;
    private const int SeedsPerSchedule = 8;

    /// <summary>The workload: each round writes two segment files, publishes a commit that lists every segment so far.</summary>
    private static void Workload(IStorage storage, Action<long> acknowledged, bool brokenProtocol = false)
    {
        var segments = new List<SegmentInfo>();
        for (int g = 1; g <= Commits; g++)
        {
            SegmentFile postings = TestHelpers.WriteSegmentFile(storage, SegmentParts.Postings, $"s{g}.postings", g);
            SegmentFile stored = TestHelpers.WriteSegmentFile(storage, SegmentParts.Stored, $"s{g}.stored", g + 100);
            segments.Add(new SegmentInfo($"s{g}", 10 * g, 0, 0, [postings, stored]));
            CommitInfo commit = TestHelpers.MakeCommit(g, segments);
            if (brokenProtocol)
            {
                BrokenCommit(storage, commit);
            }
            else
            {
                CommitProtocol.Commit(storage, commit);
            }

            acknowledged(g);
        }
    }

    /// <summary>A deliberately wrong protocol (no fsync of the commit, no directory sync): the sweep must catch it.</summary>
    private static void BrokenCommit(IStorage storage, CommitInfo commit)
    {
        using IStorageWriter writer = storage.Create(CommitFileNames.TempFor(commit.Generation));
        writer.Write(CommitFileFormat.Serialize(commit));
        writer.Dispose();
        storage.Rename(CommitFileNames.TempFor(commit.Generation), CommitFileNames.For(commit.Generation));
    }

    private static long CountOperations(bool broken)
    {
        var mem = new MemoryStorage();
        var fi = new FaultInjectingStorage(mem);
        Workload(fi, _ => { }, broken);
        return fi.OperationCount;
    }

    /// <summary>Returns a description of the violation, or null when the crashed storage opens to an acceptable commit.</summary>
    private static string? RunSchedule(long crashAt, CrashPolicy policy, int seed, bool broken)
    {
        var mem = new MemoryStorage();
        var fi = new FaultInjectingStorage(mem, new FaultOptions { Seed = seed, Policy = policy, CrashAtOperation = crashAt });
        long acked = 0;
        try
        {
            Workload(fi, g => acked = g, broken);
        }
        catch (SimulatedCrashException)
        {
        }

        fi.Crash();
        try
        {
            CommitInfo? latest = CommitProtocol.OpenLatest(mem);
            long got = latest?.Generation ?? 0;
            if (got < acked)
            {
                return $"lost commit: acknowledged {acked}, found {got}";
            }

            if (latest is not null)
            {
                CommitProtocol.VerifyFiles(mem, latest);
                if (latest.Segments.Count != got || BitConverter.ToInt64(latest.AppliedPosition.Span) != got * 7)
                {
                    return $"commit {got} opened with the wrong contents";
                }
            }

            return null;
        }
        catch (IOException e)
        {
            return $"failed open: {e.Message}";
        }
    }

    [Fact]
    public void NoScheduleLosesAnAcknowledgedCommitOrFailsToOpen()
    {
        long operations = CountOperations(broken: false);
        int schedules = 0;
        var failures = new List<string>();
        foreach (CrashPolicy policy in Enum.GetValues<CrashPolicy>())
        {
            // Index `operations` is a crash after the whole workload finished.
            for (long crashAt = 0; crashAt <= operations; crashAt++)
            {
                for (int seed = 1; seed <= SeedsPerSchedule; seed++)
                {
                    schedules++;
                    string? problem = RunSchedule(crashAt, policy, seed, broken: false);
                    if (problem is not null)
                    {
                        failures.Add($"{policy} at op {crashAt} seed {seed}: {problem}");
                    }
                }
            }
        }

        string summary = $"commit fault sweep: {operations} operations, {schedules} schedules ({Enum.GetValues<CrashPolicy>().Length} policies, crash at every operation 0..{operations}, seeds 1..{SeedsPerSchedule}), {failures.Count} failures";
        TestContext.Current.SendDiagnosticMessage(summary);
        if (Environment.GetEnvironmentVariable("ONEINDEX_TEST_REPORT") is { Length: > 0 } reportPath)
        {
            File.AppendAllText(reportPath, summary + Environment.NewLine);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(10)));
    }

    [Fact]
    public void TheSweepCatchesABrokenProtocol()
    {
        long operations = CountOperations(broken: true);
        int caught = 0;
        foreach (CrashPolicy policy in Enum.GetValues<CrashPolicy>())
        {
            for (long crashAt = 0; crashAt <= operations; crashAt++)
            {
                for (int seed = 1; seed <= SeedsPerSchedule; seed++)
                {
                    if (RunSchedule(crashAt, policy, seed, broken: true) is not null)
                    {
                        caught++;
                    }
                }
            }
        }

        Assert.True(caught > 0, "a protocol without fsync and directory sync should lose or corrupt a commit under some schedule");
    }

    [Fact]
    public void TheSameSweepHoldsOnTheFileBackend()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            long operations;
            {
                var probe = new FaultInjectingStorage(new FileSystemStorage(Path.Combine(dir, "probe")));
                Workload(probe, _ => { });
                operations = probe.OperationCount;
            }

            int schedules = 0;
            for (long crashAt = 0; crashAt <= operations; crashAt += 3)
            {
                foreach (CrashPolicy policy in Enum.GetValues<CrashPolicy>())
                {
                    string path = Path.Combine(dir, $"run-{schedules++}");
                    var inner = new FileSystemStorage(path);
                    var fi = new FaultInjectingStorage(inner, new FaultOptions { Seed = schedules, Policy = policy, CrashAtOperation = crashAt });
                    long acked = 0;
                    try
                    {
                        Workload(fi, g => acked = g);
                    }
                    catch (SimulatedCrashException)
                    {
                    }

                    fi.Crash();
                    CommitInfo? latest = CommitProtocol.OpenLatest(new FileSystemStorage(path, readOnly: true));
                    Assert.True((latest?.Generation ?? 0) >= acked, $"{policy} at {crashAt}: lost an acknowledged commit");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
