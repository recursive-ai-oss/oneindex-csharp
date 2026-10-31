using System.Text.Json;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

public class SchemaTests
{
    private static Schema Full() => new SchemaBuilder()
        .Id("id")
        .Add(new FieldDefinition("title", FieldType.Text) { Boost = 2.5f, Column = false, IndexOptions = IndexOptions.Offsets })
        .Add(new FieldDefinition("body", FieldType.Text) { Analyzer = "whitespace-lowercase", Stored = false })
        .Add(new FieldDefinition("tags", FieldType.Keyword) { Facet = true, Column = true, Multi = true })
        .Integral("year")
        .Fractional("price")
        .Date("modified")
        .Bool("active")
        .GeoPoint("where")
        .Bytes("blob")
        .Vector("embedding", 384)
        .Json("meta")
        .Build();

    [Fact]
    public void SerializationRoundTripsEveryFieldOption()
    {
        Schema schema = Full();
        byte[] bytes = schema.Serialize();
        Schema back = Schema.Deserialize(bytes);
        Assert.True(schema.Equals(back));
        Assert.Equal(schema.Fields.Count, back.Fields.Count);
        for (int i = 0; i < schema.Fields.Count; i++)
        {
            FieldDefinition a = schema.Fields[i];
            FieldDefinition b = back.Fields[i];
            Assert.Equal((a.Name, a.Type, a.Indexed, a.Stored, a.Column, a.Facet, a.Multi, a.Boost, a.Analyzer, a.IndexOptions, a.Dimensions), (b.Name, b.Type, b.Indexed, b.Stored, b.Column, b.Facet, b.Multi, b.Boost, b.Analyzer, b.IndexOptions, b.Dimensions));
        }

        Assert.Equal("id", back.IdField);
        Assert.Equal(2.5f, back.Fields[back.GetFieldId("title")].Boost);
        Assert.Equal(-1, back.GetFieldId("nope"));
        Assert.True(back.TryGetField("embedding", out FieldDefinition? v) && v.Dimensions == 384);
    }

    [Fact]
    public void SerializationIsDeterministicAndOrderIsPreserved()
    {
        Assert.Equal(Full().Serialize(), Full().Serialize());
        Schema ab = new SchemaBuilder().Keyword("a").Keyword("b").Build();
        Schema ba = new SchemaBuilder().Keyword("b").Keyword("a").Build();
        Assert.NotEqual(ab.Serialize(), ba.Serialize());
        Assert.False(ab.Equals(ba));
        Assert.Equal(0, ab.GetFieldId("a"));
        Assert.Equal(Schema.Empty.Serialize(), new SchemaBuilder().Build().Serialize());
    }

    [Fact]
    public void ABuilderRefusesInvalidSchemasNamingTheField()
    {
        void Bad(string fieldName, Func<SchemaBuilder, SchemaBuilder> make)
        {
            var e = Assert.Throws<ArgumentException>(() => make(new SchemaBuilder()).Build());
            Assert.Contains(fieldName, e.Message, StringComparison.Ordinal);
        }

        Bad("dup", b => b.Keyword("dup").Text("dup"));
        Bad("vec", b => b.Vector("vec", 0));
        Bad("vec", b => b.Vector("vec", 1_000_000));
        Bad("t", b => b.Add(new FieldDefinition("t", FieldType.Text) { Column = true }));
        Bad("k", b => b.Add(new FieldDefinition("k", FieldType.Keyword) { Analyzer = "x" }));
        Bad("n", b => b.Add(new FieldDefinition("n", FieldType.Integral) { Dimensions = 3 }));
        Bad("w", b => b.Add(new FieldDefinition("w", FieldType.Keyword) { Boost = 0 }));
        Bad("w", b => b.Add(new FieldDefinition("w", FieldType.Keyword) { Boost = float.NaN }));
        Bad("g", b => b.Add(new FieldDefinition("g", FieldType.GeoPoint) { Facet = true }));
        Bad("x", b => b.Add(new FieldDefinition("x", FieldType.Bytes) { Indexed = true }));
        Bad("t", b => b.Add(new FieldDefinition("t", FieldType.Text) { IndexOptions = IndexOptions.None }));
        Bad("bad\u0001name", b => b.Keyword("bad\u0001name"));
        Assert.Throws<ArgumentException>(() => new SchemaBuilder().Keyword("").Build());
        Assert.Throws<InvalidOperationException>(() => new SchemaBuilder().Id("a").Id("b"));
        Assert.Throws<ArgumentException>(() => new SchemaBuilder(new IndexLimits { MaxFields = 1 }).Keyword("a").Keyword("b").Build());
    }

    [Fact]
    public void MalformedSchemaBytesRaiseTheTypedException()
    {
        byte[] bytes = Full().Serialize();
        for (int n = 0; n < bytes.Length; n++)
        {
            Assert.ThrowsAny<CorruptIndexException>(() => Schema.Deserialize(bytes.AsSpan(0, n)));
        }

        byte[] wrongMagic = (byte[])bytes.Clone();
        wrongMagic[0] ^= 1;
        Assert.Throws<CorruptIndexException>(() => Schema.Deserialize(wrongMagic));
        byte[] newer = (byte[])bytes.Clone();
        newer[4] = 9;
        Assert.Throws<UnsupportedFormatException>(() => Schema.Deserialize(newer));
        Assert.Throws<CorruptIndexException>(() => Schema.Deserialize(bytes.AsSpan(), new IndexLimits { MaxSchemaBytes = 10 }));
        Assert.False(Schema.TryDeserialize([1, 2, 3], null, out Schema? s, out string? error));
        Assert.Null(s);
        Assert.NotNull(error);

        // Random single-byte damage never produces anything but the typed exception or a valid schema.
        var rng = new Random(11);
        for (int i = 0; i < 2000; i++)
        {
            byte[] damaged = (byte[])bytes.Clone();
            damaged[rng.Next(damaged.Length)] = (byte)rng.Next(256);
            if (Schema.TryDeserialize(damaged, null, out Schema? ok, out _))
            {
                Assert.Equal(damaged, ok.Serialize());
            }
        }
    }

    [Fact]
    public void DocumentsAreValidatedAgainstTheSchema()
    {
        Schema schema = Full();
        using JsonDocument json = JsonDocument.Parse("""{"a":1}""");
        var good = new Document()
            .Add("id", "x1")
            .Add("title", "Hello")
            .Add("tags", "a")
            .Add("tags", "b")
            .Add("year", 2026L)
            .Add("price", 9.5)
            .Add("modified", new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero))
            .Add("active", true)
            .Add("where", FieldValue.FromGeoPoint(48.85, 2.35))
            .Add("blob", FieldValue.FromBytes([1, 2]))
            .Add("embedding", new float[384])
            .Add("meta", FieldValue.FromJson(json.RootElement));
        schema.Validate(good);
        Assert.Equal("x1", good.Get("id").AsText());
        Assert.Equal(0, (int)good.Get("missing").Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), good.Get("modified").AsInt64());
        Assert.Equal((48.85, 2.35), good.Get("where").AsGeoPoint());
        Assert.Throws<InvalidOperationException>(() => good.Get("id").AsInt64());

        void Bad(string field, Document d) => Assert.Contains(field, Assert.Throws<ArgumentException>(() => schema.Validate(d)).Message, StringComparison.Ordinal);
        Bad("unknown", new Document().Add("id", "x").Add("unknown", "v"));
        Bad("year", new Document().Add("id", "x").Add("year", "not a number"));
        Bad("title", new Document().Add("id", "x").Add("title", "a").Add("title", "b"));
        Bad("embedding", new Document().Add("id", "x").Add("embedding", new float[3]));
        Bad("id", new Document().Add("title", "no id"));
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldValue.FromGeoPoint(91, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldValue.FromGeoPoint(0, double.NaN));
    }

    [Fact]
    public void DoublesSortLikeTheirEncodings()
    {
        double[] values = [double.NegativeInfinity, -1e300, -2.5, -1, -double.Epsilon, -0.0, 0.0, double.Epsilon, 1, 2.5, 1e300, double.PositiveInfinity];
        for (int i = 0; i + 1 < values.Length; i++)
        {
            Assert.True(OneIndex.Search.SortableBits.Encode(values[i]) < OneIndex.Search.SortableBits.Encode(values[i + 1]), $"{values[i]} < {values[i + 1]}");
            Assert.Equal(BitConverter.DoubleToInt64Bits(values[i]), BitConverter.DoubleToInt64Bits(OneIndex.Search.SortableBits.Decode(OneIndex.Search.SortableBits.Encode(values[i]))));
        }
    }
}
