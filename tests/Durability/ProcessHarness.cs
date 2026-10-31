using System.Diagnostics;
using System.Globalization;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Durability;

/// <summary>
/// The multi-process campaign: one writer process doing upserts, deletes, commits and merges, and several reader processes that
/// keep opening, refreshing and verifying snapshots against what the writer announced, while the parent kills writers and readers at
/// random and starts them again. Gates: readers see only complete committed snapshots (the content of each generation they open is
/// exactly what the writer recorded for it before committing), no reader ever misses a file, a second writer is refused, a killed
/// writer's lock is free at once, no commit acknowledged by a writer is lost, and when everything has exited no file is left over.
/// </summary>
public static class ProcessHarness
{
    private const int ExpectedKeep = 4000;

    /// <summary>Records the content hash a generation must have, before the commit that creates it can land.</summary>
    public static void PublishExpected(string directory, long generation, string hash)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"g{generation}.txt");
        string temp = Path.Combine(directory, $"tmp-{Environment.ProcessId}");
        File.WriteAllText(temp, hash);
        File.Move(temp, path, overwrite: true);
        if (generation > ExpectedKeep)
        {
            File.Delete(Path.Combine(directory, $"g{generation - ExpectedKeep}.txt"));
        }
    }

    private static string? ReadExpected(string directory, long generation)
    {
        string path = Path.Combine(directory, $"g{generation}.txt");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                Thread.Sleep(10);
            }
        }

        return null;
    }

    /// <summary>The reader process: verify until killed (or for <paramref name="seconds"/> seconds when positive).</summary>
    public static int RunReader(string directory, string expectDirectory, int seed, int seconds)
    {
        var rng = new Random(seed);
        var storage = new FileSystemStorage(directory);
        var clock = Stopwatch.StartNew();
        long verifications = 0;
        long generations = 0;
        long lastGeneration = 0;
        try
        {
            IndexReader? reader = null;
            int sinceReopen = 0;
            Console.Out.WriteLine("READY");
            while (seconds == 0 || clock.Elapsed.TotalSeconds < seconds)
            {
                if (reader is null)
                {
                    try
                    {
                        reader = IndexReader.Open(storage, Workload.Codecs);
                    }
                    catch (InvalidOperationException)
                    {
                        Thread.Sleep(20); // the writer has not committed yet
                        continue;
                    }
                }

                using (IndexSnapshot snap = reader.Acquire())
                {
                    Check(snap, expectDirectory, full: false);
                    verifications++;
                    if (snap.Generation != lastGeneration)
                    {
                        generations++;
                        lastGeneration = snap.Generation;
                    }

                    // Hold the snapshot while the writer commits and merges past it, then prove every file is still there.
                    Thread.Sleep(rng.Next(0, 60));
                    Check(snap, expectDirectory, full: true);
                    verifications++;
                }

                reader.Refresh();
                if (++sinceReopen >= 25 + rng.Next(25))
                {
                    reader.Dispose();
                    reader = null;
                    sinceReopen = 0;
                }

                if (verifications % 20 == 0)
                {
                    Console.Out.WriteLine($"V {verifications} {generations}");
                }
            }

            reader?.Dispose();
            Console.Out.WriteLine($"V {verifications} {generations}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Out.WriteLine($"FAIL {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
            return 1;
        }
    }

    private static void Check(IndexSnapshot snap, string expectDirectory, bool full)
    {
        if (full)
        {
            snap.Verify();
        }

        string actual = Workload.HashRows(Workload.ReadContent(snap));
        string? expected = ReadExpected(expectDirectory, snap.Generation);
        if (expected is null)
        {
            throw new InvalidDataException($"no expected content recorded for generation {snap.Generation}");
        }

        if (expected != actual)
        {
            throw new InvalidDataException($"silently wrong: generation {snap.Generation} read {actual}, writer recorded {expected}");
        }
    }

    private sealed class Child
    {
        private readonly Process _process;
        private string? _pre;
        private long _ackGen = -1;
        private string _ackHash = "";
        private long _verifications;
        private string? _failure;
        private bool _locked;

        public Child(string[] command, IEnumerable<string> arguments, string role)
        {
            Role = role;
            var psi = new ProcessStartInfo(command[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in command.Skip(1).Concat(arguments))
            {
                psi.ArgumentList.Add(a);
            }

            _process = Process.Start(psi)!;
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (this)
                {
                    if (e.Data.StartsWith("PRE ", StringComparison.Ordinal))
                    {
                        _pre = e.Data[4..];
                    }
                    else if (e.Data.StartsWith("ACK ", StringComparison.Ordinal))
                    {
                        string[] p = e.Data.Split(' ');
                        _ackGen = long.Parse(p[1], CultureInfo.InvariantCulture);
                        _ackHash = p[2];
                        _pre = null;
                        Commits++;
                    }
                    else if (e.Data.StartsWith("V ", StringComparison.Ordinal))
                    {
                        _verifications = long.Parse(e.Data.Split(' ')[1], CultureInfo.InvariantCulture);
                    }
                    else if (e.Data.StartsWith("FAIL ", StringComparison.Ordinal))
                    {
                        _failure = e.Data;
                    }
                    else if (e.Data == "LOCKED")
                    {
                        _locked = true;
                    }
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    lock (this)
                    {
                        _failure ??= "STDERR " + e.Data;
                    }
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public string Role { get; }

        public long Commits { get; private set; }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public (string? Pre, long AckGen, string AckHash) Writer
        {
            get
            {
                lock (this)
                {
                    return (_pre, _ackGen, _ackHash);
                }
            }
        }

        public long Verifications
        {
            get
            {
                lock (this)
                {
                    return _verifications;
                }
            }
        }

        public string? Failure
        {
            get
            {
                lock (this)
                {
                    return _failure;
                }
            }
        }

        public bool SawLocked
        {
            get
            {
                lock (this)
                {
                    return _locked;
                }
            }
        }

        public bool WaitForExit(int ms) => _process.WaitForExit(ms);

        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.WaitForExit();
        }
    }

    public static int Run(string root, int seed, int readerCount, int steadySeconds, int kills, int maxDelayMs, int paceMs, string[] command)
    {
        string index = Path.Combine(root, "index");
        string expect = Path.Combine(root, "expect");
        string failures = Path.Combine(root, "failures");
        if (Directory.Exists(index))
        {
            Directory.Delete(index, true);
        }

        if (Directory.Exists(expect))
        {
            Directory.Delete(expect, true);
        }

        Directory.CreateDirectory(index);
        Directory.CreateDirectory(expect);
        var rng = new Random(seed);
        var clock = Stopwatch.StartNew();
        var problems = new List<string>();
        long writerKills = 0;
        long readerKills = 0;
        long secondWriterRefused = 0;
        long commits = 0;
        long verifications = 0;
        long lastGen = 0;
        string lastHash = new Workload.Model().Hash();
        Child? writer = null;
        var readers = new Child?[readerCount];
        int writerStarts = 0;
        int readerStarts = 0;

        Child StartWriter()
        {
            int s = rng.Next();
            writerStarts++;
            return new Child(command, ["child", "--dir", index, "--seed", s.ToString(CultureInfo.InvariantCulture), "--expect", expect, "--pace-ms", paceMs.ToString(CultureInfo.InvariantCulture)], "writer");
        }

        Child StartReader()
        {
            int s = rng.Next();
            readerStarts++;
            return new Child(command, ["reader", "--dir", index, "--expect", expect, "--seed", s.ToString(CultureInfo.InvariantCulture)], "reader");
        }

        // Call after a writer is dead: the newest commit must be the last acknowledged or the one in flight, with the exact content.
        bool CheckWriterDeath(Child dead, string when)
        {
            (string? pre, long ackGen, string ackHash) = dead.Writer;
            commits += dead.Commits;
            if (ackGen < 0)
            {
                ackGen = lastGen;
                ackHash = lastHash;
            }

            string? failure = KillHarness.Verify(index, ackGen, ackHash, pre, out long gen, out string hash);
            if (failure is not null)
            {
                problems.Add($"{when}: {failure}");
                return false;
            }

            lastGen = gen;
            lastHash = hash;
            return true;
        }

        void CheckAlive(string when)
        {
            if (writer is { HasExited: true } w)
            {
                problems.Add($"{when}: the writer exited on its own with code {w.ExitCode} {w.Failure}");
            }

            foreach (Child? r in readers)
            {
                if (r is { HasExited: true })
                {
                    problems.Add($"{when}: a reader exited on its own with code {r.ExitCode} {r.Failure}");
                }
                else if (r?.Failure is { } f)
                {
                    problems.Add($"{when}: a reader reported {f}");
                }
            }
        }

        try
        {
            writer = StartWriter();
            for (int i = 0; i < readerCount; i++)
            {
                readers[i] = StartReader();
            }

            // Steady phase: nothing is killed. Readers must verify continuously while the writer commits and merges past them.
            Thread.Sleep(steadySeconds * 1000);
            CheckAlive("steady phase");
            long steadyVerifications = readers.Sum(r => r?.Verifications ?? 0);
            Console.WriteLine($"steady: {steadySeconds} s, {writer.Commits} commits acknowledged, {steadyVerifications} verifications by {readerCount} readers, {problems.Count} problems");

            for (int k = 0; k < kills && problems.Count == 0; k++)
            {
                // Delays are skewed toward short ones so that kills also land in start-up, recovery and the first commits.
                Thread.Sleep((int)(maxDelayMs * Math.Pow(rng.NextDouble(), 1.7)));
                CheckAlive($"kill {k}");
                if (problems.Count > 0)
                {
                    break;
                }

                if (k % 5 == 4)
                {
                    // A second writer while the first is alive must be refused at once.
                    Child second = StartWriter();
                    if (!second.WaitForExit(20_000))
                    {
                        second.Kill();
                        problems.Add($"kill {k}: a second writer ran next to the first");
                        break;
                    }

                    if (second.ExitCode != KillHarness.LockedExitCode || !second.SawLocked)
                    {
                        problems.Add($"kill {k}: the second writer exited with code {second.ExitCode} instead of being refused ({second.Failure})");
                        break;
                    }

                    secondWriterRefused++;
                }

                int victim = rng.Next(readerCount + 2);
                if (victim < 2)
                {
                    writer.Kill();
                    writerKills++;
                    if (!CheckWriterDeath(writer, $"kill {k}"))
                    {
                        break;
                    }

                    // The dead writer's lock must be free at once: the new writer opens without being refused.
                    writer = StartWriter();
                }
                else
                {
                    Child r = readers[victim - 2]!;
                    verifications += r.Verifications;
                    r.Kill();
                    readerKills++;
                    readers[victim - 2] = StartReader();
                }
            }

            CheckAlive("end of campaign");
        }
        finally
        {
            foreach (Child? r in readers)
            {
                r?.Kill();
                verifications += r?.Verifications ?? 0;
            }

            if (writer is not null)
            {
                writer.Kill();
                CheckWriterDeath(writer, "final writer kill");
            }
        }

        // Everything has exited. A new writer recovers; afterwards nothing but the newest commit's files and the lock file remains.
        try
        {
            using (IndexWriter w = IndexWriter.Open(new FileSystemStorage(index), Workload.Schema, Workload.Options()))
            {
                if (w.Generation < lastGen)
                {
                    problems.Add($"final recovery: generation {w.Generation} is open but {lastGen} was acknowledged");
                }
            }

            string? leak = Workload.FindLeaks(new FileSystemStorage(index));
            if (leak is not null)
            {
                problems.Add($"leaked file after every reader and writer exited: {leak}");
            }
        }
        catch (Exception e)
        {
            problems.Add($"final recovery failed: {e.GetType().Name}: {e.Message}");
        }

        Console.WriteLine($"procs: seed {seed}, {readerCount} readers, {writerStarts} writer starts ({writerKills} killed, {secondWriterRefused} second writers refused), {readerStarts} reader starts ({readerKills} killed), {writerKills + readerKills} kills total, {commits} commits acknowledged, {verifications} snapshot verifications, final generation {lastGen}, {problems.Count} problems, {clock.Elapsed.TotalSeconds:F0} s");
        foreach (string p in problems.Take(20))
        {
            Console.WriteLine("  " + p);
        }

        if (problems.Count > 0)
        {
            Directory.CreateDirectory(failures);
            string target = Path.Combine(failures, "seed" + seed.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(target);
            foreach (string f in Directory.GetFiles(index))
            {
                File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
            }
        }

        return problems.Count == 0 ? 0 : 1;
    }
}
