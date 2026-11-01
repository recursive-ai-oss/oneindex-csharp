using System.Security.Cryptography;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.Tests;

/// <summary>A deterministic sparse encoder: each lower-cased word gets a weight from its hash; queries weigh every word 1. Counts texts.</summary>
public sealed class CountingSparseEncoder(string modelId = "words-1", bool failing = false) : ISparseEncoder
{
    private int _texts;
    private int _calls;

    public int Texts => Volatile.Read(ref _texts);

    public int Calls => Volatile.Read(ref _calls);

    public bool Failing { get; set; } = failing;

    public string ModelId => modelId;

    public static SparseVector Of(string text, EmbeddingRole role)
    {
        var entries = new List<KeyValuePair<string, float>>();
        foreach (string w in text.ToLowerInvariant().Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            float weight = role == EmbeddingRole.Query ? 1f : 0.5f + (SHA256.HashData(Encoding.UTF8.GetBytes(w))[0] / 64f);
            entries.Add(new(w, weight));
        }

        return new SparseVector(entries);
    }

    public void Encode(IReadOnlyList<string> texts, EmbeddingRole role, Span<SparseVector> destination, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        if (Failing)
        {
            throw new InvalidOperationException("the model is not available");
        }

        Interlocked.Add(ref _texts, texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            destination[i] = Of(texts[i], role);
        }
    }

    public ValueTask EncodeAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<SparseVector> destination, CancellationToken cancellationToken = default)
    {
        Encode(texts, role, destination.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }
}

public sealed class SparseNote
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";

    [Sparse(From = nameof(Body))]
    public SparseVector? Expansion { get; set; }
}

public sealed class SparseAndDenseNote
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";

    [Vector(From = nameof(Body), Dimensions = CountingEmbedder.Dim, FlatThreshold = 3)]
    public float[]? Embedding { get; set; }

    [Sparse(From = nameof(Body))]
    public SparseVector? Expansion { get; set; }
}

public sealed class AutoSparseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "auto-sparse-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SparseNote[] Notes(int n) => [.. Enumerable.Range(0, n).Select(i => new SparseNote { Id = "n" + i, Body = $"note number {i} about topic{i % 7}" })];

    [Fact]
    public void UpsertEncodesTheSourceTextAndStoresTheVector()
    {
        var encoder = new CountingSparseEncoder();
        using SearchIndex<SparseNote> index = SearchIndex.InMemory<SparseNote>(null, encoder);
        index.Upsert(Notes(10));
        Assert.Equal(10, encoder.Texts);
        Assert.Equal(1, encoder.Calls);
        index.Commit();
        SearchHit<SparseNote> hit = index.Search().Where(x => x.Id == "n4").ToList().Single();
        Assert.Equal(CountingSparseEncoder.Of("note number 4 about topic4", EmbeddingRole.Document), hit.Document.Expansion);
    }

    [Fact]
    public void AVectorTheApplicationSuppliesIsKeptAndNotEncoded()
    {
        var encoder = new CountingSparseEncoder();
        using SearchIndex<SparseNote> index = SearchIndex.InMemory<SparseNote>(null, encoder);
        var mine = new SparseVector([new("x", 2f)]);
        index.Upsert(new SparseNote { Id = "a", Body = "y", Expansion = mine });
        index.Commit();
        Assert.Equal(0, encoder.Texts);
        Assert.Equal(mine, index.Search().ToList().Single().Document.Expansion);
    }

    [Fact]
    public void ReupsertingUnchangedTextDoesNotCallTheEncoderBeforeOrAfterCommitOrReopen()
    {
        var first = new CountingSparseEncoder();
        SparseNote[] notes = Notes(40);
        using (SearchIndex<SparseNote> index = SearchIndex.Open<SparseNote>(_dir, null, first))
        {
            index.Upsert(notes);
            Assert.Equal(40, first.Texts);
            index.Upsert(notes);
            Assert.Equal(40, first.Texts);
            index.Commit();
            index.Upsert(notes);
            index.Commit();
            Assert.Equal(40, first.Texts);
        }

        var second = new CountingSparseEncoder();
        using (SearchIndex<SparseNote> index = SearchIndex.Open<SparseNote>(_dir, null, second))
        {
            index.Upsert(notes);
            index.Commit();
            Assert.Equal(0, second.Texts);
            notes[3].Body += " changed";
            index.Upsert(notes);
            Assert.Equal(1, second.Texts);
        }
    }

    [Fact]
    public void CommitsAndMergesNeverCallTheEncoderAndAModelChangeEncodesAgain()
    {
        var encoder = new CountingSparseEncoder();
        using (SearchIndex<SparseNote> index = SearchIndex.Open<SparseNote>(_dir, null, encoder))
        {
            for (int round = 0; round < 25; round++)
            {
                index.Upsert(new SparseNote { Id = "r" + round, Body = "round " + round });
                index.Commit(); // many small segments: the merge policy merges them
            }

            Assert.Equal(25, encoder.Texts);
            Assert.Equal(25, index.Count);
        }

        var other = new CountingSparseEncoder("words-2");
        using (SearchIndex<SparseNote> index = SearchIndex.Open<SparseNote>(_dir, null, other))
        {
            Assert.Equal(25, index.ReembedStale());
            Assert.Equal(25, other.Texts);
            index.Commit();
            Assert.Equal(0, index.ReembedStale());
            Assert.Equal(25, other.Texts);
        }
    }

    [Fact]
    public void AFailingEncoderLeavesTheIndexUnchanged()
    {
        var encoder = new CountingSparseEncoder(failing: true);
        using SearchIndex<SparseNote> index = SearchIndex.InMemory<SparseNote>(null, encoder);
        Assert.Throws<EmbeddingException>(() => index.Upsert(Notes(3)));
        index.Commit();
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void SparseSearchRanksByDotProductAndHybridAddsTheArm()
    {
        var encoder = new CountingSparseEncoder();
        var embedder = new CountingEmbedder();
        using SearchIndex<SparseAndDenseNote> index = SearchIndex.InMemory<SparseAndDenseNote>(embedder, encoder);
        string[] bodies = ["red apple pie", "green apple tart", "blue ocean wave", "red ocean sunset", "apple orchard in autumn", "deep blue sea"];
        index.Upsert(bodies.Select((b, i) => new SparseAndDenseNote { Id = "d" + i, Body = b }));
        index.Commit();

        // The query role weighs each word 1, so the score of a document is the sum of its quantized weights for the query words.
        double Expected(string body, string query)
        {
            SparseVector d = CountingSparseEncoder.Of(body, EmbeddingRole.Document);
            return CountingSparseEncoder.Of(query, EmbeddingRole.Query).Tokens.Sum(t =>
            {
                int j = Array.BinarySearch(d.Tokens, t, StringComparer.Ordinal);
                return j < 0 ? 0 : SparseVector.Quantize(d.Weights[j]) / (double)SparseVector.Scale;
            });
        }

        List<SearchHit<SparseAndDenseNote>> hits = index.Search("red apple").Hybrid(0).Sparse().Take(10).ToList();
        Assert.NotEmpty(hits);
        // text + sparse fused: the top hit matches both words.
        Assert.Equal("d0", hits[0].Document.Id);

        // With the vector list as well, the fused set is the union of three lists and explain names the sparse rank.
        List<SearchHit<SparseAndDenseNote>> three = index.Search("red apple").Sparse().Explain().Take(10).ToList();
        Assert.Contains("sparse rank", three[0].Explanation!.Description);
        Assert.True(three.Count >= hits.Count);

        // The sparse list alone (a SparseQuery through the low level) matches the dot product exactly.
        List<SearchHit<SparseAndDenseNote>> direct = index.SearchBy(new SparseQuery("Expansion", CountingSparseEncoder.Of("red apple", EmbeddingRole.Query))).Take(6).ToList();
        Assert.NotEmpty(direct);
        foreach (SearchHit<SparseAndDenseNote> h in direct)
        {
            Assert.Equal(Expected(h.Document.Body, "red apple"), h.Score, 1e-3);
        }
    }

    private static readonly string[] TwoBodies = ["red apple pie", "blue sea"];

    [Fact]
    public void QueryJsonCarriesTheSparseArmAndTheToolSchemaDescribesIt()
    {
        Schema schema = SearchMappings.Require<SparseAndDenseNote>().Schema;
        string node = """{"v":1,"query":{"type":"hybrid","text":{"type":"term","field":"Body","value":"red"},"sparse":{"type":"sparse","field":"Expansion","weights":{"red":1}},"sparseWeight":2,"fusion":"linear"}}""";
        Queries.QueryRequest r = Queries.QueryJson.Read(node, schema);
        var h = Assert.IsType<HybridQuery>(r.Query);
        Assert.Null(h.Vector);
        Assert.IsType<SparseQuery>(h.Sparse);
        Assert.Equal(2.0, h.SparseWeight);
        string written = Queries.QueryJson.Write(r);
        Assert.Equal(r, Queries.QueryJson.Read(written, schema));

        Queries.QueryRequest req = Queries.QueryJson.Read("""{"v":1,"q":"red apple","hybrid":{"sparse":true,"sparseWeight":0.5,"semanticRatio":0}}""", schema);
        Assert.True(req.Hybrid!.Sparse);
        Assert.Equal(req, Queries.QueryJson.Read(Queries.QueryJson.Write(req), schema));
        Assert.Throws<Queries.QueryParseException>(() => Queries.QueryJson.Read("""{"v":1,"query":{"type":"hybrid","text":{"type":"matchAll"}}}""", schema));

        string tool = SearchTool.SchemaFor<SparseAndDenseNote>();
        Assert.Contains("\"sparse0\"", tool);
        Assert.Contains("sparseWeight", tool);

        var encoder = new CountingSparseEncoder();
        using SearchIndex<SparseAndDenseNote> index = SearchIndex.InMemory<SparseAndDenseNote>(new CountingEmbedder(), encoder);
        index.Upsert(TwoBodies.Select((b, i) => new SparseAndDenseNote { Id = "d" + i, Body = b }));
        index.Commit();
        List<SearchHit<SparseAndDenseNote>> hits = index.Parse("""{"v":1,"q":"red apple","hybrid":{"sparse":true,"semanticRatio":0}}""").ToList();
        Assert.Equal("d0", hits[0].Document.Id);
    }
}
