using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OneIndex.EntityFrameworkCore;
using OneIndex.Search;

namespace OneIndex.EfDurability;

/// <summary>Entry point: the kill campaign (<c>kill</c>), its child application (<c>app</c>) and the overhead measurements (<c>bench</c>).</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: kill --dir D --cycles N --seed S | app ... | bench --dir D --rows N");
            return 2;
        }

        var a = new Args(args);
        switch (args[0])
        {
            case "kill":
                return await new KillCampaign(a).RunAsync().ConfigureAwait(false);
            case "app":
                return await ChildApp.RunAsync(a).ConfigureAwait(false);
            case "bench":
                return await Bench.RunAsync(a).ConfigureAwait(false);
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }
}

/// <summary>Minimal <c>--name value</c> argument bag.</summary>
public sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Args(string[] args)
    {
        for (int i = 1; i + 1 < args.Length; i += 2)
        {
            _values[args[i].TrimStart('-')] = args[i + 1];
        }
    }

    public string Get(string name, string fallback = "") => _values.TryGetValue(name, out string? v) ? v : fallback;

    public int Number(string name, int fallback) => _values.TryGetValue(name, out string? v) ? int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
}

/// <summary>The seeded workload of an application process: inserts, updates and deletes of a small id space, so two processes collide often.</summary>
public static class Mutations
{
    public const int IdSpace = 400;

    public static string[] Statuses { get; } = ["open", "paid", "overdue", "void"];

    public static string[] Cities { get; } = ["Paris", "Oslo", "Rome", "Lima"];

    public static string[] Words { get; } = ["acme", "invoice", "widget", "hosting", "support", "license", "travel", "audit", "refund", "sprocket"];

    /// <summary>One save of one to five random changes. Conflicts with the other process are expected and ignored.</summary>
    public static async Task<bool> SaveSomeAsync(AppDb db, Random rng, bool useAsync)
    {
        int ops = rng.Next(1, 6);
        for (int i = 0; i < ops; i++)
        {
            string id = "inv-" + rng.Next(IdSpace).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            Invoice? row = await db.Invoices.FindAsync(id).ConfigureAwait(false);
            if (row is null)
            {
                db.Invoices.Add(Fill(new Invoice { Id = id, Version = 1 }, rng));
            }
            else if (rng.Next(10) < 3)
            {
                db.Invoices.Remove(row);
            }
            else
            {
                row.Version++;
                Fill(row, rng);
            }
        }

        try
        {
            if (useAsync)
            {
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            else
            {
                db.SaveChanges();
            }

            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private static Invoice Fill(Invoice i, Random rng)
    {
        i.Title = Words[rng.Next(Words.Length)] + " " + Words[rng.Next(Words.Length)] + " " + rng.Next(1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        i.Status = Statuses[rng.Next(Statuses.Length)];
        i.Amount = rng.Next(10_000);
        i.CustomerId = rng.Next(20);
        i.Billing = new Billing { Contact = Words[rng.Next(Words.Length)], City = Cities[rng.Next(Cities.Length)], Terms = rng.Next(90), Notes = rng.Next(3) == 0 ? [] : [Words[rng.Next(Words.Length)]] };
        i.Meta = System.Text.Json.JsonDocument.Parse("{\"tier\":" + rng.Next(3).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}").RootElement.Clone();
        return i;
    }
}

/// <summary>The application process the campaign kills.</summary>
public static class ChildApp
{
    public static async Task<int> RunAsync(Args a)
    {
        string db = a.Get("db");
        string root = a.Get("root");
        string inst = a.Get("inst");
        bool inline = a.Get("mode") == "inline";
        int ms = a.Number("ms", 3000);
        string[] kill = a.Get("kill").Split(':', 2);
        string killStage = kill[0];
        int killAt = kill.Length > 1 ? int.Parse(kill[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        string hitFile = Path.Combine(root, inst + ".hit");
        string ackFile = Path.Combine(root, inst + ".ack");
        var rng = new Random(a.Number("seed", 1));
        int hits = 0;
        OneIndexRuntime? rt = null;
        var gate = new object();

        void Probe(string stage)
        {
            lock (gate)
            {
                if (killAt > 0 && stage == killStage && ++hits == killAt)
                {
                    File.WriteAllText(hitFile, stage);
                    Process.GetCurrentProcess().Kill();
                }

                if (stage is "apply.after-commit" or "reindex.after-commit" && rt?.GetPosition<Invoice>() is { } p)
                {
                    // Acknowledged: this commit returned, so a later open must show at least this position.
                    File.AppendAllText(ackFile, p.LogId.ToString("N") + " " + p.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                }
            }
        }

        using AppDb first = AppDb.Create(db, Path.Combine(root, inst), o =>
        {
            o.Probe = Probe;
            o.ApplyInline = inline;
            o.RetainRows = a.Number("retain", 30);
            o.PollInterval = TimeSpan.FromMilliseconds(15);
            o.BatchSize = rng.Next(3, 40);
            o.LateRowAge = TimeSpan.FromSeconds(a.Number("late", 5));
        });
        rt = OneIndexRuntime.For(first);
        using var cts = new CancellationTokenSource();
        Task? applier = inline ? null : Task.Run(() => rt.RunAsync(cts.Token));
        var clock = Stopwatch.StartNew();
        int writers = a.Number("writers", 1);
        var seeds = Enumerable.Range(0, writers).Select(_ => rng.Next()).ToArray();
        await Task.WhenAll(seeds.Select(seed => Task.Run(async () =>
        {
            var wrng = new Random(seed);
            int saves = 0;
            while (clock.ElapsedMilliseconds < ms)
            {
                using AppDb w = AppDb.Create(db, Path.Combine(root, inst));
                await Mutations.SaveSomeAsync(w, wrng, wrng.Next(2) == 0).ConfigureAwait(false);
                if (inline && ++saves % 40 == 0)
                {
                    rt.Prune(a.Number("retain", 30));
                }
            }
        }))).ConfigureAwait(false);

        await cts.CancelAsync().ConfigureAwait(false);
        if (applier is not null)
        {
            await applier.ConfigureAwait(false);
        }

        return 0;
    }
}

/// <summary>Reads the database and an index and reports every disagreement.</summary>
public static class Verifier
{
    public sealed record Outcome(List<string> Problems, bool Rebuilt, int Documents, long AppliedRows);

    public static Outcome Verify(string dbFile, string indexRoot, string ackFile, int expectedHits = 0)
    {
        var problems = new List<string>();
        bool rebuilt = false;
        int docs = 0;
        long applied = 0;
        try
        {
            using AppDb db = AppDb.Create(dbFile, indexRoot, o => o.RetainRows = 0);
            OneIndexRuntime rt = OneIndexRuntime.For(db);
            try
            {
                OneIndexPosition? before = rt.GetPosition<Invoice>();
                if (File.Exists(ackFile))
                {
                    foreach (string line in File.ReadAllLines(ackFile))
                    {
                        string[] parts = line.Split(' ');
                        if (before is { } b && b.LogId.ToString("N") == parts[0] && b.Seq < long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
                        {
                            problems.Add($"lost commit: acknowledged position {parts[1]}, reopened at {b.Seq}");
                        }
                    }
                }

                rt.CatchUp();
                OneIndexPosition? after = rt.GetPosition<Invoice>();
                rebuilt = before is null || after is null || before.Value.Base != after.Value.Base || before.Value.LogId != after.Value.LogId;
                SearchIndex<Invoice> idx = rt.Index<Invoice>();
                using AppDb read = AppDb.Create(dbFile, null);
                Dictionary<string, Invoice> rows = read.Invoices.AsNoTracking().ToDictionary(i => i.Id, StringComparer.Ordinal);
                List<string> ids = idx.CommittedIds();
                docs = ids.Count;
                if (ids.Count != rows.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                {
                    problems.Add($"count: index has {ids.Count} ({ids.Distinct(StringComparer.Ordinal).Count()} distinct), database has {rows.Count}");
                }

                foreach (Invoice r in rows.Values)
                {
                    SearchResults<Invoice> hit = idx.SearchBy(new TermQuery("Id", r.Id)).Take(2).Execute();
                    if (hit.Count != 1)
                    {
                        problems.Add($"{r.Id}: {hit.Count} index documents");
                        continue;
                    }

                    Invoice d = hit[0].Document;
                    if (d.Version != r.Version || d.Title != r.Title || d.Status != r.Status || d.Amount != r.Amount || d.Billing.City != r.Billing.City || d.Billing.Terms != r.Billing.Terms || !d.Billing.Notes.SequenceEqual(r.Billing.Notes))
                    {
                        problems.Add($"{r.Id}: index v{d.Version} '{d.Title}' {d.Status} {d.Amount}; database v{r.Version} '{r.Title}' {r.Status} {r.Amount}");
                    }
                }

                // No row applied twice or skipped: the applied-row count in the position equals the log rows after the rebuild position (while nothing it covers was pruned).
                if (after is { } p)
                {
                    string type = typeof(Invoice).FullName!;
                    LogCheck(read, type, p, problems);
                    applied = p.Count;
                }
            }
            finally
            {
                rt.Dispose();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problems.Add("open/verify failed: " + e.GetType().Name + ": " + e.Message);
        }

        return new Outcome(problems, rebuilt, docs, applied);
    }

    private static void LogCheck(AppDb read, string type, OneIndexPosition p, List<string> problems)
    {
        long pruned = read.Set<OutboxState>().AsNoTracking().Select(s => s.PrunedThrough).FirstOrDefault();
        long present = read.Set<OutboxEntry>().AsNoTracking().Count(e => e.EntityType == type && e.Seq > p.Base && e.Seq <= p.Seq);
        if (pruned <= p.Base ? present != p.Count : present > p.Count)
        {
            problems.Add($"applied count {p.Count} but the log holds {present} rows in ({p.Base}, {p.Seq}] (pruned through {pruned})");
        }
    }
}

/// <summary>
/// Cycles of two application processes on one database, each with its own index. Every cycle kills both (at a random time or at a named stage),
/// sometimes lets the log be pruned past the indexes, then reopens each index, catches it up and compares it with the database.
/// </summary>
public sealed class KillCampaign(Args a)
{
    private static readonly (string Stage, int Max)[] Stages =
    [
        ("save.outbox-added", 20), ("apply.read", 10), ("apply.before-commit", 10), ("apply.after-commit", 10),
        ("reindex.start", 2), ("reindex.page", 3), ("reindex.before-commit", 2), ("reindex.after-commit", 2),
    ];

    public async Task<int> RunAsync()
    {
        string dir = Path.GetFullPath(a.Get("dir"));
        int cycles = a.Number("cycles", 20);
        int seed = a.Number("seed", 1);
        Directory.CreateDirectory(dir);
        string pg = a.Get("pg");
        string dbFile = pg.Length > 0 ? pg + ";Database=ef_" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture) : Path.Combine(dir, "app.db");
        string writers = a.Get("writers", "1");
        if (Directory.GetFileSystemEntries(dir).Length > 0)
        {
            Console.Error.WriteLine("refusing a non-empty directory " + dir);
            return 2;
        }

        using (AppDb init = AppDb.Create(dbFile, null))
        {
            init.Initialize();
        }

        string dll = System.Reflection.Assembly.GetEntryAssembly()!.Location;
        var rng = new Random(seed);
        int killsByHook = 0, killsByTimer = 0, exited = 0, failures = 0, rebuilds = 0, checks = 0, laggedCycles = 0;
        var byStage = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            var procs = new List<(string Inst, Process P, long KillAtMs, bool Hook)>();
            foreach (string inst in new[] { "a", "b" })
            {
                bool hook = rng.Next(100) < 55;
                (string stage, int max) = Stages[rng.Next(Stages.Length)];
                string kill = hook ? stage + ":" + (1 + rng.Next(max)).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                var psi = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (string s in new[] { dll, "app", "--db", dbFile, "--root", dir, "--inst", inst, "--seed", (seed * 100003 + cycle * 7 + inst[0]).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--ms", "2500", "--mode", rng.Next(3) == 0 ? "inline" : "threaded", "--writers", writers, "--kill", kill, "--retain", (5 + rng.Next(60)).ToString(System.Globalization.CultureInfo.InvariantCulture) })
                {
                    psi.ArgumentList.Add(s);
                }

                File.Delete(Path.Combine(dir, inst + ".hit"));
                procs.Add((inst, Process.Start(psi)!, 100 + rng.Next(1500), hook));
            }

            var begun = Stopwatch.StartNew();
            while (procs.Any(p => !p.P.HasExited) && begun.ElapsedMilliseconds < 6000)
            {
                foreach ((string inst, Process p, long at, bool hook) in procs)
                {
                    if (!p.HasExited && !hook && begun.ElapsedMilliseconds >= at)
                    {
                        p.Kill();
                        killsByTimer++;
                    }
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            foreach ((string inst, Process p, _, _) in procs)
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    killsByTimer++;
                }

                await p.WaitForExitAsync().ConfigureAwait(false);
                string hitFile = Path.Combine(dir, inst + ".hit");
                if (File.Exists(hitFile))
                {
                    killsByHook++;
                    string stage = File.ReadAllText(hitFile);
                    byStage[stage] = byStage.GetValueOrDefault(stage) + 1;
                }
                else if (p.ExitCode == 0)
                {
                    exited++;
                }

                if (p.ExitCode is not (0 or 137 or -9) && !File.Exists(hitFile))
                {
                    string err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(err))
                    {
                        Console.WriteLine($"cycle {cycle} {inst}: exit {p.ExitCode}: {err[..Math.Min(400, err.Length)]}");
                        failures++;
                    }
                }

                p.Dispose();
            }

            if (rng.Next(100) < 40)
            {
                // Writes the application makes while no applier runs, then retention past every index, so the next start rebuilds.
                laggedCycles++;
                using AppDb lag = AppDb.Create(dbFile, Path.Combine(dir, "lag"));
                var lagRng = new Random(rng.Next());
                for (int i = 0; i < 3 + lagRng.Next(10); i++)
                {
                    await Mutations.SaveSomeAsync(lag, lagRng, false).ConfigureAwait(false);
                }

                OneIndexRuntime.For(lag).Prune(lagRng.Next(4));
                OneIndexRuntime.For(lag).Dispose();
            }

            // Sometimes verify before the next cycle's children; always on the last cycle and every 4th, so that kills accumulate on top of kills.
            if (cycle % 4 == 0 || cycle == cycles || rng.Next(100) < 20)
            {
                foreach (string inst in new[] { "a", "b" })
                {
                    checks++;
                    Verifier.Outcome o = Verifier.Verify(dbFile, Path.Combine(dir, inst), Path.Combine(dir, inst + ".ack"));
                    rebuilds += o.Rebuilt ? 1 : 0;
                    foreach (string p in o.Problems)
                    {
                        failures++;
                        Console.WriteLine($"FAIL cycle {cycle} {inst}: {p}");
                    }
                }
            }
        }

        Console.WriteLine($"cycles {cycles} seed {seed}: kills {killsByHook + killsByTimer} (named stage {killsByHook}, timer {killsByTimer}), clean exits {exited}, checks {checks}, rebuilds {rebuilds}, lagged cycles {laggedCycles}, failures {failures}, {clock.Elapsed.TotalSeconds:F0}s");
        foreach (KeyValuePair<string, int> s in byStage)
        {
            Console.WriteLine($"  stage {s.Key}: {s.Value}");
        }

        return failures == 0 ? 0 : 1;
    }
}
