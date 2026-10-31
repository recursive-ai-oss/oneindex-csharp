using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (string d in _directories)
        {
            Directory.Delete(d, recursive: true);
        }
    }

    public static TheoryData<string> Backends => new() { "memory", "file" };

    private IStorage Make(string kind)
    {
        if (kind == "memory")
        {
            return new MemoryStorage();
        }

        string dir = TestHelpers.NewTempDirectory();
        _directories.Add(dir);
        return new FileSystemStorage(dir);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void WriteSyncReadRoundTrip(string kind)
    {
        IStorage storage = Make(kind);
        var data = new byte[200_000];
        new Random(5).NextBytes(data);
        using (IStorageWriter w = storage.Create("a.bin"))
        {
            w.Write(data.AsSpan(0, 10));
            w.Write(data.AsSpan(10, 150_000));
            w.Write(data.AsSpan(150_010));
            Assert.Equal(200_000, w.Position);
            w.Sync();
        }

        storage.SyncDirectory();
        Assert.True(storage.Exists("a.bin"));
        using IRangeReader r = storage.OpenRead("a.bin");
        Assert.Equal(200_000, r.Length);
        var back = new byte[200_000];
        r.ReadExactly(0, back);
        Assert.Equal(data, back);

        var middle = new byte[1000];
        r.ReadExactly(77_777, middle);
        Assert.Equal(data.AsSpan(77_777, 1000).ToArray(), middle);

        // A short read happens only at the end of the file.
        var tail = new byte[50];
        Assert.Equal(20, r.Read(199_980, tail));
        Assert.Equal(0, r.Read(200_000, tail));
        Assert.Equal(0, r.Read(300_000, tail));
        Assert.Throws<CorruptIndexException>(() => r.ReadExactly(199_980, tail));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Read(-1, tail));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AsyncTwinsRoundTrip(string kind)
    {
        IStorage storage = Make(kind);
        var data = new byte[130_000];
        new Random(6).NextBytes(data);
        IStorageWriter w = await storage.CreateAsync("b.bin");
        await using (w)
        {
            await w.WriteAsync(data.AsMemory(0, 100));
            await w.WriteAsync(data.AsMemory(100));
            await w.SyncAsync();
        }

        await storage.SyncDirectoryAsync();
        Assert.True(await storage.ExistsAsync("b.bin"));
        Assert.Equal(["b.bin"], await storage.ListAsync());
        using IRangeReader r = await storage.OpenReadAsync("b.bin");
        var back = new byte[130_000];
        await r.ReadExactlyAsync(0, back);
        Assert.Equal(data, back);
        await storage.RenameAsync("b.bin", "c.bin");
        await storage.DeleteAsync("c.bin");
        Assert.False(await storage.ExistsAsync("c.bin"));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void RenameReplacesAtomicallyAndOpenReadersKeepTheirBytes(string kind)
    {
        IStorage storage = Make(kind);
        TestHelpers.WriteAll(storage, "old", "old contents"u8);
        TestHelpers.WriteAll(storage, "new", "new contents!"u8);
        using (IRangeReader before = storage.OpenRead("old"))
        {
            storage.Rename("new", "old");
            Assert.False(storage.Exists("new"));
            Assert.Equal("new contents!"u8.ToArray(), TestHelpers.ReadAll(storage, "old"));
            if (kind == "memory")
            {
                var bytes = new byte[before.Length];
                before.ReadExactly(0, bytes);
                Assert.Equal("old contents"u8.ToArray(), bytes);
            }
        }

        Assert.Throws<FileNotFoundException>(() => storage.Rename("missing", "x"));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void ListDeleteCreateRules(string kind)
    {
        IStorage storage = Make(kind);
        TestHelpers.WriteAll(storage, "b", [1]);
        TestHelpers.WriteAll(storage, "a", [2]);
        TestHelpers.WriteAll(storage, "c.x-1_2", [3]);
        Assert.Equal(["a", "b", "c.x-1_2"], storage.List());
        Assert.Throws<IOException>(() => storage.Create("a"));
        storage.Delete("a");
        storage.Delete("a");
        Assert.Equal(["b", "c.x-1_2"], storage.List());
        Assert.Throws<FileNotFoundException>(() => storage.OpenRead("a"));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void BadNamesAndCancellationAreRejected(string kind)
    {
        IStorage storage = Make(kind);
        foreach (string bad in new[] { "", ".hidden", "..", "a/b", "a\\b", "with space", "ü", new string('x', 256) })
        {
            Assert.Throws<ArgumentException>(() => storage.Exists(bad));
            Assert.Throws<ArgumentException>(() => storage.Create(bad));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => storage.List(cts.Token));
        Assert.Throws<OperationCanceledException>(() => storage.Create("z", cts.Token));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void ReadOnlyViewRejectsMutation(string kind)
    {
        IStorage inner = Make(kind);
        TestHelpers.WriteAll(inner, "a", [1, 2, 3]);
        var view = new ReadOnlyStorage(inner);
        Assert.True(view.IsReadOnly);
        Assert.Equal([1, 2, 3], TestHelpers.ReadAll(view, "a"));
        Assert.Throws<NotSupportedException>(() => view.Create("b"));
        Assert.Throws<NotSupportedException>(() => view.Delete("a"));
        Assert.Throws<NotSupportedException>(() => view.Rename("a", "b"));
        Assert.Throws<NotSupportedException>(() => view.SyncDirectory());
    }

    [Fact]
    public void ReadOnlyFileStorageNeedsAnExistingDirectory()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new FileSystemStorage(Path.Combine(Path.GetTempPath(), "oneindex-missing-" + Guid.NewGuid().ToString("N")), readOnly: true));
        string dir = TestHelpers.NewTempDirectory();
        _directories.Add(dir);
        var storage = new FileSystemStorage(dir, readOnly: true);
        Assert.True(storage.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => storage.Create("a"));
    }

    [Fact]
    public void FileStorageSyncDirectoryAfterRenameDoesNotFail()
    {
        string dir = TestHelpers.NewTempDirectory();
        _directories.Add(dir);
        var storage = new FileSystemStorage(dir);
        TestHelpers.WriteAll(storage, "t", [1]);
        storage.Rename("t", "final");
        storage.SyncDirectory();
        TestHelpers.WriteAll(storage, "u", [1]);
        storage.Delete("u");
        storage.SyncDirectory();
        Assert.Equal(["final"], storage.List());
    }

    [Fact]
    public void BlockTransformWrapperRoundTripsAndStoresTransformedBytes()
    {
        var inner = new MemoryStorage();
        var storage = new TransformingStorage(inner, new XorTransform(0x5A));
        var data = new byte[150_000];
        new Random(8).NextBytes(data);
        TestHelpers.WriteAll(storage, "f", data);
        Assert.Equal(data, TestHelpers.ReadAll(storage, "f"));

        byte[] raw = TestHelpers.ReadAll(inner, "f");
        Assert.NotEqual(data, raw);
        Assert.Equal(data.Select((b, i) => (byte)(b ^ 0x5A ^ (i & 0xFF))).ToArray(), raw);

        using IRangeReader r = storage.OpenRead("f");
        var middle = new byte[333];
        r.ReadExactly(70_001, middle);
        Assert.Equal(data.AsSpan(70_001, 333).ToArray(), middle);

        // The identity transform is a pass-through.
        var plain = new TransformingStorage(inner);
        TestHelpers.WriteAll(plain, "g", data);
        Assert.Equal(data, TestHelpers.ReadAll(inner, "g"));
    }

    private sealed class XorTransform(byte key) : IBlockTransform
    {
        public int BlockSize => 16;

        public void Encode(long fileOffset, Span<byte> data) => Apply(fileOffset, data);

        public void Decode(long fileOffset, Span<byte> data) => Apply(fileOffset, data);

        private void Apply(long offset, Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= (byte)(key ^ ((offset + i) & 0xFF));
            }
        }
    }
}
