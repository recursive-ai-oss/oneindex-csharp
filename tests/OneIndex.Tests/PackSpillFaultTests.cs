using OneIndex.Durability;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>
/// A flush whose parts exceed the pack limit writes the part that crosses it to a file of its own. The fault sweeps use tiny segments and never reach that
/// path, so this test cuts the power at every storage operation of one large flush and commit, under every crash policy.
/// </summary>
public class PackSpillFaultTests
{
    private const int Documents = 5_000;

    // Incompressible stored bytes make a large segment cheaply: no analysis, no graph.
    private static readonly Schema BlobSchema = new SchemaBuilder().Id("id").Bytes("blob").Build();

    private static List<Document> MakeDocuments()
    {
        var rng = new Random(77);
        var docs = new List<Document>(Documents);
        for (int i = 0; i < Documents; i++)
        {
            var blob = new byte[1_024];
            rng.NextBytes(blob);
            docs.Add(new Document().Add("id", "d" + i).Add("blob", FieldValue.FromBytes(blob)));
        }

        return docs;
    }

    private static IndexWriterOptions Big() => new IndexWriterOptions { MaxBufferedDocuments = Documents + 1, AutoMerge = false };

    [Fact]
    public void ALargeFlushThatSpillsASecondFileSurvivesAPowerCutAtEveryOperation()
    {
        List<Document> docs = MakeDocuments();

        long total;
        {
            var fi = new FaultInjectingStorage(new MemoryStorage());
            using (IndexWriter w = IndexWriter.Open(fi, BlobSchema, Big()))
            {
                foreach (Document d in docs)
                {
                    w.Upsert(d);
                }

                w.Commit();
            }

            total = fi.OperationCount;
            CommitInfo commit = CommitProtocol.OpenLatest(fi)!;
            string[] files = [.. commit.Segments[0].Files.Select(f => f.FileName).Distinct()];
            Assert.Contains(files, f => f.EndsWith(".pack", StringComparison.Ordinal));
            Assert.True(files.Length >= 2, "the flush was expected to spill: " + string.Join(",", files));
        }

        int checkedRuns = 0;
        foreach (CrashPolicy policy in new[] { CrashPolicy.LoseUnsynced, CrashPolicy.Tear, CrashPolicy.Reorder })
        {
            for (long at = 0; at <= total; at++)
            {
                var disk = new MemoryStorage();
                var fi = new FaultInjectingStorage(disk, new FaultOptions { Seed = 3, CrashAtOperation = at, Policy = policy });
                bool acknowledged = false;
                try
                {
                    using IndexWriter w = IndexWriter.Open(fi, BlobSchema, Big());
                    foreach (Document d in docs)
                    {
                        w.Upsert(d);
                    }

                    w.Commit();
                    acknowledged = true;
                }
                catch (IOException)
                {
                    // The cut.
                }

                fi.Crash(policy);
                CommitInfo? latest = CommitProtocol.OpenLatest(disk);
                if (acknowledged)
                {
                    Assert.NotNull(latest);
                }

                if (latest is not null)
                {
                    using IndexSnapshot snap = IndexSnapshot.Open(disk);
                    snap.Verify();
                    Assert.Equal(Documents, snap.Commit.Segments.Sum(x => x.DocCount));
                }

                // Whatever survived, the next writer opens, sweeps the leftovers and commits.
                using (IndexWriter w = IndexWriter.Open(disk, BlobSchema))
                {
                    w.Upsert(new Document().Add("id", "after").Add("blob", FieldValue.FromBytes(new byte[] { 1 })));
                    w.Commit();
                }

                Assert.Null(Workload.FindLeaks(disk));
                checkedRuns++;
            }
        }

        Assert.True(checkedRuns >= 3 * (total + 1));
    }
}
