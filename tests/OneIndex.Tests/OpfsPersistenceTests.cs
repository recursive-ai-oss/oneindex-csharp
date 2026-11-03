using OneIndex.Browser;
using OneIndex.Storage;

namespace OneIndex.Tests;

public class OpfsPersistenceTests
{
    private sealed class CrashException : Exception;

    /// <summary>An OPFS directory held in a dictionary; each write or delete is atomic, and it can "crash" at the Nth change.</summary>
    private sealed class FakeHost(Dictionary<string, byte[]> files, int crashAt = int.MaxValue) : IOpfsHost
    {
        private int _ops;

        public Dictionary<string, byte[]> Files { get; } = files;

        private void Tick()
        {
            if (_ops++ >= crashAt)
            {
                throw new CrashException();
            }
        }

        public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([.. Files.Keys]);

        public ValueTask<byte[]> ReadAsync(string name, CancellationToken cancellationToken = default)
            => Files.TryGetValue(name, out byte[]? b) ? ValueTask.FromResult(b) : throw new FileNotFoundException(name);

        public ValueTask WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Tick();
            Files[name] = bytes.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            Tick();
            Files.Remove(name);
            return ValueTask.CompletedTask;
        }
    }

    private static long CountAfterLoad(Dictionary<string, byte[]> files)
    {
        var memory = new MemoryStorage();
        LoadAndCheck(files, memory);
        using SearchIndex<StaticDoc> index = SearchIndex.OpenReadOnly<StaticDoc>(memory);
        return index.Count;
    }

    private static void LoadAndCheck(Dictionary<string, byte[]> files, MemoryStorage memory)
        => OpfsPersistence.LoadAsync(new FakeHost(files), memory).AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task SaveLoadRoundTripAndCrashAtEveryChangeLeavesAnOpenableCommit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oneindex-opfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            List<StaticDoc> all = StaticIndexTests.Corpus(250);
            using SearchIndex<StaticDoc> writer = SearchIndex.Open<StaticDoc>(dir, null);
            writer.Upsert(all.Take(100));
            writer.Commit();
            var host = new FakeHost([]);
            await OpfsPersistence.SaveAsync(new FileSystemStorage(dir, readOnly: true), host);
            Assert.Equal(100, CountAfterLoad(host.Files));

            writer.Upsert(all.Skip(100).Take(150));
            writer.Commit();
            var disk = new FileSystemStorage(dir, readOnly: true);

            int crashes = 0;
            for (int k = 0; k < 400; k++)
            {
                var copy = host.Files.ToDictionary(static kv => kv.Key, static kv => kv.Value);
                var crashing = new FakeHost(copy, k);
                bool crashed = false;
                try
                {
                    await OpfsPersistence.SaveAsync(disk, crashing);
                }
                catch (CrashException)
                {
                    crashed = true;
                    crashes++;
                }

                long n = CountAfterLoad(copy);
                Assert.True(n is 100 or 250, $"crash at change {k}: {n} documents");
                if (!crashed)
                {
                    Assert.Equal(250, n);
                    break;
                }
            }

            Assert.True(crashes > 3);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
