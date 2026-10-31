using System.Buffers;
using System.Buffers.Binary;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Fuzz;

/// <summary>
/// Feeds arbitrary bytes in place of a segment's columns, points or stored-source file, then reads everything the damaged file
/// offers. Only <see cref="CorruptIndexException"/> may come out; a crash, a hang or an allocation past the limits is a finding. The
/// first input byte picks whether the header and footer are repaired (odd), so that mutations reach the structure parsers instead of
/// stopping at the checksum.
/// </summary>
internal static class DocValuesTarget
{
    private static readonly IndexLimits Limits = new() { MaxResidentFileBytes = 1 << 22, MaxDocsPerSegment = 100_000, MaxFields = 64, MaxStringBytes = 512 };
    private static readonly Lazy<(MemoryStorage Storage, byte[] Columns, byte[] Points, byte[] Stored, int DocCount)> Good = new(() => DocValuesFixture.BuildBaseline(7, 200));

    public static void RunColumns(ReadOnlySpan<byte> data) => Run(data, 0);

    public static void RunPoints(ReadOnlySpan<byte> data) => Run(data, 1);

    public static void RunStored(ReadOnlySpan<byte> data) => Run(data, 2);

    public static void WriteSeeds(string directory, int which)
    {
        var good = Good.Value;
        File.WriteAllBytes(Path.Combine(directory, "valid"), [1, .. which switch { 0 => good.Columns, 1 => good.Points, _ => good.Stored }]);
    }

    /// <summary>Opens and reads one damaged file; <see cref="CorruptIndexException"/> is the expected outcome for damaged input.</summary>
    public static void Exercise(byte[] file, int which)
    {
        var good = Good.Value;
        var storage = new MemoryStorage();
        using (IStorageWriter w = storage.Create("s.part"))
        {
            w.Write(file);
        }

        try
        {
            switch (which)
            {
                case 0:
                    ReadColumns(ColumnPartReader.Parse("s.part", file, good.DocCount, Limits), good.DocCount);
                    break;
                case 1:
                    ReadPoints(PointPartReader.Open(storage.OpenRead("s.part"), good.DocCount, Limits, default));
                    break;
                default:
                    ReadStored(SegmentStoredFields.Open(storage.OpenRead("s.part"), good.DocCount, Limits, default), good.DocCount);
                    break;
            }
        }
        catch (CorruptIndexException)
        {
            // Expected for damaged input.
        }
    }

    /// <summary>Runs one input: the first byte selects frame repair, the rest is the file.</summary>
    public static void Run(ReadOnlySpan<byte> data, int which)
    {
        if (data.IsEmpty)
        {
            return;
        }

        byte[] file = data[1..].ToArray();
        if ((data[0] & 1) != 0)
        {
            Repair(file, which);
        }

        Exercise(file, which);
    }

    private static void ReadColumns(ColumnPartReader part, int maxDoc)
    {
        long[] buffer = new long[64];
        foreach (string name in part.Names.ToList())
        {
            var column = (SegmentColumn)part.GetColumn(name)!;
            _ = (column.MinRaw, column.MaxRaw, column.DocsWithValue, column.TermCount, column.IsMultiValued);
            for (int d = 0; d < maxDoc; d++)
            {
                _ = column.HasValue(d);
                int n = column.ValueCount(d);
                _ = column.FirstValueIndex(d);
                for (int i = 0; i < Math.Min(n, 8); i++)
                {
                    switch (column.Type)
                    {
                        case FieldType.Keyword:
                            _ = column.GetKeyword(d, i);
                            _ = column.GetOrdinal(d, i);
                            break;
                        case FieldType.Fractional:
                            _ = column.GetFloat64(d, i);
                            break;
                        case FieldType.GeoPoint:
                            _ = column.GetGeoPoint(d, i);
                            break;
                        default:
                            _ = column.GetInt64(d, i);
                            break;
                    }
                }
            }

            long total = column.TotalValues;
            for (long at = 0; at < total; at += 3000)
            {
                column.ReadValues((int)at, buffer.AsSpan(0, (int)Math.Min(buffer.Length, total - at)));
            }

            if (column.TermCount > 0)
            {
                _ = column.FindOrdinal(column.GetTerm(column.TermCount / 2));
                _ = column.FindOrdinal("zzz"u8);
            }
        }
    }

    private static readonly (long Low, long High)[] Ranges = [(long.MinValue, long.MaxValue), (-5, 5), (0, 1), (1_700_000_000_000, 1_700_000_050_000), (long.MinValue, 0), (0, long.MaxValue)];

    private static void ReadPoints(PointPartReader part)
    {
        using (part)
        {
            var leaf = new long[PointFormat.LeafEntries];
            var docs = new int[PointFormat.LeafEntries];
            foreach (string name in part.Names.ToList())
            {
                var points = (SegmentPoints)part.GetPoints(name)!;
                foreach ((long low, long high) in Ranges)
                {
                    DocIdSetIterator it = points.GetRange(low, high);
                    int previous = -1;
                    for (int doc = it.NextDoc(); doc != DocIdSetIterator.NoMoreDocs; doc = it.NextDoc())
                    {
                        if (doc <= previous)
                        {
                            throw new InvalidOperationException("point iterator went backwards");
                        }

                        previous = doc;
                    }

                    it = points.GetRange(low, high);
                    for (int doc = it.Advance(3); doc != DocIdSetIterator.NoMoreDocs; doc = it.Advance(doc + 7))
                    {
                    }
                }

                for (int l = 0; l < points.LeafCount; l++)
                {
                    _ = points.ReadLeaf(l, leaf, docs);
                }
            }
        }
    }

    private static void ReadStored(SegmentStoredFields stored, int maxDoc)
    {
        using (stored)
        {
            var buffer = new ArrayBufferWriter<byte>();
            for (int d = 0; d < maxDoc; d++)
            {
                _ = stored.GetDocument(d);
                buffer.Clear();
                _ = stored.TryGetSource(d, buffer);
            }
        }
    }

    private static void Repair(byte[] file, int which)
    {
        if (file.Length < IndexFile.HeaderLength + IndexFile.FooterLength)
        {
            return;
        }

        uint magic = which switch { 0 => ColumnSegmentCodec.Magic, 1 => PointSegmentCodec.Magic, _ => StoredSegmentCodec.Magic };
        BinaryPrimitives.WriteUInt32LittleEndian(file, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 1);
        Span<byte> footer = file.AsSpan(file.Length - IndexFile.FooterLength);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, IndexFile.FooterMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], IndexFile.AlgorithmCrc32C);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], Crc32C.Compute(file.AsSpan(0, file.Length - 4)));
    }
}
