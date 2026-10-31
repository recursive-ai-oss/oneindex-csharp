using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

internal static class TestHelpers
{
    public static readonly uint TestMagic = IndexFile.Magic("TEST");

    public static string NewTempDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "oneindex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static byte[] ReadAll(IStorage storage, string name)
    {
        using IRangeReader reader = storage.OpenRead(name);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(0, bytes);
        return bytes;
    }

    public static void WriteAll(IStorage storage, string name, ReadOnlySpan<byte> data, bool sync = true)
    {
        using IStorageWriter writer = storage.Create(name);
        writer.Write(data);
        if (sync)
        {
            writer.Sync();
        }
    }

    /// <summary>Writes a small framed segment file the way a codec would: header, body in two pieces, footer, fsync.</summary>
    public static SegmentFile WriteSegmentFile(IStorage storage, string part, string fileName, int seed)
    {
        var body = new byte[200 + seed];
        new Random(seed).NextBytes(body);
        using var writer = new IndexFileWriter(storage.Create(fileName), TestMagic, 1);
        writer.Write(body.AsSpan(0, 100));
        writer.Write(body.AsSpan(100));
        IndexFileSummary summary = writer.Finish();
        return new SegmentFile(part, fileName, new CodecRef(part, 1), summary.Length, summary.Checksum);
    }

    public static CommitInfo MakeCommit(long generation, IReadOnlyList<SegmentInfo> segments)
        => new(generation, (ulong)generation + 10, segments, new SchemaBuilder().Id("id").Text("body").Build().Serialize(), BitConverter.GetBytes(generation * 7), new Dictionary<string, string> { ["gen"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static readonly object NoGcGate = new();

    /// <summary>
    /// Asserts that <paramref name="operation"/> allocates exactly nothing, measured with the thread's allocation counter over many
    /// windows, each inside a no-GC region. The counter is exact for the thread only while no collection runs: tests run in
    /// parallel, and a collection started by another thread (background collections included) makes the counter jump by several
    /// KB without this thread allocating anything. A no-GC region keeps collections out of the window; a window in which the region
    /// could not be held is thrown away and measured again. Every window that was held must read exactly zero, so an operation that
    /// allocates, even once per call, fails all of its windows.
    /// </summary>
    /// <param name="what">What is measured, for the failure message.</param>
    /// <param name="operation">The operation; it is warmed up first and then run once per window.</param>
    /// <param name="windows">How many held windows to require.</param>
    /// <param name="maxAttempts">How many windows to try before giving up.</param>
    /// <returns>The number of held windows measured.</returns>
    public static int AssertNoAllocation(string what, Action operation, int windows = 30, int maxAttempts = 300)
    {
        for (int i = 0; i < 40; i++)
        {
            operation();
        }

        int held = 0;
        for (int attempt = 0; attempt < maxAttempts && held < windows; attempt++)
        {
            long allocated = 0;
            bool kept = false;
            lock (NoGcGate)
            {
                if (!GC.TryStartNoGCRegion(16 * 1024 * 1024))
                {
                    continue;
                }

                try
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    operation();
                    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    kept = System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion;
                }
                finally
                {
                    // Always end the region, also when the operation throws: if other threads' allocations overran the budget the
                    // runtime has already left the mode but keeps its "in progress" flag until EndNoGCRegion is called (which then
                    // throws), and the next start would fail.
                    try
                    {
                        GC.EndNoGCRegion();
                    }
                    catch (InvalidOperationException)
                    {
                        kept = false;
                    }
                }
            }

            if (!kept)
            {
                continue;
            }

            Assert.True(allocated == 0, $"{what} allocated {allocated} bytes in a window held free of collections");
            held++;
        }

        Assert.True(held >= windows, $"{what}: only {held} of {windows} windows could be held free of collections in {maxAttempts} attempts");
        return held;
    }
}
