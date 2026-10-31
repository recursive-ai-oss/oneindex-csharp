using OneIndex.Durability;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

public class IndexWriterTests
{
    private static readonly string[] Deleted = ["d1", "d5", "d9"];

    private static Workload.Op Put(string id, string tag = "t0", string body = "b", long n = 1) => new(Workload.Kind.Upsert, id, tag, body, n);

    private static SortedDictionary<string, string> Content(IStorage storage)
    {
        using IndexSnapshot snap = IndexSnapshot.Open(storage, Workload.Codecs);
        snap.Verify();
        return Workload.ReadContent(snap);
    }

    private static Dictionary<string, byte[]> Files(MemoryStorage storage)
        => storage.List().ToDictionary(n => n, n => TestHelpers.ReadAll(storage, n), StringComparer.Ordinal);

    [Fact]
    public void UpsertDeleteAndCommitSurviveReopen()
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options()))
        {
            for (int i = 0; i < 20; i++)
            {
                w.Upsert(Workload.ToDocument(Put("d" + i, i % 2 == 0 ? "even" : "odd", "body" + i, i)));
            }

            w.Upsert(Workload.ToDocument(Put("d3", "odd", "replaced", 33)));
            Assert.True(w.DeleteById("d4"));
            Assert.False(w.DeleteById("missing"));
            Assert.Equal(1, w.Commit());
            Assert.Equal(10, w.DeleteByTerm("tag", "odd")); // every odd document, d3 (replaced, still odd) included
            Assert.Equal(2, w.Commit());
        }

        SortedDictionary<string, string> rows = Content(storage);
        Assert.DoesNotContain("d3", rows.Keys);
        Assert.DoesNotContain("d4", rows.Keys);
        Assert.Equal(Workload.Fingerprint("even", "body6", 6), rows["d6"]);
        Assert.Equal(9, rows.Count); // the ten even documents minus the deleted d4
        Assert.All(rows.Values, v => Assert.StartsWith("even|", v, StringComparison.Ordinal));
    }

    [Fact]
    public void DeleteByTermReachesCommittedAndBufferedDocuments()
    {
        var storage = new MemoryStorage();
        using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 4));
        for (int i = 0; i < 10; i++)
        {
            w.Upsert(Workload.ToDocument(Put("d" + i, i % 2 == 0 ? "even" : "odd", "x", i)));
        }

        w.Commit();
        w.Upsert(Workload.ToDocument(Put("e1", "odd")));
        Assert.Equal(6, w.DeleteByTerm("tag", "odd")); // five committed, one buffered
        w.Commit();
        SortedDictionary<string, string> rows = Content(storage);
        Assert.Equal(5, rows.Count);
        Assert.All(rows.Values, v => Assert.StartsWith("even|", v, StringComparison.Ordinal));
    }

    [Fact]
    public void AddRequiresNoIdentifierAndUpsertKeepsIdentifiersUnique()
    {
        using IndexWriter w = IndexWriter.Open(new MemoryStorage(), Workload.Schema, Workload.Options());
        Assert.Throws<InvalidOperationException>(() => w.Add(Workload.ToDocument(Put("a"))));
        Assert.Throws<ArgumentException>(() => w.Upsert(new Document().Add("id", "x").Add("nope", "y")));
    }

    [Fact]
    public void SnapshotKeepsReadingWhileTheWriterCommitsAndMergesPastIt()
    {
        var storage = new MemoryStorage();
        using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 5, autoMerge: false));
        for (int i = 0; i < 30; i++)
        {
            w.Upsert(Workload.ToDocument(Put("d" + i, "t", "v1", i)));
        }

        w.Commit();
        IndexSnapshot snap = w.OpenSnapshot();
        SortedDictionary<string, string> before = Workload.ReadContent(snap);
        string[] pinned = snap.Commit.Segments.SelectMany(s => s.Files.Select(f => f.FileName)).Append(CommitFileNames.For(snap.Generation)).ToArray();

        for (int i = 0; i < 30; i += 2)
        {
            w.DeleteById("d" + i);
        }

        w.Upsert(Workload.ToDocument(Put("new", "t", "v2", 0)));
        w.Commit();
        w.ForceMerge(1);
        w.Commit();
        Assert.Equal(1, w.SegmentCount);

        Assert.All(pinned, n => Assert.True(storage.Exists(n), n + " was deleted while pinned"));
        Assert.Equal(before, Workload.ReadContent(snap));
        snap.Dispose();
        IEnumerable<string> stale = pinned.Where(n => storage.Exists(n));
        Assert.Empty(stale);
        Assert.Null(Workload.FindLeaks(storage));
    }

    [Fact]
    public void DeletesMadeWhileAMergeRunsAreReappliedToItsResult()
    {
        var storage = new MemoryStorage();
        var scheduler = new CooperativeMergeScheduler();
        using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(scheduler, maxBuffered: 4, autoMerge: true));
        for (int i = 0; i < 12; i++)
        {
            w.Upsert(Workload.ToDocument(Put("d" + i, "t", "v", i)));
        }

        w.Commit(); // three segments of four; the policy queues a merge that has not run yet
        w.DeleteById("d1");
        w.DeleteById("d9");
        w.WaitForMerges(); // runs the merge, which copied d1 and d9 only if it captured them before the deletes
        w.Commit();
        w.DeleteById("d5");
        w.Commit();
        SortedDictionary<string, string> rows = Content(storage);
        Assert.Equal(9, rows.Count);
        Assert.All(Deleted, id => Assert.DoesNotContain(id, rows.Keys));
        Assert.Null(Workload.FindLeaks(storage));
    }

    [Fact]
    public void ThreadedAndCooperativeMergesWriteIdenticalBytes()
    {
        List<Workload.Op> ops = Workload.Generate(7, 200);
        var coop = new MemoryStorage();
        var threaded = new MemoryStorage();
        Run(coop, new CooperativeMergeScheduler());
        Run(threaded, new ThreadedMergeScheduler());
        Dictionary<string, byte[]> a = Files(coop);
        Dictionary<string, byte[]> b = Files(threaded);
        Assert.Equal(a.Keys.OrderBy(k => k, StringComparer.Ordinal), b.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (string name in a.Keys)
        {
            Assert.True(a[name].AsSpan().SequenceEqual(b[name]), name + " differs");
        }

        Assert.Contains(a.Keys, k => k.StartsWith('s') && k.CompareTo("s0000000000000010") > 0); // merges produced later segments

        void Run(MemoryStorage storage, IMergeScheduler scheduler)
        {
            using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(scheduler));
            foreach (Workload.Op op in ops)
            {
                if (op.Kind == Workload.Kind.Commit)
                {
                    w.WaitForMerges();
                    w.Commit();
                }
                else
                {
                    Workload.Apply(w, op);
                }
            }

            w.WaitForMerges();
            w.Commit();
        }
    }

    [Fact]
    public void RecoveryRemovesLeftoversAndKeepsTheCommit()
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options()))
        {
            w.Upsert(Workload.ToDocument(Put("a")));
            w.Commit();
        }

        TestHelpers.WriteAll(storage, "s0000000000000099.stored", [1, 2, 3]);
        TestHelpers.WriteAll(storage, "commit-0000000000000002.tmp", [1]);
        TestHelpers.WriteAll(storage, "notes.txt", [1]);
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options()))
        {
            Assert.False(storage.Exists("s0000000000000099.stored"));
            Assert.False(storage.Exists("commit-0000000000000002.tmp"));
            Assert.True(storage.Exists("notes.txt"));
            Assert.Equal(1, w.Generation);
            w.Upsert(Workload.ToDocument(Put("b")));
            w.Flush();
            Assert.True(storage.Exists("s000000000000009a.ids")); // names are never reused after a leftover
        }
    }

    [Fact]
    public void CommitWithoutChangesWritesNothing()
    {
        var storage = new MemoryStorage();
        using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options());
        Assert.Equal(1, w.Commit()); // the first commit records the schema
        string[] names = [.. storage.List()];
        Assert.Equal(1, w.Commit());
        Assert.Equal(names, storage.List());
        w.SetAppliedPosition([1, 2]);
        Assert.Equal(2, w.Commit());
        using IndexSnapshot s = w.OpenSnapshot();
        Assert.Equal(new byte[] { 1, 2 }, s.Commit.AppliedPosition.ToArray());
    }

    [Fact]
    public void ADifferentSchemaIsRefused()
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options()))
        {
            w.Commit();
        }

        Schema other = new SchemaBuilder().Id("id").Text("other").Build();
        Assert.Throws<ArgumentException>(() => IndexWriter.Open(storage, other, Workload.Options()));
    }

    [Fact]
    public void ADamagedCommittedFileFailsTheOpenAndNamesTheFile()
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options()))
        {
            w.Upsert(Workload.ToDocument(Put("a")));
            w.Commit();
        }

        byte[] ids = TestHelpers.ReadAll(storage, "s0000000000000001.ids");
        ids[^13] ^= 0xFF; // a body byte: the open reads only the footer, a full verification reads everything
        storage.Delete("s0000000000000001.ids");
        TestHelpers.WriteAll(storage, "s0000000000000001.ids", ids);
        using (IndexSnapshot snap = IndexSnapshot.Open(storage, Workload.Codecs))
        {
            CorruptIndexException body = Assert.ThrowsAny<CorruptIndexException>(() => snap.Verify());
            Assert.Contains("s0000000000000001.ids", body.Message, StringComparison.Ordinal);
        }

        ids[^1] ^= 0xFF; // the footer checksum the commit recorded
        storage.Delete("s0000000000000001.ids");
        TestHelpers.WriteAll(storage, "s0000000000000001.ids", ids);
        CorruptIndexException e = Assert.ThrowsAny<CorruptIndexException>(() => IndexWriter.Open(storage, Workload.Schema, Workload.Options()));
        Assert.Contains("s0000000000000001.ids", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentsThatAreEntirelyDeletedDisappear()
    {
        var storage = new MemoryStorage();
        using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 2, autoMerge: false));
        for (int i = 0; i < 4; i++)
        {
            w.Upsert(Workload.ToDocument(Put("d" + i)));
        }

        w.Commit();
        Assert.Equal(2, w.SegmentCount);
        w.DeleteById("d0");
        w.DeleteById("d1");
        w.Commit();
        Assert.Equal(1, w.SegmentCount);
        Assert.Null(Workload.FindLeaks(storage));
    }

    [Fact]
    public void LookupByIdentifierAllocatesNothingOnceOpen()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            foreach (IStorage storage in new IStorage[] { new MemoryStorage(), new FileSystemStorage(dir) })
            {
                using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 100)))
                {
                    for (int i = 0; i < 300; i++)
                    {
                        w.Upsert(Workload.ToDocument(Put("document-" + i)));
                    }

                    w.Commit();
                }

                using IndexSnapshot snap = IndexSnapshot.Open(storage, Workload.Codecs);
                Assert.True(snap.TryFindById("document-17", out _, out _));
                Assert.False(snap.TryFindById("nothing-here", out _, out _));
                int found = 0;
                TestHelpers.AssertNoAllocation("identifier lookup", () =>
                {
                    for (int i = 0; i < 1_000; i++)
                    {
                        if (snap.TryFindById("document-123", out int seg, out int doc) && seg >= 0 && doc >= 0)
                        {
                            found++;
                        }

                        snap.TryFindById("missing-id", out _, out _);
                    }
                });
                Assert.True(found >= 1_000);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ADeleteFileRoundTripsAndRejectsDamage()
    {
        foreach ((int maxDoc, int[] deleted) in new[] { (10, new[] { 1, 5 }), (200, Enumerable.Range(0, 200).Where(i => i % 3 != 0).ToArray()), (64, Array.Empty<int>()), (1, new[] { 0 }) })
        {
            var words = new ulong[(maxDoc + 63) >> 6];
            foreach (int d in deleted)
            {
                words[d >> 6] |= 1UL << (d & 63);
            }

            var storage = new MemoryStorage();
            var sink = new SegmentFileSink(storage, "s0000000000000001", null, _ => "x.del1");
            using (SegmentFileBuilder f = sink.Create(SegmentParts.Deletes, "OIXD", 1))
            {
                f.Write(DeleteFileFormat.SerializeBody(maxDoc, words));
                f.Complete();
            }

            byte[] file = TestHelpers.ReadAll(storage, "x.del1");
            LiveDocs live = DeleteFileFormat.Parse("x.del1", file, maxDoc, deleted.Length);
            for (int d = 0; d < maxDoc; d++)
            {
                Assert.Equal(deleted.Contains(d), live.IsDeleted(d));
            }

            // Any single changed byte is caught by the checksum; a recomputed checksum cannot hide a count that lies.
            Assert.Throws<CorruptIndexException>(() => DeleteFileFormat.Parse("x.del1", file.AsSpan(..^1), maxDoc, deleted.Length));
            Assert.Throws<CorruptIndexException>(() => DeleteFileFormat.Parse("x.del1", file, maxDoc + 1, deleted.Length));
        }
    }

    [Fact]
    public void TryReadersReportUnknownChecksumAlgorithmsAndNewerVersionsWithoutThrowing()
    {
        // Found by the fuzz target: a file with an unknown footer algorithm made a Try method throw.
        var storage = new MemoryStorage();
        var sink = new SegmentFileSink(storage, "s0000000000000001");
        IdsFormat.Write(sink, [new KeyValuePair<byte[], int>([1, 2], 0)]);
        byte[] ids = TestHelpers.ReadAll(storage, "s0000000000000001.ids");
        ids[^8] = 9; // algorithm
        Assert.False(IdsFormat.TryValidate("x", ids, 1, null, out string? error));
        Assert.NotNull(error);
        Assert.False(DeleteFileFormat.TryParse("x", ids, -1, -1, null, out _, out _));

        // An intact file written by a newer version is "unsupported" from the throwing readers.
        byte[] newer = TestHelpers.ReadAll(storage, "s0000000000000001.ids");
        newer[4] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(newer.AsSpan(newer.Length - 4), Crc32C.Compute(newer.AsSpan(0, newer.Length - 4)));
        Assert.False(IdsFormat.TryValidate("x", newer, 1, null, out _));
        using IdsReader reader = new(storage.OpenRead("s0000000000000001.ids"), 1);
        Assert.True(reader.TryFind([1, 2], out int doc) && doc == 0);
    }

    [Fact]
    public void MergeOutputIsThrottledThroughTheScheduler()
    {
        var recorder = new PauseRecorder();
        IndexWriterOptions baseOptions = Workload.Options(recorder, maxBuffered: 4);
        var options = new IndexWriterOptions
        {
            Codecs = baseOptions.Codecs,
            MaxBufferedDocuments = 4,
            MergePolicy = baseOptions.MergePolicy,
            MergeScheduler = recorder,
            MaxMergeBytesPerSecond = 1_000,
            TimeProvider = new FrozenClock(),
        };
        using IndexWriter w = IndexWriter.Open(new MemoryStorage(), Workload.Schema, options);
        for (int i = 0; i < 12; i++)
        {
            w.Upsert(Workload.ToDocument(Put("d" + i)));
        }

        w.Commit();
        w.WaitForMerges();
        Assert.NotEmpty(recorder.Pauses);
        Assert.All(recorder.Pauses, p => Assert.True(p > TimeSpan.Zero));
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    private sealed class PauseRecorder : IMergeScheduler
    {
        private readonly CooperativeMergeScheduler _inner = new();

        public List<TimeSpan> Pauses { get; } = [];

        public void Enqueue(Action<CancellationToken> work) => _inner.Enqueue(work);

        public void Poll(CancellationToken cancellationToken) => _inner.Poll(cancellationToken);

        public void WaitForIdle(CancellationToken cancellationToken) => _inner.WaitForIdle(cancellationToken);

        public void Pause(TimeSpan delay, CancellationToken cancellationToken) => Pauses.Add(delay);

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void ShortFaultSweepFindsNoViolation()
    {
        FaultSweep.Result r = FaultSweep.Run([3], [1, 2], 30);
        Assert.True(r.Failures == 0, string.Join(Environment.NewLine, r.Messages.Take(5)));
        Assert.True(r.Schedules > 500);
    }
}
