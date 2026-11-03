using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.Tests;

/// <summary>A deterministic token-vector encoder: every lower-cased word becomes one vector from its hash. Counts texts and calls.</summary>
public sealed class CountingMultiVectorEncoder(string modelId = "words-mv-1", bool failing = false, int dimension = CountingMultiVectorEncoder.Dim) : IMultiVectorEncoder
{
    public const int Dim = 8;

    private int _texts;
    private int _calls;

    public int Texts => Volatile.Read(ref _texts);

    public int Calls => Volatile.Read(ref _calls);

    public bool Failing { get; set; } = failing;

    public string ModelId => modelId;

    public int Dimension => dimension;

    public static float[][] Of(string text, int dimension = Dim, int salt = 0)
    {
        var rows = new List<float[]>();
        foreach (string w in text.ToLowerInvariant().Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            uint h = 2166136261u ^ (uint)salt;
            foreach (char c in w)
            {
                h = (h ^ c) * 16777619u;
            }

            var v = new float[dimension];
            for (int i = 0; i < dimension; i++)
            {
                h = (h ^ (uint)(i + 1)) * 16777619u;
                v[i] = (((h >> 8) % 2001) - 1000) / 1000f;
            }

            rows.Add(v);
        }

        return [.. rows];
    }

    public float[][] Encode(string text, EmbeddingRole role, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        if (Failing)
        {
            throw new InvalidOperationException("the model is not available");
        }

        Interlocked.Increment(ref _texts);
        return Of(text, dimension, modelId.EndsWith('2') ? 7 : 0);
    }
}

public sealed class LateNote
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";

    [Keyword, Sortable]
    public string Lang { get; set; } = "";

    [MultiVector(From = nameof(Body), Dimensions = CountingMultiVectorEncoder.Dim)]
    public float[][]? Tokens { get; set; }
}

/// <summary>The same document type before <c>From</c> was added: its indexes must still open.</summary>
public sealed class PlainLateNote
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Body { get; set; } = "";

    [Keyword]
    public string Lang { get; set; } = "";

    [MultiVector(Dimensions = CountingMultiVectorEncoder.Dim)]
    public float[][]? Tokens { get; set; }
}

public sealed class AutoMultiVectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "auto-mv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly string[] Vocabulary = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa"];

    private static LateNote[] Notes(int n, int seed = 1)
    {
        var rng = new Random(seed);
        return [.. Enumerable.Range(0, n).Select(i => new LateNote
        {
            Id = "n" + i,
            Lang = i % 2 == 0 ? "en" : "de",
            Body = string.Join(' ', Enumerable.Range(0, 3 + rng.Next(4)).Select(_ => Vocabulary[rng.Next(Vocabulary.Length)])),
        })];
    }

    private static SearchIndex<LateNote> OpenFile(string dir, IMultiVectorEncoder? encoder) => SearchIndex.Open<LateNote>(dir, null, null, null, encoder);

    [Fact]
    public void UpsertEncodesTheSourceTextAndStoresTheTokenVectors()
    {
        var encoder = new CountingMultiVectorEncoder();
        using SearchIndex<LateNote> index = SearchIndex.InMemory<LateNote>(null, null, null, encoder);
        index.Upsert(Notes(10));
        Assert.Equal(10, encoder.Texts);
        index.Commit();
        SearchHit<LateNote> hit = index.Search().Where(x => x.Id == "n4").ToList().Single();
        Assert.Equal(CountingMultiVectorEncoder.Of(hit.Document.Body), hit.Document.Tokens);
    }

    [Fact]
    public async Task TheAsyncTwinEncodesTheSame()
    {
        var encoder = new CountingMultiVectorEncoder();
        using SearchIndex<LateNote> index = SearchIndex.InMemory<LateNote>(null, null, null, encoder);
        await index.UpsertAsync(Notes(5));
        await index.UpsertAsync(new LateNote { Id = "solo", Body = "alpha bravo" });
        index.Commit();
        Assert.Equal(6, encoder.Texts);
        Assert.Equal(CountingMultiVectorEncoder.Of("alpha bravo"), index.Search().Where(x => x.Id == "solo").ToList().Single().Document.Tokens);
    }

    [Fact]
    public void TokensTheApplicationSuppliesAreKeptAndNotEncoded()
    {
        var encoder = new CountingMultiVectorEncoder();
        using SearchIndex<LateNote> index = SearchIndex.InMemory<LateNote>(null, null, null, encoder);
        float[][] mine = [[1, 0, 0, 0, 0, 0, 0, 0]];
        index.Upsert(new LateNote { Id = "a", Body = "y", Tokens = mine });
        index.Commit();
        Assert.Equal(0, encoder.Texts);
        Assert.Equal(mine, index.Search().ToList().Single().Document.Tokens);
    }

    [Fact]
    public void ReopeningAndReupsertingUnchangedTextsReusesTheStoredCache()
    {
        LateNote[] notes = Notes(20);
        var first = new CountingMultiVectorEncoder();
        using (SearchIndex<LateNote> index = OpenFile(_dir, first))
        {
            index.Upsert(notes);
            index.Commit();
        }

        Assert.Equal(20, first.Texts);
        var second = new CountingMultiVectorEncoder();
        using (SearchIndex<LateNote> index = OpenFile(_dir, second))
        {
            index.Upsert(notes);
            Assert.Equal(0, second.Texts);
            Assert.Equal(0, index.ReembedStale());
            LateNote changed = notes[3];
            changed.Body += " zulu";
            index.Upsert(changed);
            Assert.Equal(1, second.Texts);
            index.Commit();
            Assert.Equal(CountingMultiVectorEncoder.Of(changed.Body), index.Search().Where(x => x.Id == changed.Id).ToList().Single().Document.Tokens);
        }
    }

    [Fact]
    public void AChangedModelReencodesTheStaleEntriesOnly()
    {
        LateNote[] notes = Notes(12);
        using SearchIndex<LateNote> v1 = OpenFile(_dir, new CountingMultiVectorEncoder("words-mv-1"));
        v1.Upsert(notes);
        v1.Commit();
        v1.Dispose();

        var v2 = new CountingMultiVectorEncoder("words-mv-2");
        using SearchIndex<LateNote> index = OpenFile(_dir, v2);
        Assert.Equal(12, index.ReembedStale());
        Assert.Equal(12, v2.Texts);
        index.Commit();
        Assert.Equal(0, index.ReembedStale());
        SearchHit<LateNote> hit = index.Search().Where(x => x.Id == "n5").ToList().Single();
        Assert.Equal(CountingMultiVectorEncoder.Of(hit.Document.Body, CountingMultiVectorEncoder.Dim, salt: 7), hit.Document.Tokens);
        Assert.Equal(12, index.Search().Take(100).ToList().Count);
    }

    [Fact]
    public void MaxSimRanksTheDocumentsByLateInteraction()
    {
        LateNote[] notes = Notes(60, seed: 9);
        var encoder = new CountingMultiVectorEncoder();
        using SearchIndex<LateNote> index = SearchIndex.InMemory<LateNote>(null, null, null, encoder);
        index.Upsert(notes);
        index.Commit();
        foreach (string query in new[] { "alpha", "bravo charlie", "kilo lima mike november", "papa echo" })
        {
            double[] expected = [.. notes.Select(n => Vectors.MaxSim.Score(Flat(CountingMultiVectorEncoder.Of(query)), Flat(CountingMultiVectorEncoder.Of(n.Body)), CountingMultiVectorEncoder.Dim))];
            string[] want = [.. notes.Select((n, i) => (n.Id, Score: expected[i], i)).OrderByDescending(x => x.Score).ThenBy(x => x.i).Take(10).Select(x => x.Id)];

            // An exact scan: no search text, the first stage is every document, MaxSim gives the order.
            SearchResults<LateNote> got = index.Search().MaxSim(window: 100, query: query).Take(10).Execute();
            double[] gotScores = [.. got.Select(h => (double)h.Score)];
            Assert.Equal(want.Length, got.Count);
            for (int i = 0; i < want.Length; i++)
            {
                Assert.Equal(expected[Array.FindIndex(notes, n => n.Id == want[i])], gotScores[i], 4);
            }

            // Tied scores may come back in either order: compare ids only where the scores differ from their neighbours.
            Assert.Equal(want.Take(1), got.Take(1).Select(h => h.Id));
        }
    }

    [Fact]
    public void MaxSimAfterATextFirstStageReordersTheWindowAndKeepsTheFirstStageScore()
    {
        LateNote[] notes = Notes(40, seed: 3);
        using SearchIndex<LateNote> index = SearchIndex.InMemory<LateNote>(null, null, null, new CountingMultiVectorEncoder());
        index.Upsert(notes);
        index.Commit();
        List<SearchHit<LateNote>> plain = index.Search("alpha bravo").Take(30).ToList();
        List<SearchHit<LateNote>> late = index.Search("alpha bravo").MaxSim(x => x.Tokens, window: 30).Take(30).ToList();
        Assert.Equal(plain.Select(h => h.Id).Order(), late.Select(h => h.Id).Order());
        Assert.All(late, h => Assert.NotNull(h.FirstStageScore));
        for (int i = 1; i < late.Count; i++)
        {
            Assert.True(late[i - 1].Score >= late[i].Score);
        }

        // A filter composes with the stage.
        Assert.All(index.Search().Where(x => x.Lang == "de").MaxSim(query: "alpha").Take(5).ToList(), h => Assert.Equal("de", h.Document.Lang));
    }

    [Fact]
    public void MaxSimRefusesWhatItCannotDo()
    {
        using SearchIndex<LateNote> noEncoder = SearchIndex.InMemory<LateNote>(null, null, null, null);
        Assert.Throws<InvalidOperationException>(() => noEncoder.Search("alpha").MaxSim().ToList());
        using SearchIndex<LateNote> index = SearchIndex.InMemory<LateNote>(null, null, null, new CountingMultiVectorEncoder());
        Assert.Throws<InvalidOperationException>(() => index.Search().MaxSim().ToList()); // no text to encode
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("a").MaxSim(window: 0));
        Assert.Throws<ArgumentException>(() => index.Search("a").MaxSim(x => x.Body));
        Assert.Throws<NotSupportedException>(() => index.Search("a").MaxSim().OrderBy(x => x.Lang).ToList());
    }

    [Fact]
    public void AFailingEncoderLeavesTheIndexUnchanged()
    {
        var encoder = new CountingMultiVectorEncoder();
        using SearchIndex<LateNote> index = SearchIndex.InMemory<LateNote>(null, null, null, encoder);
        index.Upsert(new LateNote { Id = "a", Body = "alpha" });
        index.Commit();
        encoder.Failing = true;
        EmbeddingException e = Assert.Throws<EmbeddingException>(() => index.Upsert(new LateNote { Id = "b", Body = "bravo" }));
        Assert.Contains("words-mv-1", e.Message);
        index.Commit();
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void AnEncoderOfAnotherDimensionIsRefusedWhenTheIndexOpens()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => SearchIndex.InMemory<LateNote>(null, null, null, new CountingMultiVectorEncoder(dimension: 4)));
        Assert.Contains("Tokens", e.Message);
    }

    [Fact]
    public void AnIndexWrittenWithoutFromOpensWithItAndEncodesTheMissingEntries()
    {
        using (SearchIndex<PlainLateNote> plain = SearchIndex.Open<PlainLateNote>(_dir))
        {
            plain.Upsert(new PlainLateNote { Id = "a", Body = "alpha bravo", Tokens = [[1, 0, 0, 0, 0, 0, 0, 0]] });
            plain.Upsert(new PlainLateNote { Id = "b", Body = "charlie" });
            plain.Commit();
        }

        var encoder = new CountingMultiVectorEncoder();
        using SearchIndex<LateNote> index = OpenFile(_dir, encoder);
        Assert.Equal(2, index.Count);
        Assert.Equal(2, index.ReembedStale()); // neither entry has a cache key yet
        index.Commit();
        Assert.Equal(CountingMultiVectorEncoder.Of("charlie"), index.Search().Where(x => x.Id == "b").ToList().Single().Document.Tokens);
    }

    [Fact]
    public void TheDefaultEncoderIsUsedWhenNoneIsPassedAndNeverDiscovered()
    {
        Assert.Null(SearchMultiVectorEncoders.Default);
        var encoder = new CountingMultiVectorEncoder();
        SearchMultiVectorEncoders.Default = encoder;
        try
        {
            using SearchIndex<LateNote> index = SearchIndex.InMemory<LateNote>();
            Assert.Same(encoder, index.MultiVectorEncoder);
        }
        finally
        {
            SearchMultiVectorEncoders.Default = null;
        }
    }

    private static float[] Flat(float[][] rows) => [.. rows.SelectMany(static r => r)];
}
