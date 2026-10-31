using System.Diagnostics;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Durability;

/// <summary>
/// The kill campaign. A writer child process does random upserts, deletes, commits and merges against a directory and reports on
/// its standard output: <c>PRE hash</c> just before each commit call and <c>ACK generation hash</c> after the call returned. The
/// parent kills the child at a random moment, then reopens the directory and requires the newest commit to be either the last
/// acknowledged one or the one that was being written, with exactly the content reported for it.
/// </summary>
public static class KillHarness
{
    /// <summary>Exit code of a writer child that found the index locked by another writer.</summary>
    public const int LockedExitCode = 3;

    public static int RunChild(string directory, int seed, int maxOps, string? expectDirectory = null, int paceMs = 0)
    {
        var storage = new FileSystemStorage(directory);
        IndexWriterOptions options = Workload.Options(seed % 2 == 0 ? new ThreadedMergeScheduler() : null, maxBuffered: 8);
        IndexWriter writer;
        try
        {
            writer = IndexWriter.Open(storage, Workload.Schema, options);
        }
        catch (IndexLockedException)
        {
            Console.Out.WriteLine("LOCKED");
            return LockedExitCode;
        }

        using (writer)
        {
            return RunWriter(writer, seed, maxOps, expectDirectory, paceMs);
        }
    }

    private static int RunWriter(IndexWriter writer, int seed, int maxOps, string? expectDirectory, int paceMs)
    {
        var model = new Workload.Model();
        if (writer.Generation > 0)
        {
            using IndexSnapshot snap = writer.OpenSnapshot();
            foreach (KeyValuePair<string, string> row in Workload.ReadContent(snap))
            {
                model.Rows[row.Key] = row.Value;
            }
        }

        Console.Out.WriteLine("READY");
        using IndexReader? nrt = expectDirectory is not null ? writer.OpenNearRealTimeReader() : null;
        var rng = new Random(seed);
        int sinceCommit = 0;
        for (int i = 0; i < maxOps; i++)
        {
            int r = rng.Next(100);
            string id = "doc" + rng.Next(300);
            Workload.Op op;
            if (r < 70)
            {
                op = new Workload.Op(Workload.Kind.Upsert, id, "t" + rng.Next(5), "body " + rng.Next(100000), rng.NextInt64(0, 1_000_000));
            }
            else if (r < 90)
            {
                op = new Workload.Op(Workload.Kind.DeleteId, id, "", "", 0);
            }
            else
            {
                op = new Workload.Op(Workload.Kind.DeleteTag, "", "t" + rng.Next(5), "", 0);
            }

            Workload.Apply(writer, op);
            model.Apply(op);
            if (nrt is not null && rng.Next(15) == 0)
            {
                // The near-real-time view must show exactly the model, committed or not.
                nrt.Refresh();
                using IndexSnapshot view = nrt.Acquire();
                if (Workload.HashRows(Workload.ReadContent(view)) != model.Hash())
                {
                    throw new InvalidDataException("the near-real-time view differs from the writer's state");
                }
            }

            sinceCommit++;
            if (sinceCommit >= 3 + rng.Next(30))
            {
                sinceCommit = 0;
                if (rng.Next(3) == 0)
                {
                    writer.WaitForMerges();
                }

                Console.Out.WriteLine("PRE " + model.Hash());
                if (expectDirectory is not null)
                {
                    // Readers in other processes check every generation they open against this, so it is written before the commit can land.
                    ProcessHarness.PublishExpected(expectDirectory, writer.Generation + 1, model.Hash());
                }

                long gen = writer.Commit();
                if (paceMs > 0)
                {
                    Thread.Sleep(paceMs);
                }

                Console.Out.WriteLine($"ACK {gen} {model.Hash()}");
            }
        }

        return 0;
    }

    public sealed record Summary(int Kills, int Failures, List<string> Messages, long Commits, int KilledDuringCommit, int CommitLandedUnacknowledged);

    /// <summary>Runs <paramref name="kills"/> kill cycles in one directory.</summary>
    public static Summary RunLane(string directory, string failureDirectory, int kills, int seed, int maxDelayMs, string[] childCommand)
    {
        var rng = new Random(seed);
        var messages = new List<string>();
        long commits = 0;
        int duringCommit = 0;
        int landed = 0;
        long lastGen = 0;
        string lastHash = new Workload.Model().Hash();
        Directory.CreateDirectory(directory);
        int done = 0;
        for (; done < kills; done++)
        {
            int childSeed = rng.Next();
            var psi = new ProcessStartInfo(childCommand[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in childCommand.Skip(1))
            {
                psi.ArgumentList.Add(a);
            }

            psi.ArgumentList.Add("child");
            psi.ArgumentList.Add("--dir");
            psi.ArgumentList.Add(directory);
            psi.ArgumentList.Add("--seed");
            psi.ArgumentList.Add(childSeed.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using Process child = Process.Start(psi)!;
            var lines = new List<string>();
            var errors = new System.Text.StringBuilder();
            child.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (lines)
                    {
                        lines.Add(e.Data);
                    }
                }
            };
            child.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (errors)
                    {
                        errors.AppendLine(e.Data);
                    }
                }
            };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();

            // Delays are skewed toward short ones so that kills also land in start-up, recovery and the first commits.
            int delay = (int)(maxDelayMs * Math.Pow(rng.NextDouble(), 1.7));
            bool exited = child.WaitForExit(delay);
            if (!exited)
            {
                child.Kill(entireProcessTree: true);
            }

            child.WaitForExit();
            if (exited && child.ExitCode != 0)
            {
                messages.Add($"kill {done}: child failed on its own with exit code {child.ExitCode}: {errors}");
                Preserve(directory, failureDirectory, done);
                break;
            }

            string[] output;
            lock (lines)
            {
                output = [.. lines];
            }

            long ackGen = lastGen;
            string ackHash = lastHash;
            string? pre = null;
            foreach (string line in output)
            {
                if (line.StartsWith("PRE ", StringComparison.Ordinal))
                {
                    pre = line[4..];
                }
                else if (line.StartsWith("ACK ", StringComparison.Ordinal))
                {
                    string[] p = line.Split(' ');
                    ackGen = long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                    ackHash = p[2];
                    pre = null;
                    commits++;
                }
            }

            string? failure = Verify(directory, ackGen, ackHash, pre, out long gen, out string hash);
            if (failure is not null)
            {
                messages.Add($"kill {done} (delay {delay} ms, child seed {childSeed}): {failure}");
                Preserve(directory, failureDirectory, done);
                break;
            }

            if (pre is not null)
            {
                duringCommit++;
                if (gen == ackGen + 1)
                {
                    landed++;
                }
            }

            lastGen = gen;
            lastHash = hash;
        }

        return new Summary(done, messages.Count, messages, commits, duringCommit, landed);
    }

    internal static string? Verify(string directory, long ackGen, string ackHash, string? pre, out long gen, out string hash)
    {
        gen = 0;
        hash = new Workload.Model().Hash();
        try
        {
            var storage = new FileSystemStorage(directory);
            CommitInfo? commit = CommitProtocol.OpenLatest(storage);
            if (commit is null)
            {
                return ackGen == 0 ? null : $"lost commits: no commit on disk but generation {ackGen} was acknowledged";
            }

            gen = commit.Generation;
            using IndexSnapshot snap = IndexSnapshot.Open(storage, Workload.Codecs);
            snap.Verify();
            SortedDictionary<string, string> content = Workload.ReadContent(snap);
            hash = Workload.HashRows(content);
            if (gen < ackGen)
            {
                return $"lost commit: generation {gen} is open but {ackGen} was acknowledged";
            }

            if ((gen == ackGen && hash == ackHash) || (gen == ackGen + 1 && pre is not null && hash == pre))
            {
                return Workload.CheckSearch(snap, content) is { } searchFailure ? $"wrong search results at generation {gen}: {searchFailure}" : null;
            }

            return $"silently wrong: generation {gen} content {hash}, acknowledged generation {ackGen} content {ackHash}, in-flight content {pre ?? "none"}";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException)
        {
            return $"failed open or read: {e.GetType().Name}: {e.Message}";
        }
    }

    private static void Preserve(string directory, string failureDirectory, int cycle)
    {
        string target = Path.Combine(failureDirectory, $"{Path.GetFileName(directory)}-{cycle}");
        Directory.CreateDirectory(target);
        foreach (string f in Directory.GetFiles(directory))
        {
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
        }
    }
}
