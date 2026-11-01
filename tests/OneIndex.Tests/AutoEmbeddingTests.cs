using System.Security.Cryptography;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.Tests;

/// <summary>A deterministic bag-of-words embedder that counts the texts it is given.</summary>
public sealed class CountingEmbedder(string modelId = "bow-16", bool failing = false) : IEmbedder
{
    public const int Dim = 16;
    private int _texts;
    private int _calls;

    public int Texts => Volatile.Read(ref _texts);

    public int Calls => Volatile.Read(ref _calls);

    public bool Failing { get; set; } = failing;

    public string ModelId => modelId;

    public int Dimension => Dim;

    public bool IsNormalized => false;

    /// <summary>Each word adds weight to a few of 16 slots chosen by its hash: texts that share words are near each other.</summary>
    public static float[] VectorOf(string text)
    {
        var v = new float[Dim];
        foreach (string w in text.ToLowerInvariant().Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(w));
            v[h[0] % Dim] += 1f;
            v[h[1] % Dim] += 0.5f;
            v[h[2] % Dim] -= 0.25f;
        }

        if (v.All(static x => x == 0f))
        {
            v[0] = 1f;
        }

        return v;
    }

    public void Embed(IReadOnlyList<string> texts, EmbeddingRole role, Span<float> destination, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        if (Failing)
        {
            throw new InvalidOperationException("the model is not available");
        }

        Interlocked.Add(ref _texts, texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            VectorOf(texts[i]).CopyTo(destination.Slice(i * Dim, Dim));
        }
    }

    public ValueTask EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<float> destination, CancellationToken cancellationToken = default)
    {
        Embed(texts, role, destination.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }
}

public sealed class EmbNote
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";

    [Filterable, Facet, Keyword]
    public string Group { get; set; } = "";

    [Vector(From = nameof(Body), Dimensions = CountingEmbedder.Dim, FlatThreshold = 3)]
    public float[]? Embedding { get; set; }
}

// The same stored fields plus one more, so that opening an index written as EmbNote is a schema evolution.
public sealed class EmbNoteV2
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";

    [Filterable, Facet, Keyword]
    public string Group { get; set; } = "";

    [Filterable]
    public long Rank { get; set; }

    [Vector(From = nameof(Body), Dimensions = CountingEmbedder.Dim, FlatThreshold = 3)]
    public float[]? Embedding { get; set; }
}

public sealed class AutoEmbeddingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "auto-embed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static EmbNote[] Notes(int n) => [.. Enumerable.Range(0, n).Select(i => new EmbNote { Id = "n" + i, Body = $"note number {i} about topic{i % 7}", Group = "g" + (i % 3) })];

    [Fact]
    public void UpsertEmbedsTheSourceTextAndStoresTheVector()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = SearchIndex.InMemory<EmbNote>(embedder);
        index.Upsert(Notes(10));
        Assert.Equal(10, embedder.Texts);
        Assert.Equal(1, embedder.Calls); // one batch
        index.Commit();
        SearchHit<EmbNote> hit = index.Search().Where(x => x.Id == "n4").ToList().Single();
        Assert.Equal(CountingEmbedder.VectorOf("note number 4 about topic4"), hit.Document.Embedding);
    }

    [Fact]
    public void AVectorTheApplicationSuppliesIsKeptAndNotEmbedded()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = SearchIndex.InMemory<EmbNote>(embedder);
        float[] mine = Enumerable.Range(0, CountingEmbedder.Dim).Select(static i => i + 1f).ToArray();
        index.Upsert(new EmbNote { Id = "a", Body = "x", Embedding = mine });
        index.Commit();
        Assert.Equal(0, embedder.Texts);
        Assert.Equal(mine, index.Search().ToList().Single().Document.Embedding);
    }

    [Fact]
    public void ReupsertingUnchangedTextDoesNotCallTheEmbedderBeforeOrAfterCommitOrReopen()
    {
        var first = new CountingEmbedder();
        EmbNote[] notes = Notes(40);
        using (SearchIndex<EmbNote> index = SearchIndex.Open<EmbNote>(_dir, first))
        {
            index.Upsert(notes);
            Assert.Equal(40, first.Texts);
            index.Upsert(notes); // pending documents: the in-memory map
            Assert.Equal(40, first.Texts);
            index.Commit();
            index.Upsert(notes); // committed documents: the stored source
            index.Commit();
            Assert.Equal(40, first.Texts);
        }

        var second = new CountingEmbedder();
        using (SearchIndex<EmbNote> index = SearchIndex.Open<EmbNote>(_dir, second))
        {
            index.Upsert(notes);
            index.Commit();
            Assert.Equal(0, second.Texts); // a new process: only the stored source remembers
            notes[3].Body += " changed";
            index.Upsert(notes);
            Assert.Equal(1, second.Texts); // exactly the changed document
        }
    }

    [Fact]
    public void IdenticalTextsOfDifferentDocumentsAreEmbeddedOnce()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = SearchIndex.InMemory<EmbNote>(embedder);
        index.Upsert(Enumerable.Range(0, 20).Select(static i => new EmbNote { Id = "d" + i, Body = "same words" }));
        Assert.Equal(1, embedder.Texts);
    }

    [Fact]
    public void CommitsAndMergesNeverCallTheEmbedder()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = SearchIndex.Open<EmbNote>(_dir, embedder);
        for (int round = 0; round < 25; round++)
        {
            index.Upsert(new EmbNote { Id = "r" + round, Body = "round " + round });
            index.Commit(); // many small segments: the merge policy merges them
        }

        Assert.Equal(25, embedder.Texts);
        Assert.Equal(25, index.Count);
        Assert.Equal(25, index.SearchVector(x => x.Embedding, CountingEmbedder.VectorOf("round 3"), 25).Take(25).ToList().Count);
    }

    [Fact]
    public void SchemaEvolutionBackfillKeepsTheStoredVectorsWithoutEmbedding()
    {
        var first = new CountingEmbedder();
        using (SearchIndex<EmbNote> index = SearchIndex.Open<EmbNote>(_dir, first))
        {
            index.Upsert(Notes(30));
            index.Commit();
        }

        var second = new CountingEmbedder();
        using SearchIndex<EmbNoteV2> evolved = SearchIndex.Open<EmbNoteV2>(_dir, second);
        evolved.CompleteSchemaEvolution();
        Assert.Equal(0, second.Texts);
        Assert.Equal(30, evolved.Count);
        SearchHit<EmbNoteV2> nearest = evolved.SearchVector(x => x.Embedding, CountingEmbedder.VectorOf("note number 9 about topic2"), 1).ToList().Single();
        Assert.Equal("n9", nearest.Id);
    }

    [Fact]
    public void AModelChangeEmbedsAgainOnUpsertAndThroughReembedStale()
    {
        EmbNote[] notes = Notes(25);
        using (SearchIndex<EmbNote> index = SearchIndex.Open<EmbNote>(_dir, new CountingEmbedder("model-a")))
        {
            index.Upsert(notes);
            index.Commit();
        }

        var b = new CountingEmbedder("model-b");
        using (SearchIndex<EmbNote> index = SearchIndex.Open<EmbNote>(_dir, b))
        {
            index.Upsert(notes.Take(5));
            Assert.Equal(5, b.Texts); // the identity is part of the cache key
            index.Commit();
            Assert.Equal(20, index.ReembedStale()); // the rest, once
            Assert.Equal(25, b.Texts);
            index.Commit();
            Assert.Equal(0, index.ReembedStale());
            Assert.Equal(25, b.Texts);
            Assert.Equal(25, index.Count);
        }
    }

    [Fact]
    public void AFailingEmbedderThrowsClearlyAndLeavesTheIndexUnchanged()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = SearchIndex.InMemory<EmbNote>(embedder);
        index.Upsert(Notes(3));
        index.Commit();
        embedder.Failing = true;
        EmbeddingException e = Assert.Throws<EmbeddingException>(() => index.Upsert(new EmbNote { Id = "bad", Body = "never indexed" }));
        Assert.Contains("bow-16", e.Message);
        Assert.IsType<InvalidOperationException>(e.InnerException);
        Assert.Throws<EmbeddingException>(() => index.Upsert(Notes(3).Select(static n => { n.Id += "x"; n.Body += "!"; return n; })));
        index.Commit();
        Assert.Equal(3, index.Count);
        embedder.Failing = false;
        index.Upsert(new EmbNote { Id = "bad", Body = "indexed now" });
        index.Commit();
        Assert.Equal(4, index.Count);
    }

    [Fact]
    public async Task UpsertAsyncUsesTheAsynchronousEmbedderPath()
    {
        var embedder = new CountingEmbedder();
        using SearchIndex<EmbNote> index = SearchIndex.InMemory<EmbNote>(embedder);
        await index.UpsertAsync(new EmbNote { Id = "a", Body = "async note" });
        Assert.Equal(1, embedder.Texts);
        index.Commit();
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void TheEmbedderDimensionMustMatchTheMember()
    {
        Assert.Throws<InvalidOperationException>(() => SearchIndex.InMemory<Mismatch>(new CountingEmbedder()));
    }

    public sealed class Mismatch
    {
        [SearchId]
        public string Id { get; set; } = "";

        [FullText]
        public string Body { get; set; } = "";

        [Vector(From = nameof(Body), Dimensions = 5)]
        public float[]? V { get; set; }
    }

    [Fact]
    public void WithoutAnEmbedderDocumentsAreIndexedAsTextAndSearchStaysTextOnly()
    {
        using SearchIndex<EmbNote> index = SearchIndex.InMemory<EmbNote>(null);
        index.Upsert(Notes(5));
        index.Commit();
        Assert.Null(index.Embedder);
        Assert.Equal(5, index.Search("note").Take(10).ToList().Count);
        Assert.IsNotType<HybridQuery>(index.Search("note").ToQuery());
        Assert.Throws<InvalidOperationException>(() => index.Search("note").Hybrid(0.5).ToQuery());
    }

    [Fact]
    public void TheDefaultEmbedderIsUsedWhenNoneIsPassed()
    {
        IEmbedder? before = SearchEmbedders.Default;
        var embedder = new CountingEmbedder();
        try
        {
            SearchEmbedders.Default = embedder;
            using SearchIndex<EmbNote> index = SearchIndex.InMemory<EmbNote>();
            Assert.Same(embedder, index.Embedder);
        }
        finally
        {
            SearchEmbedders.Default = before;
        }
    }
}
