using System.Security.Cryptography;
using OneIndex.Index;
using OneIndex.Kernels;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Tests;

public sealed class QuantizedVectorTests
{
    private static float[] Gaussian(Random rng, int dims, bool unit)
    {
        var v = new float[dims];
        double sum = 0;
        for (int i = 0; i < dims; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * rng.NextDouble()));
            sum += v[i] * v[i];
        }

        if (unit)
        {
            float inv = (float)(1 / Math.Sqrt(sum));
            for (int i = 0; i < dims; i++)
            {
                v[i] *= inv;
            }
        }

        return v;
    }

    [Fact]
    public void PackedFourBitDotMatchesTheScalarLoopAtEveryWidthAndLength()
    {
        var rng = new Random(5);
        int cases = 0;
        foreach (KernelIsa isa in new[] { KernelIsa.Scalar, KernelIsa.Vector128, KernelIsa.Vector256, KernelIsa.Vector512 })
        {
            for (int h = 0; h <= 200; h++)
            {
                var packed = new byte[h];
                var low = new sbyte[h];
                var high = new sbyte[h];
                rng.NextBytes(packed);
                for (int i = 0; i < h; i++)
                {
                    low[i] = (sbyte)rng.Next(-128, 128);
                    high[i] = (sbyte)rng.Next(-128, 128);
                }

                int expected = 0;
                for (int i = 0; i < h; i++)
                {
                    expected += ((packed[i] & 15) * low[i]) + ((packed[i] >> 4) * high[i]);
                }

                Assert.Equal(expected, QuantizedKernels.DotInt4(isa, packed, low, high));
                cases++;
            }
        }

        Assert.Equal(804, cases);
        Assert.Throws<ArgumentException>(() => QuantizedKernels.DotInt4(KernelIsa.Scalar, new byte[3], new sbyte[2], new sbyte[3]));
    }

    [Fact]
    public void HammingCountsDifferingBits()
    {
        var rng = new Random(6);
        for (int words = 0; words < 20; words++)
        {
            var a = new byte[words * 8];
            var b = new byte[words * 8];
            rng.NextBytes(a);
            rng.NextBytes(b);
            int expected = 0;
            for (int i = 0; i < a.Length; i++)
            {
                expected += System.Numerics.BitOperations.PopCount((uint)(a[i] ^ b[i]));
            }

            Assert.Equal(expected, QuantizedKernels.Hamming(a, b));
        }

        Assert.Throws<ArgumentException>(() => QuantizedKernels.Hamming(new byte[7], new byte[7]));
    }

    [Theory]
    [InlineData(VectorQuantization.Scalar8, 0.002, 0.012, 0.02, 0.3)]
    [InlineData(VectorQuantization.Scalar4, 0.015, 0.08, 0.2, 1.2)]
    public void QuantizedDistancesTrackTheDoubleReference(VectorQuantization codec, double dotMean, double dotMax, double l2Mean, double l2Max)
    {
        var rng = new Random(11);
        double worst = 0, total = 0;
        long count = 0;
        var perKind = new Dictionary<DistanceKind, (double Total, double Worst, long Count)>();
        foreach (int dims in new[] { 3, 17, 64, 384 })
        {
            foreach (DistanceKind kind in new[] { DistanceKind.NegativeDot, DistanceKind.SquaredL2 })
            {
                bool unit = kind == DistanceKind.NegativeDot;
                var flat = new float[200 * dims];
                for (int i = 0; i < 200; i++)
                {
                    Gaussian(rng, dims, unit).CopyTo(flat, i * dims);
                }

                var values = new ArrayVectorValues(flat, dims);
                ArrayQuantizedValues compressed = ArrayQuantizedValues.Build(values, codec);
                var scorer = new QuantizedNodeScorer();
                for (int q = 0; q < 10; q++)
                {
                    float[] query = Gaussian(rng, dims, unit);
                    scorer.Reset(compressed, codec, kind, query);
                    for (int i = 0; i < 200; i++)
                    {
                        double reference = 0;
                        ReadOnlySpan<float> v = values.Get(i);
                        for (int j = 0; j < dims; j++)
                        {
                            double d = kind == DistanceKind.NegativeDot ? -((double)v[j] * query[j]) : ((double)v[j] - query[j]) * ((double)v[j] - query[j]);
                            reference += d;
                        }

                        // Squared distances of unit-scale Gaussians are about 2 * dims; compare on the same footing as dot products.
                        double scale = kind == DistanceKind.NegativeDot ? 1 : Math.Sqrt(dims);
                        double error = Math.Abs(scorer.ToQuery(i) - reference) / scale;
                        worst = Math.Max(worst, error);
                        total += error;
                        count++;
                        (double t, double w, long c) = perKind.GetValueOrDefault(kind);
                        perKind[kind] = (t + error, Math.Max(w, error), c + 1);
                    }
                }
            }
        }

        foreach ((DistanceKind k, (double t, double w, long c)) in perKind)
        {
            TestContext.Current.SendDiagnosticMessage($"{codec} {k}: {c} distances, mean error {t / c:F5}, max error {w:F5}");
        }

        Assert.True(perKind[DistanceKind.NegativeDot].Total / perKind[DistanceKind.NegativeDot].Count < dotMean);
        Assert.True(perKind[DistanceKind.NegativeDot].Worst < dotMax);
        Assert.True(perKind[DistanceKind.SquaredL2].Total / perKind[DistanceKind.SquaredL2].Count < l2Mean);
        Assert.True(perKind[DistanceKind.SquaredL2].Worst < l2Max);
    }

    [Fact]
    public void SignBitHammingOrdersByAngleOnAverage()
    {
        // The Hamming distance of sign bits estimates the angle: vectors nearer the query have clearly smaller distances.
        var rng = new Random(12);
        int dims = 256;
        float[] query = Gaussian(rng, dims, true);
        var flat = new float[500 * dims];
        for (int i = 0; i < 500; i++)
        {
            float t = i / 500f;
            float[] g = Gaussian(rng, dims, true);
            for (int j = 0; j < dims; j++)
            {
                g[j] = (t * query[j]) + ((1 - t) * g[j]);
            }

            VectorKernels.Normalize(g);
            g.CopyTo(flat, i * dims);
        }

        var values = new ArrayVectorValues(flat, dims);
        ArrayQuantizedValues bits = ArrayQuantizedValues.Build(values, VectorQuantization.OneBit);
        var scorer = new QuantizedNodeScorer();
        scorer.Reset(bits, VectorQuantization.OneBit, DistanceKind.NegativeDot, query);
        var pairs = new List<(double Dot, float Hamming)>();
        for (int i = 0; i < 500; i++)
        {
            pairs.Add((VectorKernels.Dot(query, values.Get(i)), scorer.ToQuery(i)));
        }

        double near = pairs.OrderByDescending(p => p.Dot).Take(50).Average(p => p.Hamming);
        double far = pairs.OrderBy(p => p.Dot).Take(50).Average(p => p.Hamming);
        Assert.True(near < far - 40, $"near {near}, far {far}");
    }

    [Fact]
    public void ConstantAndZeroVectorsQuantizeWithoutDividingByZero()
    {
        foreach (VectorQuantization codec in new[] { VectorQuantization.Scalar8, VectorQuantization.Scalar4, VectorQuantization.OneBit })
        {
            var flat = new float[3 * 8];
            Array.Fill(flat, 0.25f, 8, 8);
            flat[16] = 1;
            var values = new ArrayVectorValues(flat, 8);
            ArrayQuantizedValues c = ArrayQuantizedValues.Build(values, codec);
            var scorer = new QuantizedNodeScorer();
            scorer.Reset(c, codec, DistanceKind.NegativeDot, new float[8]);
            for (int i = 0; i < 3; i++)
            {
                Assert.True(float.IsFinite(scorer.ToQuery(i)));
            }

            scorer.Reset(c, codec, DistanceKind.SquaredL2, [0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f]);
            Assert.True(float.IsFinite(scorer.ToQuery(1)));
            if (codec != VectorQuantization.OneBit)
            {
                Assert.True(Math.Abs(scorer.ToQuery(1)) < 1e-3, "a constant vector is reproduced exactly");
            }
        }
    }

    [Fact]
    public void EncodedRecordsAreTheSameBytesOnEveryMachine()
    {
        // The encoders use IEEE double and integer arithmetic in a fixed order, so these hashes hold on x64 and arm64.
        var rng = new Random(2024);
        var flat = new float[100 * 37];
        for (int i = 0; i < 100; i++)
        {
            Gaussian(rng, 37, true).CopyTo(flat, i * 37);
        }

        var values = new ArrayVectorValues(flat, 37);
        string Hash(VectorQuantization codec)
        {
            var data = new List<byte>();
            for (int i = 0; i < 100; i++)
            {
                data.AddRange(ArrayQuantizedValues.Build(values, codec).Get(i).ToArray());
            }

            return Convert.ToHexString(SHA256.HashData(data.ToArray()))[..16];
        }

        TestContext.Current.SendDiagnosticMessage($"hashes {Hash(VectorQuantization.Scalar8)} {Hash(VectorQuantization.Scalar4)} {Hash(VectorQuantization.OneBit)}");
        Assert.Equal("AC0C4E8DDD9CCD4B", Hash(VectorQuantization.Scalar8));
        Assert.Equal("3ECB875FFFDF8E75", Hash(VectorQuantization.Scalar4));
        Assert.Equal("84B9B6F2F8A2BFCE", Hash(VectorQuantization.OneBit));
    }

    private static Schema QuantizedSchema(VectorQuantization codec, VectorMetric metric = VectorMetric.Cosine, float oversample = 0)
        => new SchemaBuilder().Id("id").Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = 96, Metric = metric, FlatThreshold = 50, Quantization = codec, Oversample = oversample }).Build();

    [Theory]
    [InlineData(VectorQuantization.None)]
    [InlineData(VectorQuantization.Scalar8)]
    [InlineData(VectorQuantization.Scalar4)]
    [InlineData(VectorQuantization.OneBit)]
    public void QuantizedGraphSearchReranksToExactScoresAndKeepsRecall(VectorQuantization codec)
    {
        var rng = new Random(31);
        const int n = 2500;
        var data = new float[n][];
        for (int i = 0; i < n; i++)
        {
            data[i] = Gaussian(rng, 96, true);
        }

        var storage = new MemoryStorage();
        using IndexWriter writer = IndexWriter.Open(storage, QuantizedSchema(codec, oversample: codec == VectorQuantization.OneBit ? 20 : 0), new IndexWriterOptions { MaxBufferedDocuments = 1000, AutoMerge = false });
        for (int i = 0; i < n; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("v", data[i]));
        }

        writer.Commit();
        void Check(IndexWriter w, string when)
        {
            using IndexSnapshot snap = w.OpenSnapshot();
            IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
            foreach (LeafContext leaf in searcher.Context.Leaves)
            {
                var field = leaf.Reader.GetVectors("v")!;
                Assert.True(field.HasGraph, when);
            }

            int hits = 0, total = 0;
            for (int q = 0; q < 40; q++)
            {
                float[] query = Gaussian(rng, 96, true);
                TopDocs approx = searcher.Search(new KnnQuery("v", query, 10) { Ef = 150 }, 10);
                TopDocs exact = searcher.Search(new KnnQuery("v", query, 10) { Exact = true }, 10);
                var truth = exact.Hits.Select(h => h.DocId).ToHashSet();
                hits += approx.Hits.Count(h => truth.Contains(h.DocId));
                total += 10;
                // The returned scores are measured on the full-precision vectors, so a document in both lists has the same score.
                foreach (ScoreDoc h in approx.Hits.Where(h => truth.Contains(h.DocId)))
                {
                    Assert.Equal(exact.Hits.First(e => e.DocId == h.DocId).Score, h.Score);
                }
            }

            double recall = hits / (double)total;
            TestContext.Current.SendDiagnosticMessage($"{codec} {when}: recall@10 at ef 150 = {recall:F3}");
            Assert.True(recall >= (codec == VectorQuantization.OneBit ? 0.6 : 0.9), $"{codec} {when} recall {recall}");
        }

        Check(writer, "flushed");
        writer.ForceMerge(1);
        writer.Commit();
        Check(writer, "merged");
        using IndexSnapshot merged = writer.OpenSnapshot();
        bool hasPart = merged.GetSegment(0).Info.Files.Any(f => f.Part == SegmentParts.Quantized);
        Assert.Equal(codec != VectorQuantization.None, hasPart);
    }

    [Fact]
    public void ADamagedCompressedBlockRaisesCorruptionNamingTheFile()
    {
        var rng = new Random(3);
        var data = Enumerable.Range(0, 300).Select(_ => Gaussian(rng, 96, true)).ToArray();
        var storage = new MemoryStorage();
        using IndexWriter writer = IndexWriter.Open(storage, QuantizedSchema(VectorQuantization.Scalar4), new IndexWriterOptions { AutoMerge = false });
        for (int i = 0; i < data.Length; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("v", data[i]));
        }

        writer.Commit();
        using IndexSnapshot snap = writer.OpenSnapshot();
        SegmentFile file = snap.GetSegment(0).Info.Files.First(f => f.Part == SegmentParts.Quantized);
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

        using IndexSnapshot damaged = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(damaged);
        CorruptIndexException e = Assert.Throws<CorruptIndexException>(() => searcher.Search(new KnnQuery("v", data[0], 5), 5));
        Assert.Contains(file.FileName, e.Message);
    }

    [Fact]
    public void SchemaRecordsTheCodecAndRefusesSignBitsOnEuclidean()
    {
        foreach (VectorQuantization codec in new[] { VectorQuantization.Scalar8, VectorQuantization.Scalar4, VectorQuantization.OneBit })
        {
            Schema s = QuantizedSchema(codec, VectorMetric.DotProduct, oversample: 7.5f);
            Schema back = Schema.Deserialize(s.Serialize());
            Assert.True(back.TryGetField("v", out FieldDefinition? f));
            Assert.Equal(codec, f.Quantization);
            Assert.Equal(7.5f, f.Oversample);
            Assert.Equal(VectorMetric.DotProduct, f.Metric);
        }

        // A schema without a codec is written exactly as before.
        Schema plain = QuantizedSchema(VectorQuantization.None);
        Assert.True(Schema.Deserialize(plain.Serialize()).TryGetField("v", out FieldDefinition? plainField));
        Assert.Equal(VectorQuantization.None, plainField.Quantization);
        Assert.Throws<ArgumentException>(() => QuantizedSchema(VectorQuantization.OneBit, VectorMetric.Euclidean));
        Assert.Throws<ArgumentException>(() => QuantizedSchema(VectorQuantization.Scalar8, oversample: 0.5f));
        Assert.Throws<ArgumentException>(() => QuantizedSchema(VectorQuantization.None, oversample: 3));
    }

    [Fact]
    public void TheAttributeChoosesTheCodecOnTheHappyPath()
    {
        var rng = new Random(9);
        using SearchIndex<QuantizedPassage> index = SearchIndex.InMemory<QuantizedPassage>();
        Assert.True(index.Schema.TryGetField("Embedding", out FieldDefinition? field));
        Assert.Equal(VectorQuantization.Scalar4, field.Quantization);
        Assert.Equal(4f, field.Oversample);
        var all = new List<QuantizedPassage>();
        for (int i = 0; i < 300; i++)
        {
            float[] v = Gaussian(rng, 16, true);
            var p = new QuantizedPassage { Id = "p" + i, Embedding = v };
            all.Add(p);
            index.Upsert(p);
        }

        index.Commit();
        float[] q = Gaussian(rng, 16, true);
        SearchResults<QuantizedPassage> hits = index.SearchVector(x => x.Embedding, q, neighbors: 10).Take(10).Execute();
        List<string> expected = all.OrderByDescending(p => p.Embedding.Zip(q, (a, b) => a * b).Sum()).Take(10).Select(p => p.Id).ToList();
        Assert.True(hits.Select(h => h.Id).Intersect(expected).Count() >= 9);
    }
}

/// <summary>A mapped type whose vector field keeps a 4-bit compressed copy.</summary>
public class QuantizedPassage
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [Vector(Dimensions = 16, FlatThreshold = 20, Quantization = VectorQuantization.Scalar4, Oversample = 4)]
    public float[] Embedding { get; set; } = [];

    [Fact]
    public void AllSchemaSectionsRoundTripTogether()
    {
        // Text settings (section 2), vector settings (3) and compressed copies (4) share one version-2 record in ascending order.
        Schema schema = new SchemaBuilder { Settings = new TextSettings { PrefixSearch = false } }.Id("id").Text("t")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = 8, HnswM = 24, Quantization = VectorQuantization.Scalar4, Oversample = 4 }).Build();
        byte[] bytes = schema.Serialize();
        Schema back = Schema.Deserialize(bytes);
        Assert.False(back.Settings.PrefixSearch);
        FieldDefinition v = back.Fields.Single(f => f.Name == "v");
        Assert.Equal(24, v.HnswM);
        Assert.Equal(VectorQuantization.Scalar4, v.Quantization);
        Assert.Equal(4f, v.Oversample);
        Assert.Equal(bytes, back.Serialize());
    }
}
