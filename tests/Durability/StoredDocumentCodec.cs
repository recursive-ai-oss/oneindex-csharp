using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Durability;

/// <summary>
/// A small codec for tests: it stores every document whole in the <c>stored</c> part and can look up keyword values by scanning. It
/// follows the codec contract of the index (deterministic bytes, validated reads) so that the lifecycle can be tested before the
/// real codecs exist.
/// </summary>
public sealed class StoredDocumentCodec : ISegmentCodec
{
    private static readonly uint Magic = IndexFile.Magic("TSTD");

    /// <inheritdoc/>
    public string Name => "test-stored";

    /// <inheritdoc/>
    public IReadOnlyList<string> Parts { get; } = [SegmentParts.Stored];

    /// <inheritdoc/>
    public void Flush(SegmentFlushContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        var blobs = new List<byte[]>(context.Documents.Count);
        foreach (Document d in context.Documents)
        {
            blobs.Add(Serialize(d));
        }

        Write(sink, blobs, cancellationToken);
    }

    /// <inheritdoc/>
    public void Merge(SegmentMergeContext context, SegmentFileSink sink, CancellationToken cancellationToken)
    {
        var blobs = new List<byte[]>(context.DocumentCount);
        foreach (MergeSource source in context.Sources)
        {
            if (source.Reader is not Reader reader)
            {
                continue;
            }

            for (int d = 0; d < source.MaxDoc; d++)
            {
                if (source.DocMap[d] >= 0)
                {
                    blobs.Add(reader.Raw(d).ToArray());
                }
            }
        }

        Write(sink, blobs, cancellationToken);
    }

    /// <inheritdoc/>
    public ISegmentPartReader? OpenReader(SegmentReaderContext context, CancellationToken cancellationToken)
    {
        IRangeReader? file = context.OpenPart(SegmentParts.Stored, out _, cancellationToken);
        if (file is null)
        {
            return null;
        }

        using (file)
        {
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(0, bytes, cancellationToken);
            return new Reader(file.Name, bytes, context.Info.DocCount);
        }
    }

    private static void Write(SegmentFileSink sink, List<byte[]> blobs, CancellationToken cancellationToken)
    {
        using SegmentFileBuilder file = sink.Create(SegmentParts.Stored, "TSTD", 1, cancellationToken);
        var head = new byte[4 + ((blobs.Count + 1) * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)blobs.Count);
        uint at = 0;
        for (int i = 0; i < blobs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4 + (i * 4)), at);
            at += (uint)blobs[i].Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4 + (blobs.Count * 4)), at);
        file.Write(head, cancellationToken);
        foreach (byte[] b in blobs)
        {
            file.Write(b, cancellationToken);
        }

        file.Complete(cancellationToken);
    }

    internal static byte[] Serialize(Document d)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, new UTF8Encoding(false, true), true);
        w.Write(d.Fields.Count);
        foreach (DocumentField f in d.Fields)
        {
            w.Write(f.Name);
            w.Write((byte)f.Value.Kind);
            switch (f.Value.Kind)
            {
                case FieldValueKind.Text:
                    w.Write(f.Value.AsText());
                    break;
                case FieldValueKind.Integral:
                case FieldValueKind.Date:
                    w.Write(f.Value.AsInt64());
                    break;
                case FieldValueKind.Fractional:
                    w.Write(f.Value.AsFloat64());
                    break;
                case FieldValueKind.Bool:
                    w.Write(f.Value.AsBool());
                    break;
                case FieldValueKind.GeoPoint:
                    (double lat, double lon) = f.Value.AsGeoPoint();
                    w.Write(lat);
                    w.Write(lon);
                    break;
                case FieldValueKind.Bytes:
                    w.Write(f.Value.AsBytes().Length);
                    w.Write(f.Value.AsBytes().Span);
                    break;
                case FieldValueKind.Vector:
                    w.Write(f.Value.AsVector().Length);
                    foreach (float x in f.Value.AsVector().Span)
                    {
                        w.Write(x);
                    }

                    break;
                default:
                    throw new NotSupportedException($"The test codec does not store {f.Value.Kind} values.");
            }
        }

        w.Flush();
        return ms.ToArray();
    }

    private static Document Deserialize(ReadOnlySpan<byte> raw)
    {
        using var ms = new MemoryStream(raw.ToArray());
        using var r = new BinaryReader(ms, new UTF8Encoding(false, true));
        var d = new Document();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            string name = r.ReadString();
            var kind = (FieldValueKind)r.ReadByte();
            switch (kind)
            {
                case FieldValueKind.Text:
                    d.Add(name, r.ReadString());
                    break;
                case FieldValueKind.Integral:
                    d.Add(name, r.ReadInt64());
                    break;
                case FieldValueKind.Date:
                    d.Add(name, DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64()));
                    break;
                case FieldValueKind.Fractional:
                    d.Add(name, r.ReadDouble());
                    break;
                case FieldValueKind.Bool:
                    d.Add(name, r.ReadBoolean());
                    break;
                case FieldValueKind.GeoPoint:
                    d.Add(name, FieldValue.FromGeoPoint(r.ReadDouble(), r.ReadDouble()));
                    break;
                case FieldValueKind.Bytes:
                    d.Add(name, FieldValue.FromBytes(r.ReadBytes(r.ReadInt32())));
                    break;
                case FieldValueKind.Vector:
                    var v = new float[r.ReadInt32()];
                    for (int j = 0; j < v.Length; j++)
                    {
                        v[j] = r.ReadSingle();
                    }

                    d.Add(name, v);
                    break;
                default:
                    throw new InvalidDataException("unknown value kind");
            }
        }

        return d;
    }

    private sealed class Reader : ISegmentPartReader, IStoredFieldsReader, ITermDocLookup
    {
        private readonly string _fileName;
        private readonly byte[] _bytes;
        private readonly int _count;

        public Reader(string fileName, byte[] bytes, int expectedCount)
        {
            _fileName = fileName;
            _bytes = bytes;
            IndexFile.Validate(fileName, bytes, Magic, 1);
            _count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(IndexFile.HeaderLength));
            if (_count != expectedCount || _bytes.Length < IndexFile.HeaderLength + 4 + ((_count + 1L) * 4) + IndexFile.FooterLength)
            {
                throw new CorruptIndexException(fileName, "document count differs from the commit");
            }
        }

        public ReadOnlySpan<byte> Raw(int docId)
        {
            if ((uint)docId >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(docId));
            }

            int table = IndexFile.HeaderLength + 4;
            long dataStart = table + ((_count + 1L) * 4);
            uint from = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(table + (docId * 4)));
            uint to = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(table + ((docId + 1) * 4)));
            if (from > to || dataStart + to > _bytes.Length - IndexFile.FooterLength)
            {
                throw new CorruptIndexException(_fileName, "document offsets are out of range");
            }

            return _bytes.AsSpan((int)(dataStart + from), (int)(to - from));
        }

        public Document GetDocument(int docId) => Deserialize(Raw(docId));

        public bool TryGetSource(int docId, IBufferWriter<byte> destination) => false;

        public void CollectDocs(string field, ReadOnlySpan<byte> term, List<int> destination)
        {
            string value = Encoding.UTF8.GetString(term);
            for (int d = 0; d < _count; d++)
            {
                foreach (DocumentField f in Deserialize(Raw(d)).Fields)
                {
                    if (f.Name == field && f.Value.Kind == FieldValueKind.Text && f.Value.AsText() == value)
                    {
                        destination.Add(d);
                        break;
                    }
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
