using Microsoft.EntityFrameworkCore;
using OneIndex.EfDurability;

namespace OneIndex.EntityFrameworkCore.Tests;

/// <summary>An index that fails while committing must not wedge the applier for the life of the process.</summary>
public class RecoveryTests
{
    [Fact]
    public async Task AnApplierRecoversAfterTheIndexFailedToCommit()
    {
        using var env = new Env();
        using AppDb db = env.Open();
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        db.Invoices.Add(Env.Make("a", "alpha"));
        await db.SaveChangesAsync();
        await rt.CatchUpAsync();

        // Make the next commit file impossible to create: a directory sits where its temporary file goes. The segment files are
        // written first, so the failure lands inside the commit protocol, which is the case that stops an index writer for good.
        string indexDir = Path.Combine(env.Root, nameof(Invoice));
        long latest = Directory.EnumerateFiles(indexDir, "commit-*.oic").Select(f => Convert.ToInt64(Path.GetFileName(f).Substring(7, 16), 16)).Max();
        string blocker = Path.Combine(indexDir, $"commit-{latest + 1:x16}.tmp");
        Directory.CreateDirectory(blocker);

        db.Invoices.Add(Env.Make("b", "beta"));
        await db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => rt.CatchUpAsync().AsTask());

        // The disk is fine again: the next catch-up must work without restarting the process.
        Directory.Delete(blocker);
        await rt.CatchUpAsync();
        Assert.Equal(["a", "b"], rt.Index<Invoice>().CommittedIds().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheApplierLoopNeverPrunesIntoTheLateRowWindow()
    {
        // Retention smaller than the window would delete the log row of a transaction that is still open and commits later; the loop keeps the window.
        using var env = new Env();
        using AppDb db = env.Open(o =>
        {
            o.RetainRows = 1;
            o.LateRowWindow = 50;
            o.PollInterval = TimeSpan.FromMilliseconds(10);
        });
        OneIndexRuntime rt = OneIndexRuntime.For(db);
        for (int i = 0; i < 20; i++)
        {
            db.Invoices.Add(Env.Make("i" + i, "title " + i));
            await db.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource();
        Task loop = rt.RunAsync(cts.Token);
        for (int wait = 0; wait < 200 && (rt.GetPosition<Invoice>()?.Seq ?? 0) < 20; wait++)
        {
            await Task.Delay(25);
        }

        await Task.Delay(200);
        await cts.CancelAsync();
        await loop;
        using AppDb check = AppDb.Create(env.Db, null);
        Assert.Equal(0, OutboxStore.ReadState(check).PrunedThrough);
        Assert.Equal(20, await check.Set<OutboxEntry>().CountAsync());
    }
}
