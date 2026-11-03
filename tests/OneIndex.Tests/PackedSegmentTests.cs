using OneIndex.Durability;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>Segments whose parts share one pack file: the commit format (version 3), the writer, reading, merging and damage.</summary>
public class PackedSegmentTests
{
    private static Workload.Op Put(string id, string tag = "t0", string body = "b", long n = 1) => new(Workload.Kind.Upsert, id, tag, body, n);

    // A pack of two parts written by hand: the frame, then each part's framed bytes.
    private static (SegmentFile A, SegmentFile B) WritePack(MemoryStorage storage, string name)
    {
        byte[] a = Part(storage, 1);
        byte[] b = Part(storage, 2);
        using var pack = new IndexFileWriter(storage.Create(name), IndexFile.Magic(PackedFiles.MagicTag), PackedFiles.FormatVersion);
        long offA = pack.Position;
        pack.Write(a);
        long offB = pack.Position;
        pack.Write(b);
        pack.Finish();
        return (
            new SegmentFile(SegmentParts.Postings, name, new CodecRef(SegmentParts.Postings, 1), a.Length, Footer(a), offA),
            new SegmentFile(SegmentParts.Stored, name, new CodecRef(SegmentParts.Stored, 1), b.Length, Footer(b), offB));
    }

    private static byte[] Part(MemoryStorage scratch, int seed)
    {
        SegmentFile f = TestHelpers.WriteSegmentFile(scratch, SegmentParts.Postings, "tmp" + seed, seed);
        byte[] bytes = TestHelpers.ReadAll(scratch, f.FileName);
        scratch.Delete(f.FileName);
        return bytes;
    }

    private static uint Footer(byte[] part) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(part.AsSpan(part.Length - 4));

    [Fact]
    public void PackedFilesRoundTripAsVersion3AndAreCanonical()
    {
        var storage = new MemoryStorage();
        (SegmentFile a, SegmentFile b) = WritePack(storage, "s0.pack");
        CommitInfo commit = TestHelpers.MakeCommit(1, [new SegmentInfo("s0", 5, 0, 0, [a, b])]);
        byte[] bytes = CommitFileFormat.Serialize(commit);
        Assert.Equal(3u, BitConverter.ToUInt32(bytes, 4));
        CommitInfo back = CommitFileFormat.Parse(bytes, "c");
        Assert.Equal(commit.Segments[0].Files, back.Segments[0].Files);
        Assert.Equal(["s0.pack"], back.Segments[0].FileNames);
        Assert.True(CommitFileFormat.Serialize(back).AsSpan().SequenceEqual(bytes));

        CommitProtocol.Commit(storage, commit);
        CommitProtocol.VerifyFiles(storage, CommitProtocol.OpenLatest(storage)!);
    }

    [Fact]
    public void ADamagedOrShortPackNamesThePackFile()
    {
        var storage = new MemoryStorage();
        (SegmentFile a, SegmentFile b) = WritePack(storage, "s0.pack");
        CommitProtocol.Commit(storage, TestHelpers.MakeCommit(1, [new SegmentInfo("s0", 5, 0, 0, [a, b])]));

        byte[] pack = TestHelpers.ReadAll(storage, "s0.pack");
        byte[] bodyHit = (byte[])pack.Clone();
        bodyHit[(int)a.Offset + 20] ^= 0xFF;
        storage.Delete("s0.pack");
        TestHelpers.WriteAll(storage, "s0.pack", bodyHit);
        CorruptIndexException body = Assert.ThrowsAny<CorruptIndexException>(() => CommitProtocol.VerifyFiles(storage, CommitProtocol.OpenLatest(storage)!));
        Assert.Contains("s0.pack", body.Message, StringComparison.Ordinal);

        storage.Delete("s0.pack");
        TestHelpers.WriteAll(storage, "s0.pack", pack.AsSpan(0, pack.Length - 20));
        CorruptIndexException shortPack = Assert.ThrowsAny<CorruptIndexException>(() => CommitProtocol.OpenLatest(storage));
        Assert.Contains("s0.pack", shortPack.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Version3NeedsAPackedFileAndPackNamesNeverClash()
    {
        var storage = new MemoryStorage();
        (SegmentFile a, SegmentFile b) = WritePack(storage, "s0.pack");
        SegmentFile plain = TestHelpers.WriteSegmentFile(new MemoryStorage(), SegmentParts.Terms, "s0.pack", 3);
        Assert.Throws<ArgumentException>(() => CommitFileFormat.Serialize(TestHelpers.MakeCommit(1, [new SegmentInfo("s0", 5, 0, 0, [a, plain])])));
        Assert.Throws<ArgumentException>(() => CommitFileFormat.Serialize(TestHelpers.MakeCommit(1, [new SegmentInfo("s0", 5, 0, 0, [a, a with { Part = SegmentParts.Terms }])])));
        Assert.Throws<ArgumentException>(() => CommitFileFormat.Serialize(TestHelpers.MakeCommit(1, [new SegmentInfo("s0", 5, 0, 0, [a with { Offset = 3 }])])));

        // Version 3 without any offset is not the canonical form of anything.
        byte[] plainCommit = CommitFileFormat.Serialize(TestHelpers.MakeCommit(1, [new SegmentInfo("s0", 5, 0, 0, [plain])]));
        Assert.Equal(1u, BitConverter.ToUInt32(plainCommit, 4));
        byte[] bumped = (byte[])plainCommit.Clone();
        bumped[4] = 3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bumped.AsSpan(bumped.Length - 4), Crc32C.Compute(bumped.AsSpan(0, bumped.Length - 4)));
        Assert.ThrowsAny<CorruptIndexException>(() => CommitFileFormat.Parse(bumped, "c"));
    }

    [Fact]
    public void ASmallFlushWritesOnePackFileAndSearchesAfterReopenAndMerge()
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 3, autoMerge: false)))
        {
            for (int i = 0; i < 9; i++)
            {
                w.Upsert(Workload.ToDocument(Put("d" + i, i % 2 == 0 ? "even" : "odd", "body" + i, i)));
            }

            w.Commit();
        }

        string[] listing = [.. storage.List()];
        Assert.Equal(3, listing.Count(n => n.EndsWith(".pack", StringComparison.Ordinal)));
        Assert.DoesNotContain(listing, n => n.EndsWith(".postings", StringComparison.Ordinal) || n.EndsWith(".ids", StringComparison.Ordinal));
        // Vector parts keep files of their own; every text, column, point, stored and identifier part sits in the pack.
        Assert.DoesNotContain(listing, n => n.EndsWith(".terms", StringComparison.Ordinal) || n.EndsWith(".stored", StringComparison.Ordinal) || n.EndsWith(".columns", StringComparison.Ordinal));

        SortedDictionary<string, string> before;
        using (IndexSnapshot snap = IndexSnapshot.Open(storage, Workload.Codecs))
        {
            snap.Verify();
            before = Workload.ReadContent(snap);
            Assert.Equal(9, before.Count);
        }

        // Merge the packed segments, flush another packed one next to the merged (unpacked) segment, merge again.
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 3, autoMerge: false)))
        {
            w.ForceMerge(1);
            w.Upsert(Workload.ToDocument(Put("x1", "even", "new", 50)));
            w.Commit();
            w.ForceMerge(1);
            w.Commit();
        }

        using (IndexSnapshot merged = IndexSnapshot.Open(storage, Workload.Codecs))
        {
            merged.Verify();
            SortedDictionary<string, string> after = Workload.ReadContent(merged);
            Assert.Equal(10, after.Count);
            foreach (KeyValuePair<string, string> kv in before)
            {
                Assert.Equal(kv.Value, after[kv.Key]);
            }
        }

        Assert.Null(Workload.FindLeaks(storage));
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void AnIndexWrittenByTheEarlierFormatOpensSearchesAndMergesIntoPackedSegments()
    {
        // testdata/format2-index was written by the previous release's writer (commit format 2: one file per segment part).
        string source = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "testdata", "format2-index");
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            foreach (string f in Directory.GetFiles(source))
            {
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            }

            var storage = new FileSystemStorage(dir);
            SortedDictionary<string, string> before;
            using (IndexSnapshot snap = IndexSnapshot.Open(storage, Workload.Codecs))
            {
                Assert.DoesNotContain(storage.List(), n => n.EndsWith(".pack", StringComparison.Ordinal));
                snap.Verify();
                before = Workload.ReadContent(snap);
                Assert.NotEmpty(before);
                Assert.Null(Workload.CheckSearch(snap, before));
            }

            using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 3, autoMerge: false)))
            {
                w.Upsert(Workload.ToDocument(Put("x1", "even", "fresh", 77)));
                w.Commit();
                Assert.Contains(storage.List(), n => n.EndsWith(".pack", StringComparison.Ordinal));
                w.ForceMerge(1);
                w.Commit();
            }

            using (IndexSnapshot merged = IndexSnapshot.Open(storage, Workload.Codecs))
            {
                merged.Verify();
                SortedDictionary<string, string> after = Workload.ReadContent(merged);
                Assert.Equal(before.Count + 1, after.Count);
                foreach (KeyValuePair<string, string> kv in before)
                {
                    Assert.Equal(kv.Value, after[kv.Key]);
                }

                Assert.Null(Workload.CheckSearch(merged, after));
            }

            Assert.Null(Workload.FindLeaks(storage));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
