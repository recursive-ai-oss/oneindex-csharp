using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Tests;

/// <summary>
/// Filtered nearest-neighbour search on generated corpora: a hit never fails the filter or is deleted, exact search equals brute force
/// over the passing documents, a selective filter (answered by the exact scan) equals the exact result, and the filtered graph walk finds
/// most true neighbours and fills the page. Filters come from a keyword term, from a numeric range, and from a range over a key that
/// follows the vectors' position (a correlated filter).
/// </summary>
public sealed class FilteredVectorTests
{
    private const int Dims = 24;
    private const int Count = 6000;
    private const int Ef = 30;

    private static float[][] Cloud(int seed, int n)
    {
        var rng = new Random(seed);
        var centers = new float[12][];
        for (int c = 0; c < centers.Length; c++)
        {
            centers[c] = Gaussian(rng);
        }

        var all = new float[n][];
        for (int i = 0; i < n; i++)
        {
            float[] v = Gaussian(rng);
            float[] c = centers[rng.Next(centers.Length)];
            for (int j = 0; j < Dims; j++)
            {
                v[j] = (v[j] * 0.5f) + c[j];
            }

            float norm = MathF.Sqrt(v.Sum(x => x * x));
            all[i] = v.Select(x => x / norm).ToArray();
        }

        return all;
    }

    private static float[] Gaussian(Random rng)
    {
        var v = new float[Dims];
        for (int i = 0; i < Dims; i++)
        {
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble()));
        }

        return v;
    }

    private static float Dot(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }

    [Theory]
    [InlineData(VectorQuantization.None)]
    [InlineData(VectorQuantization.Scalar4)]
    public void FilteredSearchNeverReturnsANonMatchingDocumentAndKeepsRecall(VectorQuantization codec)
    {
        float[][] data = Cloud(31, Count);
        float[] direction = Gaussian(new Random(5));
        // The correlated key is the rank of the vector's projection on one direction: a range over it selects a region of the space.
        int[] byProjection = Enumerable.Range(0, Count).OrderBy(i => Dot(data[i], direction)).ToArray();
        var rank = new int[Count];
        for (int r = 0; r < Count; r++)
        {
            rank[byProjection[r]] = r;
        }

        Schema schema = new SchemaBuilder().Id("id").Keyword("bucket").Integral("n").Integral("rank")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = Dims, Metric = VectorMetric.Cosine, FlatThreshold = 50, Quantization = codec })
            .Build();
        using var writer = IndexWriter.Open(new MemoryStorage(), schema, new IndexWriterOptions { MaxBufferedDocuments = Count + 1, AutoMerge = false });
        for (int i = 0; i < Count; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("bucket", "b" + (i % 100)).Add("n", (long)i).Add("rank", (long)rank[i]).Add("v", data[i]));
        }

        writer.Commit();
        var deleted = new HashSet<int>();
        for (int i = 7; i < Count; i += 41)
        {
            Assert.True(writer.DeleteById("d" + i));
            deleted.Add(i);
        }

        writer.Commit();
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snapshot);
        Assert.True(searcher.Context.Leaves.Single().Reader.GetVectors("v") is { HasGraph: true });

        // (name, filter, predicate, expected path). A segment answers a filter with the exact scan when it admits at most 50 * ef documents (1,500).
        var filters = new (string Name, Query Filter, Func<int, bool> Accepts, bool Scan)[]
        {
            ("n < 50%", new RangeQuery("n", null, FieldValue.FromInt64(Count / 2)) { IncludeUpper = false }, i => i < Count / 2, false),
            ("n < 30%", new RangeQuery("n", null, FieldValue.FromInt64(Count * 3 / 10)) { IncludeUpper = false }, i => i < Count * 3 / 10, false),
            ("rank < 40% (correlated)", new RangeQuery("rank", null, FieldValue.FromInt64(Count * 4 / 10)) { IncludeUpper = false }, i => rank[i] < Count * 4 / 10, false),
            ("rank < 10% (correlated)", new RangeQuery("rank", null, FieldValue.FromInt64(Count / 10)) { IncludeUpper = false }, i => rank[i] < Count / 10, true),
            ("bucket b3 (1%)", new TermQuery("bucket", "b3"), i => i % 100 == 3, true),
            ("rank < 0.2% (correlated)", new RangeQuery("rank", null, FieldValue.FromInt64(12)) { IncludeUpper = false }, i => rank[i] < 12, true),
        };
        float[][] queries = Cloud(32, 40);
        int violations = 0;
        int cases = 0;
        foreach ((string name, Query filter, Func<int, bool> accepts, bool scan) in filters)
        {
            int found = 0;
            int possible = 0;
            foreach (float[] q in queries)
            {
                List<string> truth = Enumerable.Range(0, Count).Where(i => !deleted.Contains(i) && accepts(i))
                    .Select(i => (Id: i, D: -Dot(Normalize(q), data[i]))).OrderBy(x => x.D).ThenBy(x => x.Id).Take(10).Select(x => "d" + x.Id).ToList();

                List<string> exact = Ids(searcher, searcher.Search(new KnnQuery("v", q, 10) { Filter = filter, Exact = true }, 10));
                Assert.Equal(truth, exact);

                List<string> approx = Ids(searcher, searcher.Search(new KnnQuery("v", q, 10) { Filter = filter, Ef = Ef }, 10));
                cases++;
                foreach (string id in approx)
                {
                    int n = int.Parse(id[1..], System.Globalization.CultureInfo.InvariantCulture);
                    if (!accepts(n) || deleted.Contains(n))
                    {
                        violations++;
                    }
                }

                Assert.Equal(truth.Count, approx.Count);
                if (scan)
                {
                    // The exact scan was taken: the answer is the exact one.
                    Assert.Equal(truth, approx);
                }

                found += approx.Intersect(truth).Count();
                possible += truth.Count;
            }

            double recall = found / (double)possible;
            Assert.True(recall >= (scan ? 1.0 : 0.85), $"{name} ({codec}): recall {recall:F3}");
        }

        Assert.Equal(0, violations);
        Assert.Equal(filters.Length * queries.Length, cases);
    }

    [Fact]
    public void HybridSearchHonoursTheFilterInTheVectorArm()
    {
        float[][] data = Cloud(33, 3000);
        Schema schema = new SchemaBuilder().Id("id").Text("body").Integral("n")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = Dims, Metric = VectorMetric.Cosine, FlatThreshold = 50 })
            .Build();
        using var writer = IndexWriter.Open(new MemoryStorage(), schema, new IndexWriterOptions { MaxBufferedDocuments = 3001, AutoMerge = false });
        for (int i = 0; i < data.Length; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("body", "alpha" + (i % 7)).Add("n", (long)i).Add("v", data[i]));
        }

        writer.Commit();
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snapshot);
        Query filter = new RangeQuery("n", null, FieldValue.FromInt64(1000)) { IncludeUpper = false };
        foreach (float[] q in Cloud(34, 10))
        {
            var hybrid = new HybridQuery(BooleanQuery.Of(new BooleanClause(new TermQuery("body", "alpha3"), Occur.Must), new BooleanClause(filter, Occur.Filter)), new KnnQuery("v", q, 10) { Filter = filter, Ef = 40 });
            List<string> ids = Ids(searcher, searcher.Search(hybrid, 20));
            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.True(int.Parse(id[1..], System.Globalization.CultureInfo.InvariantCulture) < 1000));
        }
    }

    private static float[] Normalize(float[] v)
    {
        float n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
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
}
