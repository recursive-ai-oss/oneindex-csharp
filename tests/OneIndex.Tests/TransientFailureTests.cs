using OneIndex.Durability;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>
/// A storage call that fails once (a full disk that frees up, a flaky network share) must never leave a writer that quietly drops documents: after the failure the
/// same writer either commits everything on a retry or refuses to continue, and an index reopened afterwards holds the last commit that was acknowledged.
/// </summary>
public class TransientFailureTests
{
    [Fact]
    public void AFailedStorageCallAtAnyPointNeverLosesDocumentsOrLeavesAnUnopenableIndex()
    {
        List<Document> docs = [.. Enumerable.Range(0, 40).Select(i => Workload.ToDocument(new Workload.Op(Workload.Kind.Upsert, "d" + i, "t" + (i % 3), "body " + i, i)))];
        List<Document> more = [.. Enumerable.Range(40, 20).Select(i => Workload.ToDocument(new Workload.Op(Workload.Kind.Upsert, "d" + i, "t" + (i % 3), "body " + i, i)))];

        long total;
        {
            var fi = new FaultInjectingStorage(new MemoryStorage());
            using IndexWriter w = IndexWriter.Open(fi, Workload.Schema, Workload.Options(maxBuffered: 1000, autoMerge: false));
            foreach (Document d in docs)
            {
                w.Upsert(d);
            }

            w.Commit();
            foreach (Document d in more)
            {
                w.Upsert(d);
            }

            w.Commit();
            total = fi.OperationCount;
        }

        int retriedOk = 0;
        int refused = 0;
        for (long at = 0; at < total; at++)
        {
            var disk = new MemoryStorage();
            var fi = new FaultInjectingStorage(disk, new FaultOptions { Seed = 1, FailAtOperations = [at] });
            int acknowledged = 0;
            IndexWriter? opened = null;
            try
            {
                opened = IndexWriter.Open(fi, Workload.Schema, Workload.Options(maxBuffered: 1000, autoMerge: false));
            }
            catch (IOException)
            {
                // Failing to open is allowed; nothing was acknowledged.
            }

            using (IndexWriter? w = opened)
            {
                if (w is null)
                {
                    continue;
                }

                try
                {
                    foreach (Document d in docs)
                    {
                        w.Upsert(d);
                    }

                    w.Commit();
                    acknowledged = docs.Count;
                    foreach (Document d in more)
                    {
                        w.Upsert(d);
                    }

                    w.Commit();
                    acknowledged = docs.Count + more.Count;
                }
                catch (IOException)
                {
                    // The injected failure. The writer is either still usable or says it is not.
                    try
                    {
                        w.Commit();
                        // A retry that succeeds must publish everything written so far, not a silent subset.
                        retriedOk++;
                        using IndexSnapshot s = w.OpenSnapshot();
                        s.Verify();
                        int n = Workload.ReadContent(s).Count;
                        Assert.True(n >= acknowledged, $"fail@{at}: retry published {n} documents after {acknowledged} were acknowledged");
                        Assert.True(n == docs.Count || n == docs.Count + more.Count, $"fail@{at}: retry published {n} documents");
                        acknowledged = n;
                    }
                    catch (InvalidOperationException)
                    {
                        refused++;
                    }
                    catch (IOException)
                    {
                        refused++;
                    }
                }
            }

            // Reopen on the same storage: the last commit stands, whatever happened above.
            CommitInfo? latest = CommitProtocol.OpenLatest(disk);
            long onDisk = latest is null ? 0 : latest.Segments.Sum(x => x.DocCount - x.DeletedCount);
            Assert.True(onDisk >= acknowledged, $"fail@{at}: {onDisk} documents on disk, {acknowledged} acknowledged");
            if (latest is not null)
            {
                using IndexSnapshot snap = IndexSnapshot.Open(disk, Workload.Codecs);
                snap.Verify();
            }

            // Opening a writer removes whatever an abandoned one left behind.
            using (IndexWriter.Open(disk, Workload.Schema, Workload.Options()))
            {
            }

            Assert.Null(Workload.FindLeaks(disk));
        }

        Assert.True(retriedOk + refused > 0);
    }
}
