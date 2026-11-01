using System.Diagnostics;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.VectorBench;

/// <summary>Times a three-segment merge (80%, 10%, 10% of the vectors) with and without continuing from the largest graph, then measures recall of the merged segment.</summary>
internal static class MergeRun
{
    public static int Run(float[] data, int count, int dims, float[][] queries, HashSet<int>[] exact, int m, int efc, string scratch)
    {
        foreach (bool reuse in new[] { false, true })
        {
            string dir = Path.Combine(scratch, "oneindex-mergebench-" + Guid.NewGuid().ToString("N"));
            try
            {
                Schema schema = new SchemaBuilder().Id("id")
                    .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = dims, HnswM = m, EfConstruction = efc, Stored = false })
                    .Build();
                using IndexWriter writer = IndexWriter.Open(new FileSystemStorage(dir), schema, new IndexWriterOptions { MaxBufferedDocuments = count + 1, AutoMerge = false });
                int[] cuts = [0, count * 8 / 10, count * 9 / 10, count];
                var flush = Stopwatch.StartNew();
                for (int s = 0; s < 3; s++)
                {
                    for (int i = cuts[s]; i < cuts[s + 1]; i++)
                    {
                        writer.AddUnique(new Document().Add("id", "d" + i).Add("v", data.AsSpan(i * dims, dims).ToArray()));
                    }

                    writer.Flush();
                }

                writer.Commit();
                double flushSeconds = flush.Elapsed.TotalSeconds;
                VectorSegmentCodec.ReuseLargestGraph = reuse;
                var merge = Stopwatch.StartNew();
                writer.ForceMerge(1);
                writer.Commit();
                double mergeSeconds = merge.Elapsed.TotalSeconds;
                using IndexSnapshot snapshot = writer.OpenSnapshot();
                SearchContext context = SnapshotSearch.CreateContext(snapshot);
                IVectorFieldReader reader = context.Leaves[0].Reader.GetVectors("v")!;
                var hits = new OneIndex.Vectors.VectorHit[10];
                long found = 0;
                for (int q = 0; q < queries.Length; q++)
                {
                    int n = reader.Search(queries[q], 10, 100, null, null, false, hits);
                    for (int i = 0; i < n; i++)
                    {
                        // The merged segment numbers documents in the order they were added, so a document id is a row number.
                        found += exact[q].Contains(hits[i].Doc) ? 1 : 0;
                    }
                }

                Console.WriteLine($"merge n={count} d={dims} reuse-largest-graph={reuse}: flush of 3 segments {flushSeconds:F1}s, merge {mergeSeconds:F1}s, recall@10 at ef 100 {found / (double)(queries.Length * 10):F4}");
            }
            finally
            {
                VectorSegmentCodec.ReuseLargestGraph = true;
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        return 0;
    }
}
