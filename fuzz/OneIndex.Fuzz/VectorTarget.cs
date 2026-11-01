using System.Buffers.Binary;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Fuzz;

/// <summary>
/// Feeds arbitrary bytes in place of a segment's vectors file, graph file or compressed-vectors file (the others stay valid), then reads everything the
/// damaged file offers: every stored vector, and exact and graph searches with and without a filter and deletes. Only
/// <see cref="CorruptIndexException"/> may come out; a crash, a hang or an allocation past the limits is a finding. Byte 0 of the input
/// picks the file (bit 3: the compressed vectors; else bit 1: 0 vectors, 1 graph), whether the header and footer are repaired (bit 0) and whether the block and table
/// checksums are skipped (bit 2), so that mutations reach the structure parsers instead of stopping at a checksum.
/// </summary>
internal static class VectorTarget
{
    private const int Documents = 150;
    private const int Dimensions = 8;

    private static readonly IndexLimits Limits = new() { MaxResidentFileBytes = 1 << 22, MaxDocsPerSegment = 100_000, MaxFields = 64, MaxStringBytes = 512 };
    private static readonly Lazy<(Schema Schema, byte[] Vectors, byte[] Graph, byte[] Quantized)> Good = new(Build);

    private static (Schema, byte[], byte[], byte[]) Build()
    {
        Schema schema = new SchemaBuilder().Id("id")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = Dimensions, FlatThreshold = 20, HnswM = 4, EfConstruction = 16, Quantization = VectorQuantization.Scalar4 })
            .Add(new FieldDefinition("w", FieldType.Vector) { Dimensions = Dimensions, FlatThreshold = 20, HnswM = 4, EfConstruction = 16, Metric = VectorMetric.Euclidean, Quantization = VectorQuantization.Scalar8 })
            .Add(new FieldDefinition("x", FieldType.Vector) { Dimensions = Dimensions, FlatThreshold = 20, HnswM = 4, EfConstruction = 16, Quantization = VectorQuantization.OneBit })
            .Build();
        var storage = new MemoryStorage();
        using var writer = IndexWriter.Open(storage, schema, new IndexWriterOptions { AutoMerge = false, MaxBufferedDocuments = 1000 });
        var rng = new Random(7);
        for (int i = 0; i < Documents; i++)
        {
            var doc = new Document().Add("id", "d" + i);
            doc.Add("v", Vector(rng));
            if (i % 3 != 0)
            {
                doc.Add("w", Vector(rng));
            }

            if (i % 2 == 0)
            {
                doc.Add("x", Vector(rng));
            }

            writer.Upsert(doc);
        }

        writer.Commit();
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        SegmentInfo info = snapshot.GetSegment(0).Info;
        return (schema, Read(storage, info.FindPart(SegmentParts.Vectors)!.FileName), Read(storage, info.FindPart(SegmentParts.Graph)!.FileName), Read(storage, info.FindPart(SegmentParts.Quantized)!.FileName));
    }

    private static float[] Vector(Random rng)
    {
        var v = new float[Dimensions];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = (float)(rng.NextDouble() - 0.5);
        }

        return v;
    }

    private static byte[] Read(MemoryStorage storage, string name)
    {
        using IRangeReader r = storage.OpenRead(name);
        var bytes = new byte[r.Length];
        r.ReadExactly(0, bytes);
        return bytes;
    }

    public static void WriteSeeds(string directory)
    {
        var good = Good.Value;
        File.WriteAllBytes(Path.Combine(directory, "vectors"), [5, .. good.Vectors]);
        File.WriteAllBytes(Path.Combine(directory, "graph"), [7, .. good.Graph]);
        File.WriteAllBytes(Path.Combine(directory, "quantized"), [13, .. good.Quantized]);
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        bool quantized = (data[0] & 8) != 0;
        bool graph = !quantized && (data[0] & 2) != 0;
        byte[] file = data[1..].ToArray();
        if ((data[0] & 1) != 0)
        {
            Repair(file, graph, quantized);
        }

        VectorFiles.VerifyChecksums = (data[0] & 4) == 0;
        try
        {
            Exercise(graph || quantized ? Good.Value.Vectors : file, graph ? file : Good.Value.Graph, quantized ? file : Good.Value.Quantized);
        }
        finally
        {
            VectorFiles.VerifyChecksums = true;
        }
    }

    /// <summary>Opens and reads one pair of files; <see cref="CorruptIndexException"/> is the expected outcome for damaged input.</summary>
    public static void Exercise(byte[] vectors, byte[] graph, byte[] quantized)
    {
        var good = Good.Value;
        var storage = new MemoryStorage();
        foreach ((string name, byte[] bytes) in new[] { ("s.vec", vectors), ("s.hnsw", graph), ("s.vecq", quantized) })
        {
            using IStorageWriter w = storage.Create(name);
            w.Write(bytes);
        }

        try
        {
            using VectorPartReader part = VectorPartReader.Open(storage.OpenRead("s.vec"), storage.OpenRead("s.hnsw"), storage.OpenRead("s.vecq"), good.Schema, Documents, Limits, default);
            var rng = new Random(3);
            var hits = new VectorHit[10];
            var buffer = new float[Dimensions];
            var half = new ulong[(Documents + 63) / 64];
            for (int d = 0; d < Documents; d += 2)
            {
                half[d >> 6] |= 1UL << (d & 63);
            }

            var live = new EveryThirdDeleted();
            foreach (string name in part.Names.ToList())
            {
                VectorFieldReader field = part.GetField(name)!;
                for (int d = 0; d < Documents; d++)
                {
                    _ = field.TryGetVector(d, buffer);
                }

                for (int q = 0; q < 4; q++)
                {
                    float[] query = Vector(rng);
                    _ = field.Search(query, 10, 0, null, null, exact: true, hits);
                    _ = field.Search(query, 10, 30, null, null, exact: false, hits);
                    _ = field.Search(query, 10, 30, live, half, exact: false, hits);
                    _ = field.Search(query, 5, 0, live, null, exact: false, hits);
                }
            }
        }
        catch (CorruptIndexException)
        {
            // Expected for damaged input.
        }
    }

    private sealed class EveryThirdDeleted : ILiveDocs
    {
        public int DeletedCount => Documents / 3;

        public bool IsLive(int docId) => docId % 3 != 1;
    }

    private static void Repair(byte[] file, bool graph, bool quantized)
    {
        if (file.Length < IndexFile.HeaderLength + IndexFile.FooterLength)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(file, quantized ? VectorSegmentCodec.QuantizedMagic : graph ? VectorSegmentCodec.GraphMagic : VectorSegmentCodec.VectorsMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 1);
        Span<byte> footer = file.AsSpan(file.Length - IndexFile.FooterLength);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, IndexFile.FooterMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], IndexFile.AlgorithmCrc32C);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], Crc32C.Compute(file.AsSpan(0, file.Length - 4)));
    }
}
