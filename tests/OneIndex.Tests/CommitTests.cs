using System.Buffers.Binary;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

public class CommitTests
{
    private static CommitInfo Sample()
    {
        var f1 = new SegmentFile(SegmentParts.Postings, "s0.postings", new CodecRef(SegmentParts.Postings, 1), 300, 0x11223344);
        var f2 = new SegmentFile(SegmentParts.Deletes, "s0.del2", new CodecRef(SegmentParts.Deletes, 1), 40, 0x55667788);
        return new CommitInfo(
            42,
            9,
            [new SegmentInfo("s0", 100, 7, 2, [f1, f2]), new SegmentInfo("s1", 5, 0, 0, [])],
            new SchemaBuilder().Id("id").Text("body").Build().Serialize(),
            new byte[] { 1, 2, 3 },
            new Dictionary<string, string> { ["zeta"] = "z", ["alpha"] = "ä" });
    }

    [Fact]
    public void CommitFileRoundTripsAndIsDeterministic()
    {
        CommitInfo commit = Sample();
        byte[] bytes = CommitFileFormat.Serialize(commit);
        Assert.Equal(bytes, CommitFileFormat.Serialize(Sample()));

        CommitInfo back = CommitFileFormat.Parse(bytes, "c");
        Assert.Equal(42, back.Generation);
        Assert.Equal(9UL, back.NextSegmentId);
        Assert.Equal([1, 2, 3], back.AppliedPosition.ToArray());
        Assert.Equal(commit.SchemaBytes.ToArray(), back.SchemaBytes.ToArray());
        Assert.Equal(["alpha", "zeta"], back.UserData.Keys);
        Assert.Equal("ä", back.UserData["alpha"]);
        Assert.Equal(2, back.Segments.Count);
        Assert.Equal(93, back.Segments[0].LiveCount);
        Assert.Equal(2, back.Segments[0].DeleteGeneration);
        Assert.Equal(commit.Segments[0].Files, back.Segments[0].Files);
        Assert.Equal(98, back.LiveDocCount);
        Assert.Equal(bytes, CommitFileFormat.Serialize(back));
    }

    [Fact]
    public void EveryFlippedBitAndEveryTruncationIsRejectedWithTheTypedException()
    {
        byte[] bytes = CommitFileFormat.Serialize(Sample());
        for (int i = 0; i < bytes.Length; i++)
        {
            byte[] copy = (byte[])bytes.Clone();
            copy[i] ^= (byte)(1 << (i % 8));
            var e = Assert.ThrowsAny<CorruptIndexException>(() => CommitFileFormat.Parse(copy, "commit-x"));
            Assert.Equal("commit-x", e.FileName);
            Assert.Contains("commit-x", e.Message, StringComparison.Ordinal);
        }

        for (int n = 0; n < bytes.Length; n++)
        {
            Assert.ThrowsAny<CorruptIndexException>(() => CommitFileFormat.Parse(bytes.AsSpan(0, n), "commit-x"));
        }

        Assert.False(CommitFileFormat.TryParse(bytes.AsSpan(0, 10), "c", null, out CommitInfo? none, out string? error));
        Assert.Null(none);
        Assert.NotNull(error);
    }

    /// <summary>Builds a commit file with a correct frame around a body, so only the body's own validation can reject it.</summary>
    private static byte[] Framed(Action<ByteSink> body, int version = 1)
    {
        var sink = new ByteSink();
        sink.WriteUInt32(CommitFileFormat.Magic);
        sink.WriteUInt32((uint)version);
        body(sink);
        sink.WriteUInt32(IndexFile.FooterMagic);
        sink.WriteUInt32(IndexFile.AlgorithmCrc32C);
        sink.WriteUInt32(Crc32C.Compute(sink.WrittenSpan));
        return sink.ToArray();
    }

    private static void Header(ByteSink s, ulong generation = 1)
    {
        s.WriteUInt64(generation);
        s.WriteUInt64(0);
        s.WriteBytes([]);
        s.WriteBytes([]);
        s.WriteVarUInt(0);
    }

    [Fact]
    public void HostileBodiesWithValidChecksumsAreRejected()
    {
        var hostile = new Dictionary<string, byte[]>
        {
            ["huge segment count"] = Framed(s => { Header(s); s.WriteVarUInt(ulong.MaxValue); }),
            ["segment count past the limit"] = Framed(s => { Header(s); s.WriteVarUInt(1_000_000); }),
            ["name length past EOF"] = Framed(s => { Header(s); s.WriteVarUInt(1); s.WriteVarUInt(200); }),
            ["schema length past EOF"] = Framed(s => { s.WriteUInt64(1); s.WriteUInt64(0); s.WriteBytes([]); s.WriteVarUInt(1_000_000); }),
            ["applied position past the limit"] = Framed(s => { s.WriteUInt64(1); s.WriteUInt64(0); s.WriteVarUInt(5000); }),
            ["generation zero"] = Framed(s => { Header(s, 0); s.WriteVarUInt(0); }),
            ["trailing bytes"] = Framed(s => { Header(s); s.WriteVarUInt(0); s.WriteByte(0); }),
            ["deleted more than docs"] = Framed(s => { Header(s); s.WriteVarUInt(1); s.WriteString("s0"); s.WriteVarUInt(3); s.WriteVarUInt(4); }),
            ["duplicate segment"] = Framed(s =>
            {
                Header(s);
                s.WriteVarUInt(2);
                for (int i = 0; i < 2; i++)
                {
                    s.WriteString("s0");
                    s.WriteVarUInt(1);
                    s.WriteVarUInt(0);
                    s.WriteVarUInt(0);
                    s.WriteVarUInt(0);
                }
            }),
            ["path in a file name"] = Framed(s =>
            {
                Header(s);
                s.WriteVarUInt(1);
                s.WriteString("s0");
                s.WriteVarUInt(1);
                s.WriteVarUInt(0);
                s.WriteVarUInt(0);
                s.WriteVarUInt(1);
                s.WriteString("postings");
                s.WriteString("../escape");
            }),
            ["unsorted user data"] = Framed(s =>
            {
                s.WriteUInt64(1);
                s.WriteUInt64(0);
                s.WriteBytes([]);
                s.WriteBytes([]);
                s.WriteVarUInt(2);
                s.WriteString("b");
                s.WriteString("1");
                s.WriteString("a");
                s.WriteString("2");
                s.WriteVarUInt(0);
            }),
            ["invalid utf-8"] = Framed(s => { Header(s); s.WriteVarUInt(1); s.WriteVarUInt(2); s.WriteByte(0xC3); s.WriteByte(0x28); }),
            ["overlong varint"] = Framed(s => { Header(s); s.WriteByte(0x80); s.WriteByte(0x00); }),
            ["varint past 64 bits"] = Framed(s => { Header(s); for (int i = 0; i < 11; i++) { s.WriteByte(0xFF); } }),
        };
        foreach ((string what, byte[] bytes) in hostile)
        {
            var e = Record.Exception(() => CommitFileFormat.Parse(bytes, "commit-h"));
            Assert.True(e is CorruptIndexException, $"{what}: expected CorruptIndexException, got {e?.GetType().Name ?? "no exception"}");
        }
    }

    [Fact]
    public void NewerFormatAndUnknownCodecAreUnsupportedNotCorrupt()
    {
        byte[] newer = Framed(s => { Header(s); s.WriteVarUInt(0); }, version: 3);
        Assert.Throws<UnsupportedFormatException>(() => CommitFileFormat.Parse(newer, "c"));

        byte[] codec = Framed(s =>
        {
            Header(s);
            s.WriteVarUInt(1);
            s.WriteString("s0");
            s.WriteVarUInt(1);
            s.WriteVarUInt(0);
            s.WriteVarUInt(0);
            s.WriteVarUInt(1);
            s.WriteString("postings");
            s.WriteString("s0.p");
            s.WriteString("postings");
            s.WriteVarUInt(99);
            s.WriteVarUInt(10);
            s.WriteUInt32(0);
        });
        Assert.Throws<UnsupportedFormatException>(() => CommitFileFormat.Parse(codec, "c"));
        Assert.Throws<UnsupportedFormatException>(() => CommitFileFormat.Serialize(new CommitInfo(1, 0, [new SegmentInfo("s", 1, 0, 0, [new SegmentFile("x", "s.x", new CodecRef("nope", 1), 1, 0)])])));
    }

    [Fact]
    public void LimitsAreEnforcedOnBothSides()
    {
        var tight = new IndexLimits { MaxSegments = 1 };
        var two = new CommitInfo(1, 0, [new SegmentInfo("a", 1, 0, 0, []), new SegmentInfo("b", 1, 0, 0, [])]);
        Assert.Throws<ArgumentException>(() => CommitFileFormat.Serialize(two, tight));
        byte[] bytes = CommitFileFormat.Serialize(two);
        Assert.Throws<CorruptIndexException>(() => CommitFileFormat.Parse(bytes, "c", tight));
        Assert.Throws<CorruptIndexException>(() => CommitFileFormat.Parse(bytes, "c", new IndexLimits { MaxCommitFileBytes = 20 }));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    public async Task CommitProtocolPublishesAndOpensLatest(string kind)
    {
        string? dir = null;
        try
        {
            IStorage storage;
            if (kind == "memory")
            {
                storage = new MemoryStorage();
            }
            else
            {
                dir = TestHelpers.NewTempDirectory();
                storage = new FileSystemStorage(dir);
            }

            Assert.Null(CommitProtocol.OpenLatest(storage));
            Assert.False(CommitProtocol.TryOpenLatest(storage, null, out CommitInfo? none, out CorruptIndexException? noError));
            Assert.Null(none);
            Assert.Null(noError);

            SegmentFile f0 = TestHelpers.WriteSegmentFile(storage, SegmentParts.Postings, "s0.postings", 1);
            CommitProtocol.Commit(storage, TestHelpers.MakeCommit(1, [new SegmentInfo("s0", 10, 0, 0, [f0])]));
            SegmentFile f1 = TestHelpers.WriteSegmentFile(storage, SegmentParts.Postings, "s1.postings", 2);
            await CommitProtocol.CommitAsync(storage, TestHelpers.MakeCommit(2, [new SegmentInfo("s0", 10, 0, 0, [f0]), new SegmentInfo("s1", 4, 1, 0, [f1])]));

            Assert.DoesNotContain(storage.List(), n => n.EndsWith(".tmp", StringComparison.Ordinal));
            CommitInfo latest = CommitProtocol.OpenLatest(storage)!;
            Assert.Equal(2, latest.Generation);
            Assert.Equal(13, latest.LiveDocCount);
            CommitInfo asyncLatest = (await CommitProtocol.OpenLatestAsync(storage))!;
            Assert.Equal(latest.Generation, asyncLatest.Generation);
            CommitProtocol.VerifyFiles(storage, latest);

            // A generation that does not advance is refused.
            Assert.Throws<InvalidOperationException>(() => CommitProtocol.Commit(storage, TestHelpers.MakeCommit(2, [])));
            // A commit may not list a file that is not there or is not the recorded one.
            var ghost = new SegmentFile(SegmentParts.Postings, "ghost.postings", new CodecRef(SegmentParts.Postings, 1), 100, 1);
            Assert.Throws<FileNotFoundException>(() => CommitProtocol.Commit(storage, TestHelpers.MakeCommit(3, [new SegmentInfo("g", 1, 0, 0, [ghost])])));
            var wrong = f1 with { Checksum = f1.Checksum ^ 1 };
            Assert.Throws<CorruptIndexException>(() => CommitProtocol.Commit(storage, TestHelpers.MakeCommit(3, [new SegmentInfo("s1", 4, 1, 0, [wrong])])));
            Assert.Equal(2, CommitProtocol.OpenLatest(storage)!.Generation);
        }
        finally
        {
            if (dir is not null)
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void OpenLatestNamesTheDamagedFile()
    {
        var storage = new MemoryStorage();
        SegmentFile f = TestHelpers.WriteSegmentFile(storage, SegmentParts.Postings, "s0.postings", 1);
        CommitProtocol.Commit(storage, TestHelpers.MakeCommit(1, [new SegmentInfo("s0", 1, 0, 0, [f])]));

        // Truncated segment file.
        byte[] seg = TestHelpers.ReadAll(storage, "s0.postings");
        storage.Delete("s0.postings");
        TestHelpers.WriteAll(storage, "s0.postings", seg.AsSpan(0, seg.Length - 5));
        var e = Assert.Throws<CorruptIndexException>(() => CommitProtocol.OpenLatest(storage));
        Assert.Equal("s0.postings", e.FileName);
        Assert.False(CommitProtocol.TryOpenLatest(storage, null, out _, out CorruptIndexException? error));
        Assert.Equal("s0.postings", error!.FileName);

        // Same length, flipped body byte: the footer still matches the commit, so only the deep check sees it.
        seg[50] ^= 1;
        storage.Delete("s0.postings");
        TestHelpers.WriteAll(storage, "s0.postings", seg);
        CommitInfo commit = CommitProtocol.OpenLatest(storage)!;
        var deep = Assert.Throws<CorruptIndexException>(() => CommitProtocol.VerifyFiles(storage, commit));
        Assert.Equal("s0.postings", deep.FileName);

        // Missing segment file.
        storage.Delete("s0.postings");
        Assert.Equal("s0.postings", Assert.Throws<CorruptIndexException>(() => CommitProtocol.OpenLatest(storage)).FileName);

        // A damaged newest commit is reported, not replaced by an older one.
        var s2 = new MemoryStorage();
        SegmentFile g = TestHelpers.WriteSegmentFile(s2, SegmentParts.Postings, "a.postings", 1);
        CommitProtocol.Commit(s2, TestHelpers.MakeCommit(1, [new SegmentInfo("a", 1, 0, 0, [g])]));
        CommitProtocol.Commit(s2, TestHelpers.MakeCommit(2, [new SegmentInfo("a", 1, 0, 0, [g])]));
        byte[] c2 = TestHelpers.ReadAll(s2, CommitFileNames.For(2));
        c2[20] ^= 0xFF;
        s2.Delete(CommitFileNames.For(2));
        TestHelpers.WriteAll(s2, CommitFileNames.For(2), c2);
        Assert.Equal(CommitFileNames.For(2), Assert.Throws<CorruptIndexException>(() => CommitProtocol.OpenLatest(s2)).FileName);
    }

    [Fact]
    public void CommitFileNamesParseOnlyTheExactForm()
    {
        Assert.Equal("commit-000000000000002a.oic", CommitFileNames.For(42));
        Assert.True(CommitFileNames.TryParse("commit-000000000000002a.oic", out long g));
        Assert.Equal(42, g);
        foreach (string bad in new[] { "commit-000000000000002A.oic", "commit-2a.oic", "commit-000000000000002a.tmp", "commit-0000000000000000.oic", "xcommit-000000000000002a.oic", "commit-000000000000002g.oic", "commit-ffffffffffffffff.oic" })
        {
            Assert.False(CommitFileNames.TryParse(bad, out _), bad);
        }
    }

    [Fact]
    public void IndexFileFrameDetectsEveryDamage()
    {
        var storage = new MemoryStorage();
        SegmentFile f = TestHelpers.WriteSegmentFile(storage, SegmentParts.Terms, "t", 3);
        byte[] bytes = TestHelpers.ReadAll(storage, "t");
        Assert.Equal(f.Length, bytes.Length);
        Assert.Equal(1, IndexFile.Validate("t", bytes, TestHelpers.TestMagic, 1));
        Assert.Throws<CorruptIndexException>(() => IndexFile.Validate("t", bytes, IndexFile.Magic("NOPE"), 1));
        Assert.Throws<UnsupportedFormatException>(() =>
        {
            byte[] v = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(v.Length - 4), Crc32C.Compute(v.AsSpan(0, v.Length - 4)));
            IndexFile.Validate("t", v, TestHelpers.TestMagic, 1);
        });
        for (int i = 0; i < bytes.Length; i++)
        {
            byte[] c = (byte[])bytes.Clone();
            c[i] ^= 0x10;
            Assert.ThrowsAny<CorruptIndexException>(() => IndexFile.Validate("t", c, TestHelpers.TestMagic, 1));
        }

        using IRangeReader reader = storage.OpenRead("t");
        Assert.Equal(f.Checksum, IndexFile.ReadFooterChecksum(reader));
        Assert.Equal(f.Checksum, IndexFile.VerifyChecksum(reader));
    }

    [Fact]
    public void Crc32CMatchesTheKnownVectorAndIsIncremental()
    {
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));
        var data = new byte[1000];
        new Random(3).NextBytes(data);
        uint whole = Crc32C.Compute(data);
        for (int split = 0; split <= data.Length; split += 37)
        {
            Assert.Equal(whole, Crc32C.Update(Crc32C.Compute(data.AsSpan(0, split)), data.AsSpan(split)));
        }
    }
}
