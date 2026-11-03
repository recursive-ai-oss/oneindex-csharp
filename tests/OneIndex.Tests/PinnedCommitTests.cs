using OneIndex.Durability;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>
/// A reader in another process pins a commit with a lease. When the writer cannot read that commit file because of a transient I/O error, it must not conclude
/// that the commit pins nothing: guessing "nothing" lets it delete segment files the reader is still using.
/// </summary>
public class PinnedCommitTests
{
    /// <summary>Delegates everything to a memory storage and fails the next open of one named file with an I/O error, once.</summary>
    private sealed class FlakyReadStorage(MemoryStorage inner) : StorageBase, IStorage
    {
        public string? FailOnce { get; set; }

        public int Failures { get; private set; }

        public override bool IsReadOnly => false;

        public bool SupportsLocks => ((IStorage)inner).SupportsLocks;

        public IStorageLock? TryLock(string name, CancellationToken cancellationToken = default) => inner.TryLock(name, cancellationToken);

        public override IStorageWriter Create(string name, CancellationToken cancellationToken = default) => inner.Create(name, cancellationToken);

        public override void Delete(string name, CancellationToken cancellationToken = default) => inner.Delete(name, cancellationToken);

        public override bool Exists(string name, CancellationToken cancellationToken = default) => inner.Exists(name, cancellationToken);

        public override IReadOnlyList<string> List(CancellationToken cancellationToken = default) => inner.List(cancellationToken);

        public override IRangeReader OpenRead(string name, CancellationToken cancellationToken = default)
        {
            if (FailOnce == name)
            {
                FailOnce = null;
                Failures++;
                throw new IOException("transient read failure of " + name);
            }

            return inner.OpenRead(name, cancellationToken);
        }

        public override void Rename(string source, string destination, CancellationToken cancellationToken = default) => inner.Rename(source, destination, cancellationToken);

        public override void SyncDirectory(CancellationToken cancellationToken = default) => inner.SyncDirectory(cancellationToken);
    }

    private static Document Doc(int i) => Workload.ToDocument(new Workload.Op(Workload.Kind.Upsert, "d" + i, "t", "body " + i, i));

    // Two commits of one segment each, a reader pinning the second, then a merge and a third commit: the second commit's segment files are now
    // referenced by nothing but the reader's lease.
    private static (MemoryStorage Disk, IndexSnapshot Pinned, string[] PinnedSegmentFiles) Arrange()
    {
        var disk = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(disk, Workload.Schema, Workload.Options(maxBuffered: 1000, autoMerge: false)))
        {
            for (int i = 0; i < 10; i++)
            {
                w.Upsert(Doc(i));
            }

            w.Commit();
            for (int i = 10; i < 20; i++)
            {
                w.Upsert(Doc(i));
            }

            w.Commit();
        }

        IndexSnapshot pinned = IndexSnapshot.Open(disk);
        string[] files = [.. pinned.Commit.Segments.SelectMany(static s => s.Files).Select(static f => f.FileName)];
        using (IndexWriter w = IndexWriter.Open(disk, Workload.Schema, Workload.Options(maxBuffered: 1000, autoMerge: false)))
        {
            w.ForceMerge(1);
            w.Commit();
        }

        foreach (string f in files)
        {
            Assert.True(disk.Exists(f), "the pinned segment file should be kept: " + f);
        }

        return (disk, pinned, files);
    }

    [Fact]
    public void ATransientErrorWhileReadingAPinnedCommitIsNotTakenAsNothingPinned()
    {
        (MemoryStorage disk, IndexSnapshot pinned, _) = Arrange();
        using (pinned)
        {
            var flaky = new FlakyReadStorage(disk) { FailOnce = CommitFileNames.For(pinned.Generation) };
            Assert.Throws<IOException>(() => IndexLocks.LivePinnedFiles(flaky, IndexLimits.Default, default));
            Assert.Equal(1, flaky.Failures);

            // Once the error is gone the same scan lists the segment files of the pinned commit.
            HashSet<string> set = IndexLocks.LivePinnedFiles(flaky, IndexLimits.Default, default)!;
            Assert.All(pinned.Commit.Segments.SelectMany(static s => s.Files), f => Assert.Contains(f.FileName, set));
        }
    }

    [Fact]
    public void AWriterOpenedDuringATransientErrorKeepsEveryFileAReaderMayNeed()
    {
        (MemoryStorage disk, IndexSnapshot pinned, string[] files) = Arrange();
        using (pinned)
        {
            var flaky = new FlakyReadStorage(disk) { FailOnce = CommitFileNames.For(pinned.Generation) };
            using (IndexWriter.Open(flaky, Workload.Schema, Workload.Options(maxBuffered: 1000, autoMerge: false)))
            {
                Assert.Equal(1, flaky.Failures);
            }

            foreach (string f in files)
            {
                Assert.True(disk.Exists(f), "a file the pinned commit lists was deleted: " + f);
            }

            pinned.Verify();
        }
    }
}
