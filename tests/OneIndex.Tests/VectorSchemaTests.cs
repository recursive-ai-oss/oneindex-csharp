using OneIndex.Index;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Tests;

public sealed class VectorSchemaTests
{
    private static Schema With(int dims = 8, VectorMetric metric = VectorMetric.Cosine, int m = 16, int efc = 100, int flat = 1000)
        => new SchemaBuilder().Id("id")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = dims, Metric = metric, HnswM = m, EfConstruction = efc, FlatThreshold = flat, Stored = true })
            .Build();

    [Fact]
    public void DefaultSettingsKeepTheFormatOneBytesAndCustomSettingsRoundTripInASection()
    {
        byte[] plain = With().Serialize();
        Assert.Equal(1, plain[4]); // the little-endian version word right after the 4-byte magic
        Assert.Equal(8, Schema.Deserialize(plain).Fields.Single(f => f.Name == "v").Dimensions);

        Schema custom = With(metric: VectorMetric.Euclidean, m: 24, efc: 150, flat: 5);
        byte[] bytes = custom.Serialize();
        Assert.Equal(2, bytes[4]);
        FieldDefinition v = Schema.Deserialize(bytes).Fields.Single(f => f.Name == "v");
        Assert.Equal((VectorMetric.Euclidean, 24, 150, 5), (v.Metric, v.HnswM, v.EfConstruction, v.FlatThreshold));
        Assert.Equal(bytes, Schema.Deserialize(bytes).Serialize());
    }

    [Fact]
    public void ChangingGraphSettingsRebuildsAndChangingDimensionsIsAnError()
    {
        var limits = new IndexLimits();
        Assert.False(SchemaDiff.Compare(With(), With(), limits).Rebuild);
        Assert.True(SchemaDiff.Compare(With(), With(m: 32), limits).Rebuild);
        Assert.True(SchemaDiff.Compare(With(), With(metric: VectorMetric.DotProduct), limits).Rebuild);
        SchemaEvolutionException e = Assert.Throws<SchemaEvolutionException>(() => { _ = SchemaDiff.Compare(With(), With(dims: 16), limits); });
        Assert.Contains("'v'", e.Message);
    }
}
