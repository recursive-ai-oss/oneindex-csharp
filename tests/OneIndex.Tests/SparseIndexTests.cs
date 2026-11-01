using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class SparseIndexTests
{
    private static SparseVector Random(Random rng, int vocab, int maxEntries)
    {
        int n = rng.Next(1, maxEntries);
        var entries = new List<KeyValuePair<string, float>>();
        for (int i = 0; i < n; i++)
        {
            // A skewed token choice: a few common tokens, a long tail, as learned sparse output looks.
            int t = (int)(vocab * Math.Pow(rng.NextDouble(), 3));
            entries.Add(new("t" + t, (float)(rng.NextDouble() * 4)));
        }

        return new SparseVector(entries);
    }

    private static Schema SparseSchema() => new SchemaBuilder().Id("id").Sparse("s").Build();

    [Fact]
    public void TopKEqualsExhaustiveDotProductAcrossSegmentsDeletesAndMerge()
    {
        var rng = new Random(11);
        var docs = Enumerable.Range(0, 1500).Select(_ => Random(rng, 300, 30)).ToArray();
        var deleted = new HashSet<int>();
        var storage = new MemoryStorage();
        using IndexWriter writer = IndexWriter.Open(storage, SparseSchema(), new IndexWriterOptions { MaxBufferedDocuments = 400, AutoMerge = false });
        for (int i = 0; i < docs.Length; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("s", docs[i]));
        }

        writer.Commit();
        for (int i = 0; i < docs.Length; i += 37)
        {
            Assert.True(writer.DeleteById("d" + i));
            deleted.Add(i);
        }

        writer.Commit();
        for (int pass = 0; pass < 2; pass++)
        {
            using (IndexSnapshot snap = writer.OpenSnapshot())
            {
                IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
                Assert.Equal(pass == 0 ? 4 : 1, snap.SegmentCount);
                for (int q = 0; q < 60; q++)
                {
                    SparseVector query = Random(rng, 300, 12);
                    foreach (int k in new[] { 1, 10, 100 })
                    {
                        TopDocs top = searcher.Search(new SparseQuery("s", query), k);
                        var expected = Enumerable.Range(0, docs.Length).Where(i => !deleted.Contains(i))
                            .Select(i => (Id: i, Score: Dot(query, docs[i], quantized: true))).Where(x => x.Score > 0)
                            .OrderByDescending(x => x.Score).ThenBy(x => x.Id).Take(k).ToList();
                        Assert.Equal(expected.Count, top.Hits.Count);
                        for (int r = 0; r < expected.Count; r++)
                        {
                            // Equal scores may come back in another order, so compare the score at each rank.
                            Assert.Equal(expected[r].Score, top.Hits[r].Score, 1e-3);
                        }

                        // The quantized score is within the documented bound of the exact dot product.
                        foreach (ScoreDoc h in top.Hits)
                        {
                            LeafContext leaf = searcher.Context.Leaves.Last(l => l.DocBase <= h.DocId);
                            int id = int.Parse(leaf.Reader.StoredFields!.GetDocument(h.DocId - leaf.DocBase).Get("id").AsText()[1..]);
                            double bound = (0.5 / SparseVector.Scale * query.Weights.Sum()) + 1e-3;
                            Assert.InRange(Dot(query, docs[id], quantized: false) - h.Score, -bound, bound);
                        }
                    }
                }
            }

            if (pass == 0)
            {
                writer.ForceMerge(1);
                writer.Commit();
            }
        }
    }

    private static double Dot(SparseVector q, SparseVector d, bool quantized)
    {
        double sum = 0;
        for (int i = 0; i < q.Count; i++)
        {
            int j = Array.BinarySearch(d.Tokens, q.Tokens[i], StringComparer.Ordinal);
            if (j >= 0)
            {
                double w = quantized ? SparseVector.Quantize(d.Weights[j]) / (double)SparseVector.Scale : d.Weights[j];
                sum += q.Weights[i] * w;
            }
        }

        return sum;
    }

    [Fact]
    public void StoredSparseVectorsRoundTripThroughTheSource()
    {
        var storage = new MemoryStorage();
        var v = new SparseVector([new("a", 1.5f), new("b", 0.25f)]);
        using (IndexWriter writer = IndexWriter.Open(storage, SparseSchema()))
        {
            writer.Upsert(new Document().Add("id", "x").Add("s", v));
            writer.Commit();
        }

        using IndexWriter again = IndexWriter.Open(storage, SparseSchema());
        using IndexSnapshot snap = again.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
        Assert.Equal(v, searcher.Context.Leaves[0].Reader.StoredFields!.GetDocument(0).Get("s").AsSparse());
    }

    [Fact]
    public void QueryJsonRoundTripsSparseNodesAndNamesMistakes()
    {
        Schema schema = SparseSchema();
        QueryRequest r = QueryJson.Read("""{"v":1,"query":{"type":"sparse","field":"s","weights":{"cat":1.5,"dog":0.25},"boost":2}}""", schema);
        var q = Assert.IsType<SparseQuery>(r.Query);
        Assert.Equal(2f, q.Boost);
        Assert.Equal(["cat", "dog"], q.Vector.Tokens);
        string written = QueryJson.Write(r);
        Assert.Equal(r, QueryJson.Read(written, schema));
        Assert.Equal(written, QueryJson.Write(QueryJson.Read(written, schema)));

        QueryParseException e = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"query":{"type":"sparse","field":"id","weights":{}}}""", schema));
        Assert.Contains("sparse field", e.Message);
        e = Assert.Throws<QueryParseException>(() => QueryJson.Read("""{"v":1,"query":{"type":"sparse","field":"s","weights":{"a":-1}}}""", schema));
        Assert.Contains("weights.a", e.Path ?? e.Message);
    }
}
