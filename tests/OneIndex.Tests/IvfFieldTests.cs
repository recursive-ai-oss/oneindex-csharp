using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Tests;

public sealed class IvfFieldTests
{
    private const int Dims = 48;

    private static float[] Unit(Random rng, int centre)
    {
        // Clustered data: 12 centres plus noise, so lists mean something.
        var v = new float[Dims];
        double sum = 0;
        for (int i = 0; i < Dims; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double noise = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble());
            v[i] = (float)((((i * 7) + (centre * 13)) % 11 < 4 ? 1.5 : 0.0) + (0.5 * noise));
            sum += v[i] * v[i];
        }

        float inv = (float)(1 / Math.Sqrt(sum));
        for (int i = 0; i < Dims; i++)
        {
            v[i] *= inv;
        }

        return v;
    }

    private static Schema IvfSchema(VectorMetric metric = VectorMetric.Cosine, int lists = 0)
        => new SchemaBuilder().Id("id").Keyword("tag")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = Dims, Metric = metric, FlatThreshold = 100, VectorIndex = VectorIndexKind.Ivf, IvfLists = lists }).Build();

    private static List<float[]> Fill(IndexWriter writer, int n, int seed)
    {
        var rng = new Random(seed);
        var data = new List<float[]>();
        for (int i = 0; i < n; i++)
        {
            float[] v = Unit(rng, rng.Next(12));
            data.Add(v);
            writer.Upsert(new Document().Add("id", "d" + i).Add("tag", i % 2 == 0 ? "even" : "odd").Add("v", v));
        }

        writer.Commit();
        return data;
    }

    private static double Recall(IndexWriter writer, List<float[]> data, int probes, Query? filter, string when)
    {
        using IndexSnapshot snap = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
        foreach (LeafContext leaf in searcher.Context.Leaves)
        {
            var field = (VectorFieldReader)leaf.Reader.GetVectors("v")!;
            Assert.Equal(leaf.Reader.MaxDoc >= 100, field.HasIvf);
            Assert.False(field.HasGraph);
        }

        var rng = new Random(77);
        int hits = 0, total = 0;
        for (int q = 0; q < 40; q++)
        {
            float[] query = Unit(rng, rng.Next(12));
            TopDocs approx = searcher.Search(new KnnQuery("v", query, 10) { Ef = probes, Filter = filter }, 10);
            TopDocs exact = searcher.Search(new KnnQuery("v", query, 10) { Exact = true, Filter = filter }, 10);
            var truth = exact.Hits.Select(h => h.DocId).ToHashSet();
            hits += approx.Hits.Count(h => truth.Contains(h.DocId));
            total += exact.Hits.Count;
            // Scores come from the full-precision vectors, so a document found by both has the same score.
            foreach (ScoreDoc h in approx.Hits.Where(h => truth.Contains(h.DocId)))
            {
                Assert.Equal(exact.Hits.First(e => e.DocId == h.DocId).Score, h.Score);
            }
        }

        double recall = hits / (double)total;
        TestContext.Current.SendDiagnosticMessage($"IVF {when}: nprobe {probes} filter {(filter is null ? "none" : "half")} recall@10 = {recall:F3}");
        return recall;
    }

    [Fact]
    public void AFieldWithAnIvfIndexBuildsOnFlushAndMergeAndFindsTheNeighbours()
    {
        var storage = new MemoryStorage();
        using IndexWriter writer = IndexWriter.Open(storage, IvfSchema(), new IndexWriterOptions { MaxBufferedDocuments = 700, AutoMerge = false });
        List<float[]> data = Fill(writer, 2000, 5);
        using (IndexSnapshot snap = writer.OpenSnapshot())
        {
            Assert.True(snap.SegmentCount >= 3);
            Assert.All(Enumerable.Range(0, snap.SegmentCount), i => Assert.Contains(snap.GetSegment(i).Info.Files, f => f.Part == SegmentParts.Ivf));
            Assert.All(Enumerable.Range(0, snap.SegmentCount), i => Assert.DoesNotContain(snap.GetSegment(i).Info.Files, f => f.Part == SegmentParts.Graph));
            snap.Verify();
        }

        Assert.True(Recall(writer, data, 0, null, "flushed, default probes") >= 0.8);
        Assert.True(Recall(writer, data, 200, null, "flushed, all lists") >= 0.99);
        Assert.True(Recall(writer, data, 0, new TermQuery("tag", "even"), "flushed, filtered") >= 0.8);

        writer.ForceMerge(1);
        writer.Commit();
        using (IndexSnapshot merged = writer.OpenSnapshot())
        {
            Assert.Equal(1, merged.SegmentCount);
            Assert.Contains(merged.GetSegment(0).Info.Files, f => f.Part == SegmentParts.Ivf);
            merged.Verify();
        }

        Assert.True(Recall(writer, data, 0, null, "merged, default probes") >= 0.8);
        Assert.True(Recall(writer, data, 0, new TermQuery("tag", "odd"), "merged, filtered") >= 0.8);
    }

    [Fact]
    public void DeletedDocumentsNeverComeBackAndTheIndexReopens()
    {
        var storage = new MemoryStorage();
        using (IndexWriter writer = IndexWriter.Open(storage, IvfSchema(VectorMetric.Euclidean, lists: 20), new IndexWriterOptions { AutoMerge = false }))
        {
            List<float[]> data = Fill(writer, 800, 6);
            for (int i = 0; i < 800; i += 3)
            {
                writer.DeleteById("d" + i);
            }

            writer.Commit();
            using IndexSnapshot snap = writer.OpenSnapshot();
            IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
            TopDocs top = searcher.Search(new KnnQuery("v", data[0], 10) { Ef = 20 }, 10);
            Assert.Equal(10, top.Hits.Count);
            Assert.DoesNotContain(top.Hits, h => h.DocId % 3 == 0);
        }

        // A new writer on the same storage reads the schema (kind and list count) and the IVF part back.
        using IndexWriter reopened = IndexWriter.Open(storage, null, new IndexWriterOptions { AutoMerge = false });
        Assert.True(reopened.Schema.TryGetField("v", out FieldDefinition? v));
        Assert.Equal(VectorIndexKind.Ivf, v.VectorIndex);
        Assert.Equal(20, v.IvfLists);
        using IndexSnapshot again = reopened.OpenSnapshot();
        var field = (VectorFieldReader)SnapshotSearch.CreateSearcher(again).Context.Leaves[0].Reader.GetVectors("v")!;
        Assert.True(field.HasIvf);
    }

    [Fact]
    public void SmallSegmentsAreSearchedExactlyAndAKnnQueryNeedsOnlyPositiveProbes()
    {
        var storage = new MemoryStorage();
        using IndexWriter writer = IndexWriter.Open(storage, IvfSchema(), new IndexWriterOptions { AutoMerge = false });
        List<float[]> data = Fill(writer, 60, 7);
        using IndexSnapshot snap = writer.OpenSnapshot();
        Assert.DoesNotContain(snap.GetSegment(0).Info.Files, f => f.Part == SegmentParts.Ivf);
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
        Assert.Equal(5, searcher.Search(new KnnQuery("v", data[0], 5) { Ef = 1 }, 5).Hits.Count);
        Assert.Throws<QueryException>(() => searcher.Search(new KnnQuery("v", data[0], 5) { Ef = -1 }, 5));
    }

    [Fact]
    public void ADamagedIvfFileRaisesCorruptionNamingTheFile()
    {
        var storage = new MemoryStorage();
        using IndexWriter writer = IndexWriter.Open(storage, IvfSchema(), new IndexWriterOptions { AutoMerge = false });
        List<float[]> data = Fill(writer, 400, 8);
        using IndexSnapshot snap = writer.OpenSnapshot();
        SegmentFile file = snap.GetSegment(0).Info.Files.First(f => f.Part == SegmentParts.Ivf);
        byte[] bytes;
        using (IRangeReader r = storage.OpenRead(file.FileName))
        {
            bytes = new byte[r.Length];
            r.ReadExactly(0, bytes);
        }

        bytes[bytes.Length - IndexFile.FooterLength - 20] ^= 0x55; // inside the directory or its trailer
        storage.Delete(file.FileName);
        using (IStorageWriter w = storage.Create(file.FileName))
        {
            w.Write(bytes);
            w.Sync();
        }

        CorruptIndexException e = Assert.Throws<CorruptIndexException>(() =>
        {
            using IndexSnapshot damaged = writer.OpenSnapshot();
            SnapshotSearch.CreateSearcher(damaged).Search(new KnnQuery("v", data[0], 5), 5);
        });
        Assert.Contains(file.FileName, e.Message);
    }

    [Fact]
    public void TheAttributeChoosesIvfOnTheHappyPath()
    {
        using SearchIndex<IvfPassage> index = SearchIndex.InMemory<IvfPassage>();
        Assert.True(index.Schema.TryGetField("Embedding", out FieldDefinition? field));
        Assert.Equal(VectorIndexKind.Ivf, field.VectorIndex);
        Assert.Equal(16, field.IvfLists);
        var rng = new Random(9);
        var all = new List<IvfPassage>();
        for (int i = 0; i < 600; i++)
        {
            float[] v = Unit(rng, rng.Next(12));
            var p = new IvfPassage { Id = "p" + i, Embedding = v };
            all.Add(p);
            index.Upsert(p);
        }

        index.Commit();
        float[] q = Unit(rng, 3);
        SearchResults<IvfPassage> hits = index.SearchVector(x => x.Embedding, q, neighbors: 10).Take(10).Execute();
        List<string> expected = all.OrderByDescending(p => p.Embedding.Zip(q, (a, b) => a * b).Sum()).Take(10).Select(p => p.Id).ToList();
        Assert.True(hits.Select(h => h.Id).Intersect(expected).Count() >= 8);
    }

    [Fact]
    public void SchemaRoundTripsIvfSettingsAndRefusesBadOnes()
    {
        Schema back = Schema.Deserialize(IvfSchema(lists: 33).Serialize());
        Assert.True(back.TryGetField("v", out FieldDefinition? f));
        Assert.Equal(VectorIndexKind.Ivf, f.VectorIndex);
        Assert.Equal(33, f.IvfLists);
        Assert.Throws<ArgumentException>(() => IvfSchema(lists: 5000));
        Assert.Throws<ArgumentException>(() => new SchemaBuilder().Id("id").Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = 8, IvfLists = 4 }).Build());
    }
}

/// <summary>A mapped type whose vector field is searched through an IVF index.</summary>
public class IvfPassage
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Vector(Dimensions = 48, FlatThreshold = 50, Index = VectorIndexKind.Ivf, IvfLists = 16)]
    public float[] Embedding { get; set; } = [];
}
