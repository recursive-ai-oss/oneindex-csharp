using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OneIndex.EntityFrameworkCore;

namespace OneIndex.EfDurability;

/// <summary>Indicative overhead numbers: SaveChanges without and with the interceptor, with inline apply, and the applier's throughput.</summary>
public static class Bench
{
    public static async Task<int> RunAsync(Args a)
    {
        string dir = Path.GetFullPath(a.Get("dir"));
        int n = a.Number("rows", 2000);
        Directory.CreateDirectory(dir);
        double plain = await SavesAsync(Path.Combine(dir, "plain.db"), null, n, update: false, inline: false).ConfigureAwait(false);
        double intercepted = await SavesAsync(Path.Combine(dir, "intercepted.db"), Path.Combine(dir, "idx-intercepted"), n, update: false, inline: false).ConfigureAwait(false);
        double inlined = await SavesAsync(Path.Combine(dir, "inline.db"), Path.Combine(dir, "idx-inline"), n, update: false, inline: true).ConfigureAwait(false);
        Console.WriteLine($"SaveChanges, one insert per save, {n} saves: plain {plain / n:F3} ms, with outbox row {intercepted / n:F3} ms, inline apply {inlined / n:F3} ms");

        // Applier throughput: a full rebuild of n rows, then replay of n logged updates.
        string db = Path.Combine(dir, "apply.db");
        string root = Path.Combine(dir, "idx-apply");
        using (AppDb init = AppDb.Create(db, null))
        {
            init.Initialize();
        }

        var rng = new Random(7);
        await FillAsync(db, root, n, rng, update: false).ConfigureAwait(false);
        using AppDb ctx = AppDb.Create(db, root, o => o.RetainRows = 0);
        OneIndexRuntime rt = OneIndexRuntime.For(ctx);
        var sw = Stopwatch.StartNew();
        rt.CatchUp();
        double rebuild = sw.Elapsed.TotalSeconds;
        await FillAsync(db, root, n, rng, update: true).ConfigureAwait(false);
        sw.Restart();
        int applied = rt.CatchUp();
        double replay = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"applier: rebuild {n} rows in {rebuild:F2} s ({n / rebuild:F0} rows/s); replay {applied} log rows in {replay:F2} s ({applied / replay:F0} rows/s, one index commit per 500-row batch)");
        rt.Dispose();
        return 0;
    }

    private static async Task<double> SavesAsync(string db, string? root, int n, bool update, bool inline)
    {
        using (AppDb init = AppDb.Create(db, null))
        {
            init.Initialize();
        }

        var rng = new Random(3);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            using AppDb w = AppDb.Create(db, root, o => o.ApplyInline = inline);
            w.Invoices.Add(new Invoice { Id = "inv-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), Title = "hosting " + rng.Next(100), Status = "open", Amount = i, Version = update ? 2 : 1, Meta = System.Text.Json.JsonDocument.Parse("{}").RootElement });
            await w.SaveChangesAsync().ConfigureAwait(false);
        }

        double ms = sw.Elapsed.TotalMilliseconds;
        if (root is not null)
        {
            using AppDb d = AppDb.Create(db, root);
            OneIndexRuntime.For(d).Dispose();
        }

        return ms;
    }

    private static async Task FillAsync(string db, string root, int n, Random rng, bool update)
    {
        for (int i = 0; i < n; i += 100)
        {
            using AppDb w = AppDb.Create(db, root);
            for (int j = i; j < Math.Min(n, i + 100); j++)
            {
                string id = "inv-" + j.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
                Invoice? row = update ? await w.Invoices.FindAsync(id).ConfigureAwait(false) : null;
                if (row is null)
                {
                    w.Invoices.Add(new Invoice { Id = id, Title = "hosting " + rng.Next(100), Status = "open", Amount = j, Version = 1, Meta = System.Text.Json.JsonDocument.Parse("{}").RootElement });
                }
                else
                {
                    row.Version++;
                    row.Amount += 1;
                }
            }

            await w.SaveChangesAsync().ConfigureAwait(false);
        }
    }
}
