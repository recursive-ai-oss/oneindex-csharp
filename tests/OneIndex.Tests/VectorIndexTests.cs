using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Tests;

public sealed class VectorIndexTests
{
    private const int Dims = 16;

    private static float[][] Cloud(int seed, int n, int dims, bool unit = true)
    {
        var rng = new Random(seed);
        var centers = new float[8][];
        for (int c = 0; c < centers.Length; c++)
        {
            centers[c] = Gaussian(rng, dims);
        }

        var all = new float[n][];
        for (int i = 0; i < n; i++)
        {
            float[] v = Gaussian(rng, dims);
            float[] c = centers[rng.Next(centers.Length)];
            for (int j = 0; j < dims; j++)
            {
                v[j] = (v[j] * 0.5f) + c[j];
            }

            if (unit)
            {
                float norm = MathF.Sqrt(v.Sum(x => x * x));
                for (int j = 0; j < dims; j++)
                {
                    v[j] /= norm;
                }
            }

            all[i] = v;
        }

        return all;
    }

    private static float[] Gaussian(Random rng, int dims)
    {
        var v = new float[dims];
        for (int i = 0; i < dims; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble()));
        }

        return v;
    }

    [Fact]
    public void GraphSearchFindsMostTrueNeighboursAndTheBuildIsDeterministic()
    {
        float[][] all = Cloud(1, 3100, 24);
        float[][] data = all[..3000];
        var flat = new float[data.Length * 24];
        for (int i = 0; i < data.Length; i++)
        {
            data[i].CopyTo(flat, i * 24);
        }

        var values = new ArrayVectorValues(flat, 24);
        var a = new HnswBuilder(values, DistanceKind.NegativeDot, 16, 100, 7);
        a.InsertAll(CancellationToken.None);
        var b = new HnswBuilder(values, DistanceKind.NegativeDot, 16, 100, 7);
        b.InsertAll(CancellationToken.None);
        Assert.Equal(a.EntryPoint, b.EntryPoint);
        Assert.Equal(a.MaxLevel, b.MaxLevel);
        for (int node = 0; node < values.Count; node++)
        {
            Assert.True(a.Neighbors(node, 0).SequenceEqual(b.Neighbors(node, 0)));
            Assert.Equal(a.Level(node), b.Level(node));
        }

        float[][] queries = all[3000..];
        var scorer = new FloatNodeScorer(DistanceKind.NegativeDot);
        var found = new Candidate[10];
        var exactOut = new Candidate[10];
        int hit = 0;
        foreach (float[] q in queries)
        {
            scorer.Reset(values, q);
            int n = HnswSearch.Search(a, scorer, 10, 64, null, 0, found, out bool complete);
            Assert.True(complete);
            int m = FlatSearch.Search(values, DistanceKind.NegativeDot, q, 10, null, exactOut);
            Assert.Equal(10, m);
            Assert.Equal(10, n);
            hit += found.Take(n).Select(c => c.Id).Intersect(exactOut.Select(c => c.Id)).Count();
        }

        Assert.True(hit >= 0.97 * 1000, $"recall {hit / 1000.0}");
    }

    [Fact]
    public void BatchedExactSearchEqualsOneByOne()
    {
        float[][] data = Cloud(3, 500, 16);
        var values = new ArrayVectorValues(data.SelectMany(x => x).ToArray(), 16);
        float[][] queries = Cloud(4, 20, 16);
        var batch = FlatSearch.SearchBatch(values, DistanceKind.SquaredL2, queries, 7, CancellationToken.None);
        var one = new Candidate[7];
        for (int q = 0; q < queries.Length; q++)
        {
            int n = FlatSearch.Search(values, DistanceKind.SquaredL2, queries[q], 7, null, one);
            Assert.Equal(n, batch[q].Ordinals.Length);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(one[i].Id, batch[q].Ordinals[i]);
                Assert.Equal(one[i].Key, batch[q].Distances[i]);
            }
        }
    }

    private static Schema VectorSchema(VectorMetric metric, int flatThreshold = 20)
        => new SchemaBuilder().Id("id").Keyword("tag").Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = Dims, Metric = metric, FlatThreshold = flatThreshold }).Build();

    private static void Fill(IndexWriter writer, float[][] data, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("tag", i % 5 == 0 ? "five" : "other").Add("v", data[i]));
        }
    }

    // Brute force over the vectors that are still live, with the same scoring as the index.
    private static List<string> Expected(float[][] data, HashSet<int> deleted, float[] q, int k, VectorMetric metric, Func<int, bool>? accept = null)
    {
        float[] nq = metric == VectorMetric.Cosine ? Normalize(q) : q;
        return Enumerable.Range(0, data.Length).Where(i => !deleted.Contains(i) && (accept?.Invoke(i) ?? true))
            .Select(i => (Id: i, Distance: Dist(metric, metric == VectorMetric.Cosine ? Normalize(data[i]) : data[i], nq)))
            .OrderBy(x => x.Distance).ThenBy(x => x.Id).Take(k).Select(x => "d" + x.Id).ToList();
    }

    private static float[] Normalize(float[] v)
    {
        float n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    private static double Dist(VectorMetric metric, float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            s += metric == VectorMetric.Euclidean ? Math.Pow(a[i] - b[i], 2) : -(double)a[i] * b[i];
        }

        return s;
    }

    private static List<string> Ids(IndexSearcher searcher, TopDocs top)
    {
        var ids = new List<string>();
        foreach (ScoreDoc h in top.Hits)
        {
            LeafContext leaf = searcher.Context.Leaves.Last(l => l.DocBase <= h.DocId);
            ids.Add(leaf.Reader.StoredFields!.GetDocument(h.DocId - leaf.DocBase).Get("id").AsText());
        }

        return ids;
    }

    [Theory]
    [InlineData(VectorMetric.Cosine)]
    [InlineData(VectorMetric.DotProduct)]
    [InlineData(VectorMetric.Euclidean)]
    public void ExactKnnMatchesBruteForceAcrossSegmentsDeletesAndMerges(VectorMetric metric)
    {
        float[][] data = Cloud(5, 400, Dims, unit: metric != VectorMetric.DotProduct);
        var storage = new MemoryStorage();
        var deleted = new HashSet<int>();
        using (IndexWriter writer = IndexWriter.Open(storage, VectorSchema(metric), new IndexWriterOptions { MaxBufferedDocuments = 100, AutoMerge = false }))
        {
            Fill(writer, data, 0, 400);
            writer.Commit();
            foreach (int i in new[] { 3, 77, 150, 151, 399 })
            {
                Assert.True(writer.DeleteById("d" + i));
                deleted.Add(i);
            }

            writer.Commit();
            float[][] queries = Cloud(6, 15, Dims, unit: false);
            using (IndexSnapshot snap = writer.OpenSnapshot())
            {
                Assert.True(snap.SegmentCount >= 4);
                IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
                Assert.True(searcher.Context.Leaves.All(l => l.Reader.GetVectors("v") is { HasGraph: true }));
                foreach (float[] q in queries)
                {
                    TopDocs top = searcher.Search(new KnnQuery("v", q, 10) { Exact = true }, 10);
                    Assert.Equal(Expected(data, deleted, q, 10, metric), Ids(searcher, top));
                    // Scores come back best first.
                    Assert.True(top.Hits.Zip(top.Hits.Skip(1)).All(p => p.First.Score >= p.Second.Score));
                }
            }

            writer.ForceMerge(1);
            writer.Commit();
            using (IndexSnapshot snap = writer.OpenSnapshot())
            {
                Assert.Equal(1, snap.SegmentCount);
                IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
                Assert.True(searcher.Context.Leaves[0].Reader.GetVectors("v") is { HasGraph: true, Count: 395 });
                foreach (float[] q in queries)
                {
                    TopDocs exact = searcher.Search(new KnnQuery("v", q, 10) { Exact = true }, 10);
                    Assert.Equal(Expected(data, deleted, q, 10, metric), Ids(searcher, exact));
                    TopDocs approx = searcher.Search(new KnnQuery("v", q, 10) { Ef = 200 }, 10);
                    Assert.True(Ids(searcher, approx).Intersect(Ids(searcher, exact)).Count() >= 9);
                }
            }
        }

        // A new process: the committed files alone give the same answers.
        using IndexWriter reopened = IndexWriter.Open(storage, VectorSchema(metric), new IndexWriterOptions { AutoMerge = false });
        using IndexSnapshot again = reopened.OpenSnapshot();
        IndexSearcher s2 = SnapshotSearch.CreateSearcher(again);
        float[] probe = data[10];
        Assert.Equal(Expected(data, deleted, probe, 5, metric), Ids(s2, s2.Search(new KnnQuery("v", probe, 5) { Exact = true }, 5)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergingKeepsRecallWhetherOrNotItContinuesFromTheLargestGraph(bool reuse)
    {
        float[][] data = Cloud(13, 700, Dims);
        bool saved = OneIndex.Codecs.VectorSegmentCodec.ReuseLargestGraph;
        OneIndex.Codecs.VectorSegmentCodec.ReuseLargestGraph = reuse;
        try
        {
            using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), VectorSchema(VectorMetric.Cosine, flatThreshold: 20), new IndexWriterOptions { MaxBufferedDocuments = 400, AutoMerge = false });
            Fill(writer, data, 0, 400);
            writer.Flush();
            Fill(writer, data, 400, 550);
            writer.Flush();
            Fill(writer, data, 550, 700);
            writer.Commit();
            writer.ForceMerge(1);
            writer.Commit();
            using IndexSnapshot snap = writer.OpenSnapshot();
            Assert.Equal(1, snap.SegmentCount);
            IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
            Assert.True(searcher.Context.Leaves[0].Reader.GetVectors("v") is { HasGraph: true, Count: 700 });
            int found = 0;
            float[][] queries = Cloud(14, 60, Dims);
            foreach (float[] q in queries)
            {
                List<string> exact = Ids(searcher, searcher.Search(new KnnQuery("v", q, 10) { Exact = true }, 10));
                found += Ids(searcher, searcher.Search(new KnnQuery("v", q, 10) { Ef = 60 }, 10)).Intersect(exact).Count();
            }

            Assert.True(found >= 0.96 * 600, $"recall {found / 600.0}");
        }
        finally
        {
            OneIndex.Codecs.VectorSegmentCodec.ReuseLargestGraph = saved;
        }
    }

    [Fact]
    public void FilteredKnnReturnsFullPagesFromTheFilteredSet()
    {
        float[][] data = Cloud(8, 600, Dims);
        using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), VectorSchema(VectorMetric.Cosine, flatThreshold: 50), new IndexWriterOptions { MaxBufferedDocuments = 200, AutoMerge = false });
        Fill(writer, data, 0, 600);
        writer.Commit();
        using IndexSnapshot snap = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
        float[] q = Cloud(9, 1, Dims)[0];
        foreach ((string tag, Func<int, bool> accept) in new (string, Func<int, bool>)[] { ("five", i => i % 5 == 0), ("other", i => i % 5 != 0) })
        {
            var knn = new KnnQuery("v", q, 10) { Filter = new TermQuery("tag", tag), Exact = true };
            Assert.Equal(Expected(data, [], q, 10, VectorMetric.Cosine, accept), Ids(searcher, searcher.Search(knn, 10)));
            // The graph path with the same filter finds nearly the same set.
            var approx = knn with { Exact = false, Ef = 150 };
            TopDocs top = searcher.Search(approx, 10);
            Assert.Equal(10, top.Hits.Count);
            Assert.True(Ids(searcher, top).Intersect(Expected(data, [], q, 10, VectorMetric.Cosine, accept)).Count() >= 8);
        }

        // The knn clause composes with text clauses in a boolean query.
        var combined = BooleanQuery.Of(new BooleanClause(new KnnQuery("v", q, 20), Occur.Must), new BooleanClause(new TermQuery("tag", "five"), Occur.Filter));
        TopDocs mixed = searcher.Search(combined, 50);
        Assert.All(Ids(searcher, mixed), id => Assert.Equal(0, int.Parse(id[1..]) % 5));
    }

    [Fact]
    public void BadQueriesAreRejectedByName()
    {
        float[][] data = Cloud(10, 30, Dims);
        using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), VectorSchema(VectorMetric.Cosine), new IndexWriterOptions { AutoMerge = false });
        Fill(writer, data, 0, 30);
        writer.Commit();
        using IndexSnapshot snap = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
        Assert.Contains("dimensions", Assert.Throws<QueryException>(() => searcher.Search(new KnnQuery("v", new float[3], 5), 5)).Message);
        Assert.Contains("indexed vector field", Assert.Throws<QueryException>(() => searcher.Search(new KnnQuery("tag", data[0], 5), 5)).Message);
        Assert.Throws<QueryException>(() => searcher.Search(new KnnQuery("v", data[0], 0), 5));
        float[] bad = (float[])data[0].Clone();
        bad[2] = float.NaN;
        Assert.Throws<QueryException>(() => searcher.Search(new KnnQuery("v", bad, 5), 5));
        Assert.Throws<ArgumentException>(() => writer.Upsert(new Document().Add("id", "x").Add("tag", "t").Add("v", bad)));
    }

    [Fact]
    public void MutatedVectorAndGraphFilesOnlyEverRaiseCorruption()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oneindex-vector-seeds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            OneIndex.Fuzz.VectorTarget.WriteSeeds(dir);
            var rng = new Random(77);
            int survived = 0;
            foreach (string seed in Directory.GetFiles(dir))
            {
                byte[] original = File.ReadAllBytes(seed);
                for (int round = 0; round < 600; round++)
                {
                    byte[] input = (byte[])original.Clone();
                    input[0] = (byte)((input[0] & ~7) | rng.Next(8));
                    int edits = 1 + rng.Next(4);
                    for (int e = 0; e < edits; e++)
                    {
                        // Half of the edits land in the last 400 bytes, where the directory and the upper layers are.
                        int at = rng.Next(2) == 0 ? input.Length - 1 - IndexFile.FooterLength - rng.Next(Math.Min(400, input.Length - 20)) : 1 + rng.Next(input.Length - 1);
                        input[at] = (byte)rng.Next(256);
                    }

                    if (rng.Next(10) == 0)
                    {
                        Array.Resize(ref input, 1 + rng.Next(input.Length));
                    }

                    OneIndex.Fuzz.VectorTarget.Run(input);
                    survived++;
                }
            }

            Assert.Equal(2400, survived);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ADamagedVectorBlockRaisesCorruptionNamingTheFile()
    {
        float[][] data = Cloud(11, 200, Dims);
        var storage = new MemoryStorage();
        using (IndexWriter writer = IndexWriter.Open(storage, VectorSchema(VectorMetric.Cosine), new IndexWriterOptions { AutoMerge = false }))
        {
            Fill(writer, data, 0, 200);
            writer.Commit();
        }

        using IndexWriter reopened = IndexWriter.Open(storage, VectorSchema(VectorMetric.Cosine), new IndexWriterOptions { AutoMerge = false });
        using IndexSnapshot snap = reopened.OpenSnapshot();
        SegmentFile file = snap.GetSegment(0).Info.Files.First(f => f.Part == SegmentParts.Vectors);
        // Rewrite the file with one byte of vector data flipped, keeping its length.
        byte[] bytes;
        using (IRangeReader r = storage.OpenRead(file.FileName))
        {
            bytes = new byte[r.Length];
            r.ReadExactly(0, bytes);
        }

        bytes[40] ^= 0x55;
        storage.Delete(file.FileName);
        using (IStorageWriter w = storage.Create(file.FileName))
        {
            w.Write(bytes);
            w.Sync();
        }

        using IndexSnapshot damaged = reopened.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(damaged);
        CorruptIndexException e = Assert.Throws<CorruptIndexException>(() => searcher.Search(new KnnQuery("v", data[0], 5) { Exact = true }, 5));
        Assert.Contains(file.FileName, e.Message);
    }
}

/// <summary>A mapped type with a vector property.</summary>
public class Passage
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Text { get; set; } = string.Empty;

    [Keyword]
    [Filterable]
    public string Topic { get; set; } = string.Empty;

    [Vector(Dimensions = 8, FlatThreshold = 10)]
    public float[] Embedding { get; set; } = [];
}

public sealed class VectorHappyPathTests
{
    private static float[] Unit(Random rng)
    {
        var v = new float[8];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = (float)(rng.NextDouble() - 0.5);
        }

        float n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    [Fact]
    public void NearestSearchWithFiltersQueryJsonAndToolSchemaWork()
    {
        var rng = new Random(12);
        using SearchIndex<Passage> index = SearchIndex.InMemory<Passage>();
        var all = new List<Passage>();
        for (int i = 0; i < 200; i++)
        {
            var p = new Passage { Id = "p" + i, Text = "passage " + i, Topic = "t" + (i % 4), Embedding = Unit(rng) };
            all.Add(p);
            index.Upsert(p);
        }

        index.Commit();
        float[] q = Unit(rng);
        SearchResults<Passage> hits = index.SearchVector(x => x.Embedding, q, neighbors: 30).Where(x => x.Topic == "t1").Take(10).Execute();
        Assert.Equal(10, hits.Count);
        Assert.All(hits, h => Assert.Equal("t1", h.Document.Topic));
        List<string> expected = all.Where(p => p.Topic == "t1")
            .OrderByDescending(p => p.Embedding.Zip(q, (a, b) => a * b).Sum()).ThenBy(p => p.Id, StringComparer.Ordinal).Take(10).Select(p => p.Id).ToList();
        Assert.True(hits.Select(h => h.Id).Intersect(expected).Count() >= 9);
        Assert.Equal(all.First(p => p.Id == hits[0].Id).Embedding, hits[0].Document.Embedding);
        Assert.True(hits[0].Score >= hits[9].Score);

        Search.Query knn = index.SearchVector(x => x.Embedding, q, neighbors: 5).Where(x => x.Topic == "t2").ToQuery();
        string json = QueryJson.WriteQuery(knn);
        Assert.Contains("\"type\":\"knn\"", json, StringComparison.Ordinal);
        Search.Query back = QueryJson.Read("{\"v\":1,\"query\":" + json + "}", index.Schema).Query!;
        Assert.Equal(knn, back);
        QueryParseException bad = Assert.Throws<QueryParseException>(() => QueryJson.Read("{\"v\":1,\"query\":{\"type\":\"knn\",\"field\":\"Embedding\",\"vector\":[1,2]}}", index.Schema));
        Assert.Contains("exactly 8 numbers", bad.Message, StringComparison.Ordinal);
        Assert.Contains("knn0", SearchTool.For<Passage>(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => index.SearchVector(x => x.Embedding, new float[3]));
        Assert.Throws<ArgumentException>(() => index.SearchVector(x => x.Topic, q));
    }
}
