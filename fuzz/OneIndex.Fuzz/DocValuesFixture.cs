using System.Text;
using System.Text.Json;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Fuzz;

/// <summary>
/// A schema that uses every column, point and stored-source feature, a seeded generator of documents that hit the edge cases (extreme
/// integers, NaN, signed zero, empty and very long keywords, multi-byte text, missing and repeated values), and helpers to compare
/// values exactly. Shared by the tests and the fuzz targets.
/// </summary>
internal static class DocValuesFixture
{
    public static readonly string[] ColumnFieldNames = ["id", "n", "f", "when", "flag", "cat", "tags", "nums", "loc"];

    public static readonly string[] PointFieldNames = ["f", "flag", "n", "nums", "when"];

    public static Schema CreateSchema() => new SchemaBuilder()
        .Id("id")
        .Integral("n")
        .Fractional("f")
        .Date("when")
        .Bool("flag")
        .Add(new FieldDefinition("cat", FieldType.Keyword) { Column = true })
        .Add(new FieldDefinition("tags", FieldType.Keyword) { Column = true, Facet = true, Multi = true })
        .Add(new FieldDefinition("nums", FieldType.Integral) { Column = true, Multi = true })
        .Add(new FieldDefinition("loc", FieldType.GeoPoint) { Column = true })
        .Add(new FieldDefinition("raw", FieldType.Integral) { Column = true, Indexed = false })
        .Text("body")
        .Add(new FieldDefinition("secret", FieldType.Text) { Stored = false })
        .Add(new FieldDefinition("blob", FieldType.Bytes))
        .Vector("vec", 3)
        .Sparse("sp")
        .Json("meta")
        .Build();

    /// <summary>Generates <paramref name="count"/> documents with identifiers <c>d&lt;first&gt;</c> and up.</summary>
    public static List<Document> Generate(int seed, int count, int first = 0)
    {
        var rng = new Random(seed);
        var docs = new List<Document>(count);
        for (int i = 0; i < count; i++)
        {
            var d = new Document().Add("id", "d" + (first + i));
            if (rng.Next(10) > 0)
            {
                d.Add("n", RandomLong(rng));
            }

            if (rng.Next(10) > 0)
            {
                d.Add("f", RandomDouble(rng));
            }

            if (rng.Next(10) > 0)
            {
                d.Add("when", DateTimeOffset.FromUnixTimeMilliseconds(rng.Next(4) == 0 ? rng.NextInt64(-62_135_596_800_000, 253_402_300_799_999) : 1_700_000_000_000 + rng.Next(100_000_000)));
            }

            if (rng.Next(10) > 1)
            {
                d.Add("flag", rng.Next(2) == 0);
            }

            if (rng.Next(10) > 2)
            {
                d.Add("cat", RandomKeyword(rng));
            }

            for (int t = rng.Next(4) == 0 ? 0 : rng.Next(5); t > 0; t--)
            {
                d.Add("tags", RandomKeyword(rng));
            }

            for (int t = rng.Next(3) == 0 ? 0 : rng.Next(6); t > 0; t--)
            {
                d.Add("nums", RandomLong(rng));
            }

            if (rng.Next(10) > 3)
            {
                d.Add("loc", FieldValue.FromGeoPoint((rng.NextDouble() * 180) - 90, (rng.NextDouble() * 360) - 180));
            }

            if (rng.Next(3) == 0)
            {
                d.Add("raw", rng.NextInt64());
            }

            d.Add("body", RandomText(rng));
            if (rng.Next(2) == 0)
            {
                d.Add("secret", "do not store " + i);
            }

            if (rng.Next(4) == 0)
            {
                var bytes = new byte[rng.Next(40)];
                rng.NextBytes(bytes);
                d.Add("blob", FieldValue.FromBytes(bytes));
            }

            if (rng.Next(4) == 0)
            {
                d.Add("vec", [(float)rng.NextDouble(), (float)rng.NextDouble(), -0f]);
            }

            if (rng.Next(3) == 0)
            {
                var entries = new List<KeyValuePair<string, float>>();
                for (int t = rng.Next(1, 12); t > 0; t--)
                {
                    entries.Add(new("w" + rng.Next(200), (float)(rng.NextDouble() * (rng.Next(20) == 0 ? 1000 : 4))));
                }

                d.Add("sp", new SparseVector(entries));
            }

            if (rng.Next(4) == 0)
            {
                d.Add("meta", FieldValue.FromJson(JsonDocument.Parse($"{{\"a\":{rng.Next()},\"b\":[1,2,{{\"c\":null}}],\"s\":\"x\\u00e9\"}}").RootElement));
            }

            docs.Add(d);
        }

        return docs;
    }

    public static long RandomLong(Random rng) => rng.Next(9) switch
    {
        0 => rng.Next(-5, 6),
        1 => long.MinValue,
        2 => long.MaxValue,
        3 => rng.NextInt64() ,
        4 => rng.Next(0, 50) * 1_000_000_007L,
        5 => 1_700_000_000_000 + rng.Next(1000),
        6 => rng.Next(100) == 0 ? long.MinValue + rng.Next(3) : rng.Next(-1000, 1000),
        7 => (long)rng.Next(8) << 40,
        _ => rng.NextInt64(-100_000, 100_000),
    };

    public static double RandomDouble(Random rng) => rng.Next(14) switch
    {
        0 => 0.0,
        1 => -0.0,
        2 => double.NaN,
        3 => BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001L + rng.Next(5)),
        4 => double.PositiveInfinity,
        5 => double.NegativeInfinity,
        6 => double.Epsilon,
        7 => double.MaxValue,
        8 => -double.MaxValue,
        9 => rng.Next(-10, 10),
        10 => rng.NextDouble(),
        _ => (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-300, 300) / 10.0),
    };

    public static string RandomKeyword(Random rng) => rng.Next(12) switch
    {
        0 => string.Empty,
        1 => new string('k', 5_000 + rng.Next(5_000)),
        2 => "caf\u00e9 \u6F22\u5B57 \U0001F600",
        3 => "z" + rng.Next(1_000_000),
        _ => "tag" + rng.Next(30),
    };

    public static string RandomText(Random rng)
    {
        var sb = new StringBuilder();
        int words = rng.Next(0, 30);
        for (int i = 0; i < words; i++)
        {
            sb.Append(rng.Next(20) == 0 ? "\u00fcber\U0001F600" : "w" + rng.Next(200)).Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>Whether two values are identical, comparing floating-point numbers bit for bit and arrays by content.</summary>
    public static bool SameValue(FieldValue a, FieldValue b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        return a.Kind switch
        {
            FieldValueKind.Text => a.AsText() == b.AsText(),
            FieldValueKind.Integral or FieldValueKind.Date => a.AsInt64() == b.AsInt64(),
            FieldValueKind.Fractional => BitConverter.DoubleToInt64Bits(a.AsFloat64()) == BitConverter.DoubleToInt64Bits(b.AsFloat64()),
            FieldValueKind.Bool => a.AsBool() == b.AsBool(),
            FieldValueKind.GeoPoint => a.AsGeoPoint().Latitude.Equals(b.AsGeoPoint().Latitude) && a.AsGeoPoint().Longitude.Equals(b.AsGeoPoint().Longitude),
            FieldValueKind.Bytes => a.AsBytes().Span.SequenceEqual(b.AsBytes().Span),
            FieldValueKind.Vector => a.AsVector().Span.Length == b.AsVector().Span.Length
                && a.AsVector().Span.ToArray().Zip(b.AsVector().Span.ToArray(), (x, y) => BitConverter.SingleToUInt32Bits(x) == BitConverter.SingleToUInt32Bits(y)).All(x => x),
            FieldValueKind.Sparse => a.AsSparse().Equals(b.AsSparse()),
            FieldValueKind.Json => a.AsJson().GetRawText() == b.AsJson().GetRawText(),
            _ => false,
        };
    }

    /// <summary>The stored form of a document: the fields the schema stores, in document order.</summary>
    public static List<DocumentField> StoredView(Schema schema, Document d)
        => [.. d.Fields.Where(f => schema.TryGetField(f.Name, out FieldDefinition? def) && def.Stored)];

    public static bool SameStored(Schema schema, Document expected, Document actual)
    {
        List<DocumentField> e = StoredView(schema, expected);
        if (e.Count != actual.Fields.Count)
        {
            return false;
        }

        for (int i = 0; i < e.Count; i++)
        {
            if (e[i].Name != actual.Fields[i].Name || !SameValue(e[i].Value, actual.Fields[i].Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes the documents into a memory storage with small segments and returns the part files of the first segment.</summary>
    public static (MemoryStorage Storage, byte[] Columns, byte[] Points, byte[] Stored, int DocCount) BuildBaseline(int seed, int count)
    {
        var storage = new MemoryStorage();
        var options = new IndexWriterOptions { MaxBufferedDocuments = count + 1, AutoMerge = false };
        using (IndexWriter writer = IndexWriter.Open(storage, CreateSchema(), options))
        {
            foreach (Document d in Generate(seed, count))
            {
                writer.Upsert(d);
            }

            writer.Commit();
        }

        using IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        SegmentInfo info = snapshot.GetSegment(0).Info;
        return (storage, Read(storage, info, SegmentParts.Columns), Read(storage, info, SegmentParts.Points), Read(storage, info, SegmentParts.Stored), info.DocCount);
    }

    private static byte[] Read(MemoryStorage storage, SegmentInfo info, string part)
    {
        using IRangeReader r = storage.OpenRead(info.FindPart(part)!.FileName);
        var bytes = new byte[r.Length];
        r.ReadExactly(0, bytes);
        return bytes;
    }
}
