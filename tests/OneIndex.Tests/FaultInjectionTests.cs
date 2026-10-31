using OneIndex.Storage;

namespace OneIndex.Tests;

public class FaultInjectionTests
{
    private static byte[] Bytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Theory]
    [InlineData(CrashPolicy.LoseUnsynced)]
    [InlineData(CrashPolicy.Tear)]
    [InlineData(CrashPolicy.Reorder)]
    public void SyncedBytesAndDirectorySyncedNamesAlwaysSurvive(CrashPolicy policy)
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var mem = new MemoryStorage();
            var fi = new FaultInjectingStorage(mem, new FaultOptions { Seed = seed, Policy = policy });
            byte[] a = Bytes(1000, 1);
            using (IStorageWriter w = fi.Create("durable"))
            {
                w.Write(a);
                w.Sync();
            }

            fi.SyncDirectory();
            using (IStorageWriter w = fi.Create("volatile"))
            {
                w.Write(Bytes(500, 2));
            }

            fi.Crash();
            Assert.Equal(a, TestHelpers.ReadAll(mem, "durable"));
        }
    }

    [Fact]
    public void LoseUnsyncedDropsUnsyncedBytesAndNames()
    {
        var mem = new MemoryStorage();
        var fi = new FaultInjectingStorage(mem, new FaultOptions { Policy = CrashPolicy.LoseUnsynced });
        using (IStorageWriter w = fi.Create("f"))
        {
            w.Write(Bytes(100, 1));
            w.Sync();
            w.Write(Bytes(100, 2));
        }

        fi.SyncDirectory();
        using (IStorageWriter w = fi.Create("g"))
        {
            w.Write(Bytes(10, 3));
            w.Sync();
        }

        fi.Rename("g", "h");
        fi.Crash();

        // f keeps its synced first 100 bytes; g's creation was never directory-synced, so there is no g and no h.
        Assert.Equal(["f"], mem.List());
        Assert.Equal(Bytes(100, 1), TestHelpers.ReadAll(mem, "f"));
    }

    [Fact]
    public void TearKeepsAnOrderedPrefixWithAtMostOneTornWrite()
    {
        var lengths = new HashSet<int>();
        for (int seed = 0; seed < 200; seed++)
        {
            var mem = new MemoryStorage();
            var fi = new FaultInjectingStorage(mem, new FaultOptions { Seed = seed, Policy = CrashPolicy.Tear });
            var expected = new List<byte>();
            using (IStorageWriter w = fi.Create("f"))
            {
                for (int i = 0; i < 5; i++)
                {
                    byte[] chunk = Bytes(100, i);
                    expected.AddRange(chunk);
                    w.Write(chunk);
                }
            }

            fi.SyncDirectory();
            fi.Crash();
            byte[] got = TestHelpers.ReadAll(mem, "f");
            lengths.Add(got.Length);
            Assert.True(got.Length <= 500);
            Assert.Equal(expected.Take(got.Length).ToArray(), got); // a prefix of what was written
        }

        Assert.True(lengths.Count > 10, "tearing should cut at many different lengths");
    }

    [Fact]
    public void ReorderCanPersistALaterWriteWithoutAnEarlierOne()
    {
        bool sawHole = false;
        for (int seed = 0; seed < 200 && !sawHole; seed++)
        {
            var mem = new MemoryStorage();
            var fi = new FaultInjectingStorage(mem, new FaultOptions { Seed = seed, Policy = CrashPolicy.Reorder });
            byte[] first = Bytes(64, 1).Select(b => (byte)(b | 1)).ToArray();
            byte[] second = Bytes(64, 2).Select(b => (byte)(b | 1)).ToArray();
            using (IStorageWriter w = fi.Create("f"))
            {
                w.Write(first);
                w.Write(second);
            }

            fi.SyncDirectory();
            fi.Crash();
            byte[] got = TestHelpers.ReadAll(mem, "f");
            if (got.Length == 128)
            {
                // The second write is present whole; the first may be absent (zeros), torn or present.
                Assert.Equal(second, got[64..]);
                sawHole |= !got.AsSpan(0, 64).SequenceEqual(first);
            }
        }

        Assert.True(sawHole);
    }

    [Fact]
    public void SameSeedGivesTheSameSurvivingBytes()
    {
        byte[] Run(int seed)
        {
            var mem = new MemoryStorage();
            var fi = new FaultInjectingStorage(mem, new FaultOptions { Seed = seed, Policy = CrashPolicy.Reorder });
            using (IStorageWriter w = fi.Create("f"))
            {
                for (int i = 0; i < 8; i++)
                {
                    w.Write(Bytes(50, i));
                }
            }

            fi.SyncDirectory();
            fi.Crash();
            return TestHelpers.ReadAll(mem, "f");
        }

        Assert.Equal(Run(7), Run(7));
        Assert.Contains(Enumerable.Range(0, 20), s => !Run(s).AsSpan().SequenceEqual(Run(s + 1)));
    }

    [Fact]
    public void ScheduledCrashStopsTheOperationAndEverythingAfterIt()
    {
        var mem = new MemoryStorage();
        var fi = new FaultInjectingStorage(mem, new FaultOptions { CrashAtOperation = 3 });
        IStorageWriter w = fi.Create("f"); // 0
        w.Write(Bytes(10, 1)); // 1
        w.Sync(); // 2
        var crash = Assert.Throws<SimulatedCrashException>(() => w.Write(Bytes(10, 2))); // 3
        Assert.Equal(3, crash.OperationIndex);
        Assert.True(fi.HasCrashed);
        Assert.Throws<SimulatedCrashException>(() => fi.List());
        Assert.Throws<SimulatedCrashException>(() => fi.Create("g"));
        Assert.Equal(4, fi.Operations.Count); // the write that crashed was seen
        fi.Crash();
        Assert.False(fi.HasCrashed);
        Assert.Equal(0, fi.OperationCount);
        _ = fi.List();
    }

    [Fact]
    public void InjectedFailureHasNoEffect()
    {
        var mem = new MemoryStorage();
        var fi = new FaultInjectingStorage(mem, new FaultOptions { FailAtOperations = [1] });
        IStorageWriter w = fi.Create("f"); // 0
        Assert.Throws<IOException>(() => w.Write(Bytes(10, 1))); // 1 fails
        w.Write(Bytes(4, 2)); // 2
        w.Sync();
        w.Dispose();
        Assert.Equal(Bytes(4, 2), TestHelpers.ReadAll(mem, "f"));
    }

    [Fact]
    public void ExistingFilesAreTreatedAsDurable()
    {
        var mem = new MemoryStorage();
        TestHelpers.WriteAll(mem, "pre", Bytes(20, 1));
        var fi = new FaultInjectingStorage(mem, new FaultOptions());
        fi.Delete("pre");
        fi.Crash(CrashPolicy.LoseUnsynced); // the delete was never directory-synced
        Assert.Equal(Bytes(20, 1), TestHelpers.ReadAll(mem, "pre"));
    }

    [Fact]
    public void ReadOnlyStorageCannotBeWrapped() => Assert.Throws<ArgumentException>(() => new FaultInjectingStorage(new ReadOnlyStorage(new MemoryStorage())));
}
