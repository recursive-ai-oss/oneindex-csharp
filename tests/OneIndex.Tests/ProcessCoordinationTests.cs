using OneIndex.Durability;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>One writer, many readers, in this process standing in for several: separate storage objects over one directory exclude each other exactly as separate processes do (the multi-process campaign in tests/Durability covers real kills).</summary>
public class ProcessCoordinationTests
{
    private static Workload.Op Put(string id, string tag = "t0", string body = "b", long n = 1) => new(Workload.Kind.Upsert, id, tag, body, n);

    private static void Fill(IndexWriter w, int count, string body)
    {
        for (int i = 0; i < count; i++)
        {
            w.Upsert(Workload.ToDocument(Put("d" + i, "t", body, i)));
        }
    }

    [Fact]
    public void SecondWriterOnTheSameDirectoryIsRefusedUntilTheFirstCloses()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            using (IndexWriter first = IndexWriter.Open(new FileSystemStorage(dir), Workload.Schema, Workload.Options()))
            {
                IndexLockedException e = Assert.Throws<IndexLockedException>(() => IndexWriter.Open(new FileSystemStorage(dir), Workload.Schema, Workload.Options()));
                Assert.Equal("write.lock", e.LockName);
                first.Upsert(Workload.ToDocument(Put("a")));
                first.Commit();
            }

            using IndexWriter second = IndexWriter.Open(new FileSystemStorage(dir), Workload.Schema, Workload.Options());
            Assert.Equal(1, second.Generation);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SecondWriterOnTheSameMemoryStorageIsRefused()
    {
        var storage = new MemoryStorage();
        using IndexWriter first = IndexWriter.Open(storage, Workload.Schema, Workload.Options());
        Assert.Throws<IndexLockedException>(() => IndexWriter.Open(storage, Workload.Schema, Workload.Options()));
    }

    [Fact]
    public void AFailedOpenReleasesTheLock()
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options()))
        {
            w.Upsert(Workload.ToDocument(Put("a")));
            w.Commit();
        }

        // A different schema is refused after the lock was taken; the lock must not stay behind.
        Schema other = new SchemaBuilder().Id("id").Text("other").Build();
        Assert.Throws<ArgumentException>(() => IndexWriter.Open(storage, other, Workload.Options()));
        using IndexWriter again = IndexWriter.Open(storage, Workload.Schema, Workload.Options());
    }

    [Fact]
    public void ReaderLeaseKeepsFilesFromTheWriterUntilItLetsGo()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            var writerStorage = new FileSystemStorage(dir);
            using IndexWriter w = IndexWriter.Open(writerStorage, Workload.Schema, Workload.Options(maxBuffered: 5, autoMerge: false));
            Fill(w, 30, "v1");
            w.Commit();

            // A reader through its own storage object, as another process would have.
            var readerStorage = new FileSystemStorage(dir);
            IndexSnapshot snap = IndexSnapshot.Open(readerStorage, Workload.Codecs);
            SortedDictionary<string, string> before = Workload.ReadContent(snap);
            string[] pinned = [.. snap.Commit.Segments.SelectMany(s => s.Files.Select(f => f.FileName)), CommitFileNames.For(snap.Generation)];
            Assert.Contains(readerStorage.List(), n => n.EndsWith(".lease", StringComparison.Ordinal));

            for (int i = 0; i < 30; i += 2)
            {
                w.DeleteById("d" + i);
            }

            w.Upsert(Workload.ToDocument(Put("new", "t", "v2", 0)));
            w.Commit();
            w.ForceMerge(1);
            w.Commit();
            Assert.Equal(1, w.SegmentCount);

            Assert.All(pinned, n => Assert.True(writerStorage.Exists(n), n + " was deleted while a reader pinned it"));
            snap.Verify();
            Assert.Equal(before, Workload.ReadContent(snap));

            snap.Dispose();
            // The reader's housekeeping already reclaimed what the writer had to keep.
            Assert.All(pinned, n => Assert.False(writerStorage.Exists(n), n + " was not reclaimed"));
            Assert.Null(Workload.FindLeaks(writerStorage));
            Assert.DoesNotContain(writerStorage.List(), n => n.EndsWith(".lease", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FilesAReaderPinnedAreReclaimedOnceItLetsGo()
    {
        var storage = new MemoryStorage();
        using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 5, autoMerge: false));
        Fill(w, 20, "v1");
        w.Commit();
        IndexSnapshot snap = IndexSnapshot.Open(storage, Workload.Codecs);
        string[] pinned = [.. snap.Commit.Segments.SelectMany(s => s.Files.Select(f => f.FileName)), CommitFileNames.For(snap.Generation)];
        Fill(w, 20, "v2");
        w.Commit();
        w.ForceMerge(1);
        w.Commit();
        Assert.All(pinned, n => Assert.True(storage.Exists(n)));
        snap.Dispose();
        w.ReclaimFiles();
        Assert.All(pinned, n => Assert.False(storage.Exists(n), n + " was not reclaimed"));
        Assert.Null(Workload.FindLeaks(storage));
    }

    [Fact]
    public void ALeaseWhoseHolderIsGoneProtectsNothingAndIsRemoved()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            var storage = new FileSystemStorage(dir);
            using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 5, autoMerge: false)))
            {
                Fill(w, 10, "v1");
                w.Commit();
                // What a killed reader leaves: a lease file nobody holds.
                TestHelpers.WriteAll(storage, "reader-0000000000000001-00000000000000ab.lease", []);
                Fill(w, 10, "v2");
                w.Commit();
                w.ForceMerge(1);
                w.Commit();
            }

            using IndexWriter reopened = IndexWriter.Open(storage, Workload.Schema, Workload.Options());
            Assert.Null(Workload.FindLeaks(storage));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReaderRefreshMovesToTheNewestCommitAndKeepsAcquiredSnapshotsValid()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            using IndexWriter w = IndexWriter.Open(new FileSystemStorage(dir), Workload.Schema, Workload.Options(maxBuffered: 5));
            Fill(w, 10, "v1");
            w.Commit();
            using IndexReader reader = IndexReader.Open(new FileSystemStorage(dir), Workload.Codecs);
            Assert.Equal(1, reader.Generation);
            Assert.False(reader.Refresh()); // nothing newer
            using IndexSnapshot held = reader.Acquire();

            Fill(w, 10, "v2");
            w.Commit();
            Assert.True(reader.Refresh());
            Assert.Equal(2, reader.Generation);
            Assert.True(reader.IsDurable);

            Assert.Equal(1, held.Generation);
            held.Verify();
            Assert.All(Workload.ReadContent(held).Values, v => Assert.Contains("v1", v, StringComparison.Ordinal));
            using IndexSnapshot current = reader.Acquire();
            Assert.All(Workload.ReadContent(current).Values, v => Assert.Contains("v2", v, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadOnlyStorageOpensWithoutALease()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            using (IndexWriter w = IndexWriter.Open(new FileSystemStorage(dir), Workload.Schema, Workload.Options()))
            {
                Fill(w, 5, "v1");
                w.Commit();
            }

            var readOnly = new FileSystemStorage(dir, readOnly: true);
            Assert.False(readOnly.SupportsLocks);
            using IndexSnapshot snap = IndexSnapshot.Open(readOnly, Workload.Codecs);
            Assert.Equal(5, Workload.ReadContent(snap).Count);
            Assert.DoesNotContain(readOnly.List(), n => n.EndsWith(".lease", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NearRealTimeReaderShowsUncommittedChangesAndMatchesTheCommit()
    {
        var storage = new MemoryStorage();
        using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 7));
        var model = new Workload.Model();
        var rng = new Random(11);
        using IndexReader nrt = w.OpenNearRealTimeReader();
        Assert.False(nrt.IsDurable);
        for (int round = 0; round < 12; round++)
        {
            for (int i = 0; i < 15; i++)
            {
                Workload.Op op = rng.Next(4) == 0
                    ? new Workload.Op(Workload.Kind.DeleteId, "doc" + rng.Next(25), "", "", 0)
                    : new Workload.Op(Workload.Kind.Upsert, "doc" + rng.Next(25), "t" + rng.Next(3), "body" + rng.Next(100), rng.Next(1000));
                Workload.Apply(w, op);
                model.Apply(op);
            }

            nrt.Refresh();
            using (IndexSnapshot view = nrt.Acquire())
            {
                Assert.False(view.IsDurable);
                Assert.Equal(model.Rows, Workload.ReadContent(view));
                view.Verify();
            }

            if (round % 3 == 2)
            {
                // Differential: once committed, a committed read shows exactly what the near-real-time read showed.
                w.Commit();
                using IndexSnapshot committed = IndexSnapshot.Open(storage, Workload.Codecs);
                Assert.True(committed.IsDurable);
                Assert.Equal(model.Rows, Workload.ReadContent(committed));
            }
        }
    }

    [Fact]
    public void NearRealTimeSnapshotIsIsolatedFromLaterChangesAndKeepsMergedFiles()
    {
        var storage = new MemoryStorage();
        using IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 5, autoMerge: false));
        Fill(w, 20, "v1");
        w.Commit();
        w.DeleteById("d3");
        w.Upsert(Workload.ToDocument(Put("extra")));
        using IndexSnapshot view = w.OpenNearRealTimeSnapshot();
        SortedDictionary<string, string> seen = Workload.ReadContent(view);
        Assert.DoesNotContain("d3", seen.Keys);
        Assert.Contains("extra", seen.Keys);

        w.DeleteById("d4");
        w.Commit();
        w.ForceMerge(1);
        w.Commit();
        view.Verify(); // the segments the view uses were not deleted by the merge
        Assert.Equal(seen, Workload.ReadContent(view));
    }

    [Fact]
    public void NearRealTimeViewKeepsItsFilesFromAReaderProcessTidyingUp()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            using IndexWriter w = IndexWriter.Open(new FileSystemStorage(dir), Workload.Schema, Workload.Options(maxBuffered: 5, autoMerge: false));
            Fill(w, 10, "v1");
            w.Commit();
            Fill(w, 10, "v2"); // flushed segments that no commit lists yet
            using IndexSnapshot view = w.OpenNearRealTimeSnapshot();
            SortedDictionary<string, string> seen = Workload.ReadContent(view);
            w.Commit(); // commit 2 lists the view's segments

            // A reader process pins commit 2, the writer merges the segments away and commits, and the reader then lets go: its
            // housekeeping deletes superseded commits nobody pins, which must not take files the view still reads.
            IndexSnapshot other = IndexSnapshot.Open(new FileSystemStorage(dir), Workload.Codecs);
            Assert.Equal(2, other.Generation);
            w.ForceMerge(1);
            w.Commit();
            other.Dispose();

            view.Verify();
            Assert.Equal(seen, Workload.ReadContent(view));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NearRealTimeStateIsNotDurable()
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, Workload.Schema, Workload.Options(maxBuffered: 4)))
        {
            Fill(w, 6, "committed");
            w.Commit();
            Fill(w, 12, "volatile");
            using IndexReader nrt = w.OpenNearRealTimeReader();
            using (IndexSnapshot view = nrt.Acquire())
            {
                Assert.All(Workload.ReadContent(view).Values, v => Assert.Contains("volatile", v, StringComparison.Ordinal));
                Assert.Equal(12, Workload.ReadContent(view).Count);
            }

            // The writer goes away without committing: nothing of the near-real-time view is on disk as a commit.
        }

        using (IndexSnapshot after = IndexSnapshot.Open(storage, Workload.Codecs))
        {
            Assert.Equal(1, after.Generation);
            Assert.Equal(6, Workload.ReadContent(after).Count);
            Assert.All(Workload.ReadContent(after).Values, v => Assert.Contains("committed", v, StringComparison.Ordinal));
        }

        Assert.Null(Workload.FindLeaks(storage));
    }
}
