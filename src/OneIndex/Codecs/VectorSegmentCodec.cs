using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using OneIndex.Index;
using OneIndex.Kernels;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Codecs;

/// <summary>
/// The vector codec: owns the <c>vectors</c> part (the full-precision vectors of every indexed vector field) and the <c>hnsw</c> part (one
/// navigable small-world graph per field that is large enough). See docs/vectors.md for the formats. The full-precision file is
/// kept apart from the graph so that compressed copies added later can search the compressed data and rerank from this file.
/// A merge builds the merged segment's graph again from the live vectors of the sources, in their merged order, which gives the bytes
/// that flushing the same documents would.
/// </summary>
public sealed class VectorSegmentCodec : ISegmentCodec
{
    internal static readonly uint VectorsMagic = IndexFile.Magic("OIXV");
    internal static readonly uint GraphMagic = IndexFile.Magic("OIXH");

    /// <inheritdoc/>
    public string Name => "vectors";

    /// <inheritdoc/>
    public IReadOnlyList<string> Parts { get; } = [SegmentParts.Vectors, SegmentParts.Graph];

    private sealed class Payload(FieldDefinition def, VectorValues values, int[]? ordinalToDoc)
    {
        public FieldDefinition Def { get; } = def;

        public VectorValues Values { get; } = values;

        public int[]? OrdinalToDoc { get; } = ordinalToDoc;

        public HnswBuilder? Graph { get; set; }

        /// <summary>The graph of a source segment this merge continues from, and where its first vector lands.</summary>
        public HnswGraph? Seed { get; set; }

        public int SeedOffset { get; set; }
    }

    /// <summary>
    /// Whether a merge continues from the largest source graph (when that source has no deleted vectors) instead of building all over.
    /// Always true in the library; the benchmark switches it off to measure the difference.
    /// </summary>
    internal static bool ReuseLargestGraph { get; set; } = true;

    private static List<FieldDefinition> VectorFields(Schema schema)
    {
        var fields = schema.Fields.Where(f => f.Type == FieldType.Vector && f.Indexed).ToList();
        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields;
    }

    internal static ulong SeedFor(string field) => 0x4F6E65496E646578UL ^ Crc32C.Compute(Encoding.UTF8.GetBytes(field));

    internal static DistanceKind KindOf(VectorMetric metric) => metric == VectorMetric.Euclidean ? DistanceKind.SquaredL2 : DistanceKind.NegativeDot;

    /// <inheritdoc/>
    public void Flush(SegmentFlushContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        List<FieldDefinition> fields = VectorFields(context.Schema);
        if (fields.Count == 0 || context.Documents.Count == 0)
        {
            return;
        }

        var payloads = new List<Payload>();
        foreach (FieldDefinition def in fields)
        {
            var docs = new List<int>();
            for (int d = 0; d < context.Documents.Count; d++)
            {
                foreach (DocumentField f in context.Documents[d].Fields)
                {
                    if (string.Equals(f.Name, def.Name, StringComparison.Ordinal))
                    {
                        docs.Add(d);
                        break;
                    }
                }
            }

            if (docs.Count == 0)
            {
                continue;
            }

            var data = new float[checked(docs.Count * def.Dimensions)];
            for (int i = 0; i < docs.Count; i++)
            {
                if ((i & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                foreach (DocumentField f in context.Documents[docs[i]].Fields)
                {
                    if (string.Equals(f.Name, def.Name, StringComparison.Ordinal))
                    {
                        Span<float> slot = data.AsSpan(i * def.Dimensions, def.Dimensions);
                        f.Value.AsVector().Span.CopyTo(slot);
                        if (def.Metric == VectorMetric.Cosine)
                        {
                            VectorKernels.Normalize(slot);
                        }

                        break;
                    }
                }
            }

            payloads.Add(new Payload(def, new ArrayVectorValues(data, def.Dimensions), docs.Count == context.Documents.Count ? null : [.. docs]));
        }

        Write(payloads, context.Documents.Count, sink, cancellationToken);
    }

    /// <inheritdoc/>
    public void Merge(SegmentMergeContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        List<FieldDefinition> fields = VectorFields(context.Schema);
        if (fields.Count == 0 || context.DocumentCount == 0)
        {
            return;
        }

        var payloads = new List<Payload>();
        foreach (FieldDefinition def in fields)
        {
            var readers = new VectorFieldReader?[context.Sources.Count];
            for (int s = 0; s < readers.Length; s++)
            {
                readers[s] = (context.Sources[s].Reader as VectorPartReader)?.GetField(def.Name);
            }

            var source = new List<int>();
            var ordinal = new List<int>();
            var newDoc = new List<int>();
            for (int s = 0; s < readers.Length; s++)
            {
                VectorFieldReader? r = readers[s];
                if (r is null)
                {
                    continue;
                }

                DocMap map = context.Sources[s].DocMap;
                for (int o = 0; o < r.Count; o++)
                {
                    if ((o & 4095) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    int nd = map[r.DocOf(o)];
                    if (nd >= 0)
                    {
                        source.Add(s);
                        ordinal.Add(o);
                        newDoc.Add(nd);
                    }
                }
            }

            if (source.Count == 0)
            {
                continue;
            }

            var values = new MergedVectorValues(readers, [.. source], [.. ordinal], def.Dimensions);
            var payload = new Payload(def, values, source.Count == context.DocumentCount ? null : [.. newDoc]);
            if (ReuseLargestGraph)
            {
                // A source whose vectors all survive keeps its numbering order, so its graph maps onto a contiguous range of the new
                // set; the largest such graph saves the most insertions.
                int bestSource = -1;
                int offset = 0;
                int first = 0;
                for (int s = 0; s < readers.Length; s++)
                {
                    VectorFieldReader? r = readers[s];
                    int live = source.Count(x => x == s);
                    if (r?.Graph is { } g && live == r.Count && g.MaxConnections == def.HnswM && (bestSource < 0 || live > readers[bestSource]!.Count))
                    {
                        bestSource = s;
                        offset = first;
                    }

                    first += live;
                }

                if (bestSource >= 0)
                {
                    payload.Seed = readers[bestSource]!.Graph;
                    payload.SeedOffset = offset;
                }
            }

            payloads.Add(payload);
        }

        Write(payloads, context.DocumentCount, sink, cancellationToken);
    }

    private sealed class MergedVectorValues(VectorFieldReader?[] readers, int[] source, int[] ordinal, int dimensions) : VectorValues
    {
        public override int Count => source.Length;

        public override int Dimensions => dimensions;

        public override ReadOnlySpan<float> Get(int index) => readers[source[index]]!.Values.Get(ordinal[index]);
    }

    private static void Write(List<Payload> payloads, int maxDoc, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        if (payloads.Count == 0)
        {
            return;
        }

        foreach (Payload p in payloads)
        {
            if (p.Values.Count >= p.Def.FlatThreshold && p.Values.Count >= 2)
            {
                var builder = new HnswBuilder(p.Values, KindOf(p.Def.Metric), p.Def.HnswM, p.Def.EfConstruction, SeedFor(p.Def.Name));
                if (p.Seed is not null)
                {
                    builder.SeedFrom(p.Seed, p.SeedOffset);
                }

                builder.InsertAll(cancellationToken);
                p.Graph = builder;
            }
        }

        WriteVectors(payloads, maxDoc, sink, cancellationToken);
        if (payloads.Any(p => p.Graph is not null))
        {
            WriteGraphs(payloads, maxDoc, sink, cancellationToken);
        }
    }

    private static void WriteVectors(List<Payload> payloads, int maxDoc, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        using SegmentFileBuilder file = sink.Create(SegmentParts.Vectors, "OIXV", CodecRegistry.NewestVersion(SegmentParts.Vectors), cancellationToken);
        var head = new ByteBuilder(16);
        head.WriteUInt32((uint)maxDoc);
        file.Write(head.Span, cancellationToken);
        var directory = new ByteBuilder(256);
        directory.WriteVarUInt((ulong)payloads.Count);
        foreach (Payload p in payloads)
        {
            int dims = p.Def.Dimensions;
            int recordBytes = dims * 4;
            int perBlock = VectorFiles.RecordsPerBlock(recordBytes);
            int count = p.Values.Count;
            long dataOffset = file.Position;
            var blocks = new BlockWriter(file, perBlock * recordBytes, cancellationToken);
            for (int i = 0; i < count; i++)
            {
                if ((i & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                blocks.Append(MemoryMarshal.AsBytes(p.Values.Get(i)));
            }

            blocks.Finish();
            long ordDocOffset = 0;
            uint ordDocCrc = 0;
            if (p.OrdinalToDoc is { } ordDoc)
            {
                var raw = new byte[ordDoc.Length * 4];
                for (int i = 0; i < ordDoc.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(i * 4), ordDoc[i]);
                }

                ordDocOffset = file.Position;
                ordDocCrc = Crc32C.Compute(raw);
                file.Write(raw, cancellationToken);
            }

            long crcOffset = file.Position;
            byte[] table = VectorFiles.CrcTable(blocks.Crcs);
            file.Write(table, cancellationToken);
            byte[] name = Encoding.UTF8.GetBytes(p.Def.Name);
            directory.WriteVarUInt((ulong)name.Length);
            directory.Write(name);
            directory.WriteVarUInt((ulong)dims);
            directory.WriteByte((byte)p.Def.Metric);
            directory.WriteByte(0); // encoding 0: 32-bit floats
            directory.WriteVarUInt((ulong)count);
            directory.WriteByte((byte)(p.OrdinalToDoc is null ? 0 : 1));
            directory.WriteVarUInt((ulong)dataOffset);
            directory.WriteVarUInt((ulong)perBlock);
            directory.WriteVarUInt((ulong)ordDocOffset);
            directory.WriteVarUInt((ulong)crcOffset);
            directory.WriteUInt64(ordDocCrc);
            directory.WriteUInt64(Crc32C.Compute(table));
        }

        FinishWithDirectory(file, directory, cancellationToken);
    }

    private static void WriteGraphs(List<Payload> payloads, int maxDoc, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        using SegmentFileBuilder file = sink.Create(SegmentParts.Graph, "OIXH", CodecRegistry.NewestVersion(SegmentParts.Graph), cancellationToken);
        var head = new ByteBuilder(16);
        head.WriteUInt32((uint)maxDoc);
        file.Write(head.Span, cancellationToken);
        var directory = new ByteBuilder(256);
        var graphs = payloads.Where(p => p.Graph is not null).ToList();
        directory.WriteVarUInt((ulong)graphs.Count);
        foreach (Payload p in graphs)
        {
            HnswBuilder g = p.Graph!;
            int m = g.MaxConnections;
            int recordInts = (2 * m) + 1;
            int perBlock = VectorFiles.RecordsPerBlock(recordInts * 4);
            long level0Offset = file.Position;
            var blocks = new BlockWriter(file, perBlock * recordInts * 4, cancellationToken);
            var record = new int[recordInts];
            for (int node = 0; node < g.Count; node++)
            {
                if ((node & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Array.Clear(record);
                ReadOnlySpan<int> links = g.Neighbors(node, 0);
                record[0] = links.Length;
                links.CopyTo(record.AsSpan(1));
                blocks.Append(MemoryMarshal.AsBytes(record.AsSpan()));
            }

            blocks.Finish();
            long crcOffset = file.Position;
            byte[] table = VectorFiles.CrcTable(blocks.Crcs);
            file.Write(table, cancellationToken);
            var upper = new ByteBuilder(1024);
            int upperCount = 0;
            for (int node = 0; node < g.Count; node++)
            {
                if (g.Level(node) > 0)
                {
                    upperCount++;
                }
            }

            upper.WriteVarUInt((ulong)upperCount);
            int previous = -1;
            for (int node = 0; node < g.Count; node++)
            {
                int level = g.Level(node);
                if (level == 0)
                {
                    continue;
                }

                upper.WriteVarUInt((ulong)(node - previous - 1));
                previous = node;
                upper.WriteByte((byte)level);
                for (int l = 1; l <= level; l++)
                {
                    ReadOnlySpan<int> links = g.Neighbors(node, l);
                    upper.WriteVarUInt((ulong)links.Length);
                    foreach (int target in links)
                    {
                        upper.WriteVarUInt((ulong)target);
                    }
                }
            }

            long upperOffset = file.Position;
            file.Write(upper.Span, cancellationToken);
            byte[] name = Encoding.UTF8.GetBytes(p.Def.Name);
            directory.WriteVarUInt((ulong)name.Length);
            directory.Write(name);
            directory.WriteVarUInt((ulong)g.Count);
            directory.WriteVarUInt((ulong)m);
            directory.WriteVarUInt((ulong)g.MaxLevel);
            directory.WriteVarUInt((ulong)g.EntryPoint);
            directory.WriteVarUInt((ulong)level0Offset);
            directory.WriteVarUInt((ulong)perBlock);
            directory.WriteVarUInt((ulong)crcOffset);
            directory.WriteUInt64(Crc32C.Compute(table));
            directory.WriteVarUInt((ulong)upperOffset);
            directory.WriteVarUInt((ulong)upper.Length);
            directory.WriteUInt64(Crc32C.Compute(upper.Span));
        }

        FinishWithDirectory(file, directory, cancellationToken);
    }

    private static void FinishWithDirectory(SegmentFileBuilder file, ByteBuilder directory, CancellationToken cancellationToken)
    {
        long directoryAt = file.Position;
        file.Write(directory.Span, cancellationToken);
        var trailer = new ByteBuilder(16);
        trailer.WriteUInt64((ulong)directoryAt);
        trailer.WriteUInt32(Crc32C.Compute(directory.Span));
        file.Write(trailer.Span, cancellationToken);
        file.Complete(cancellationToken);
    }

    /// <inheritdoc/>
    public ISegmentPartReader? OpenReader(SegmentReaderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IRangeReader? vectors = context.OpenPart(SegmentParts.Vectors, out _, cancellationToken);
        if (vectors is null)
        {
            return null;
        }

        IRangeReader? graph = null;
        try
        {
            graph = context.OpenPart(SegmentParts.Graph, out _, cancellationToken);
            return VectorPartReader.Open(vectors, graph, context.Schema, context.Info.DocCount, context.Limits, cancellationToken);
        }
        catch
        {
            vectors.Dispose();
            graph?.Dispose();
            throw;
        }
    }
}
