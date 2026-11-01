using OneIndex.Models;
using OneIndex.Vectors;

namespace OneIndex.Tests.Models;

/// <summary>
/// The default embedder end to end. Runs against the weights packed in the assembly or, if the build has none, the model files in
/// ONEINDEX_MODEL_DIR (model.int8.onemodel and vocab.txt, written by tools/models/gate.sh); skipped when neither exists.
/// </summary>
public class BgeSmallEmbedderTests : IDisposable
{
    private readonly BgeSmallEmbedder? _embedder;

    public BgeSmallEmbedderTests()
    {
        try
        {
            _embedder = BgeSmallEmbedder.Load();
        }
        catch (FileNotFoundException)
        {
            string? dir = Environment.GetEnvironmentVariable("ONEINDEX_MODEL_DIR");
            if (dir is not null && File.Exists(Path.Combine(dir, "model.int8.onemodel")))
            {
                _embedder = BgeSmallEmbedder.Open(Path.Combine(dir, "model.int8.onemodel"), Path.Combine(dir, "vocab.txt"), "int8");
            }
        }
    }

    public void Dispose()
    {
        _embedder?.Dispose();
        GC.SuppressFinalize(this);
    }

    private BgeSmallEmbedder Embedder => _embedder ?? throw new Xunit.Sdk.XunitException("no model");

    private bool Skip()
    {
        if (_embedder is null)
        {
            Assert.Skip("no packed weights and ONEINDEX_MODEL_DIR is not set");
        }

        return false;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    [Fact]
    public void VectorsAreUnitLengthAndRelatedTextsAreCloser()
    {
        Skip();
        BgeSmallEmbedder e = Embedder;
        Assert.Equal(384, e.Dimension);
        Assert.True(e.IsNormalized);
        Assert.StartsWith("bge-small-en-v1.5/", e.ModelId, StringComparison.Ordinal);
        float[] query = e.Embed("how do plants make food from sunlight", EmbeddingRole.Query);
        float[] related = e.Embed("Photosynthesis lets plants turn light, water and carbon dioxide into sugar.");
        float[] unrelated = e.Embed("The central bank raised interest rates by a quarter point on Tuesday.");
        Assert.InRange(Dot(query, query), 0.999, 1.001);
        Assert.True(Dot(query, related) > Dot(query, unrelated) + 0.1);
    }

    [Fact]
    public void QueryAndDocumentRolesDiffer()
    {
        Skip();
        float[] asQuery = Embedder.Embed("solar panels", EmbeddingRole.Query);
        float[] asDocument = Embedder.Embed("solar panels", EmbeddingRole.Document);
        Assert.NotEqual(asQuery, asDocument);
        Assert.True(Dot(asQuery, asDocument) > 0.8);
    }

    [Fact]
    public void BatchesGiveTheSameVectorsAsSingleCalls()
    {
        Skip();
        string[] texts = ["short", "a somewhat longer sentence about gardens and the weather", "", string.Join(' ', Enumerable.Repeat("word", 700)), "\u4e2d\u6587 text"];
        float[] batch = Embedder.EmbedBatch(texts);
        for (int i = 0; i < texts.Length; i++)
        {
            float[] single = Embedder.Embed(texts[i]);
            Assert.InRange(Dot(single, batch.AsSpan(i * 384, 384)), 0.9999, 1.0001);
        }
    }

    [Fact]
    public async Task AsyncMatchesSyncAndCancellationIsObserved()
    {
        Skip();
        float[] sync = Embedder.Embed("asynchronous embedding");
        float[] async = await Embedder.EmbedAsync("asynchronous embedding");
        Assert.Equal(sync, async);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => Embedder.Embed(["x"], EmbeddingRole.Document, new float[384], cts.Token));
        Assert.Throws<ArgumentException>(() => Embedder.Embed(["x"], EmbeddingRole.Document, new float[10]));
        Embedder.Embed([], EmbeddingRole.Document, []);
    }
}
