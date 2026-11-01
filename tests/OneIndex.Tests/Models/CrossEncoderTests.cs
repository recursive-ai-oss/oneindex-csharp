using System.Text;
using OneIndex.Models;
using OneIndex.Models.Tokenization;
using OneIndex.Vectors;

namespace OneIndex.Tests.Models;

public class PairEncodingTests
{
    private static readonly string[] Tokens = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "a", "b", "hello", "world"];

    private static readonly WordPieceTokenizer Tokenizer =
        new(WordPieceVocabulary.Parse(Encoding.UTF8.GetBytes(string.Join('\n', Tokens) + "\n")), null, UnicodeDecomposer.Instance);

    private static (int[] Ids, int[] Segments) Pair(string first, string second, int max)
    {
        var ids = new int[max];
        var segments = new int[max];
        int n = Tokenizer.EncodePair(first, second, ids, segments);
        return (ids[..n], segments[..n]);
    }

    [Fact]
    public void APairIsClassFirstSeparatorSecondSeparatorWithSegmentIds()
    {
        (int[] ids, int[] segments) = Pair("hello", "world a", 32);
        Assert.Equal([2, 7, 3, 8, 5, 3], ids);
        Assert.Equal([0, 0, 0, 1, 1, 1], segments);
    }

    [Fact]
    public void EmptyTextsLeaveOnlyTheSpecialTokens()
    {
        var (e1, g1) = Pair("", "", 16);
        Assert.Equal([2, 3, 3], e1);
        Assert.Equal([0, 0, 1], g1);
        var (e2, g2) = Pair("hello", "", 16);
        Assert.Equal([2, 7, 3, 3], e2);
        Assert.Equal([0, 0, 0, 1], g2);
    }

    [Fact]
    public void TheLongerTextIsCutFirstAndTheShorterKeepsUpToHalfTheRoom()
    {
        // Room for 8 tokens besides the 3 special ones in 11 ids.
        string a3 = "a a a";
        string b20 = string.Join(' ', Enumerable.Repeat("b", 20));
        (int[] ids, int[] segments) = Pair(a3, b20, 11);
        Assert.Equal(11, ids.Length);
        Assert.Equal(3, segments.Count(static s => s == 0) - 2);
        Assert.Equal(5, segments.Count(static s => s == 1) - 1);

        // Both long: 4 and 4. The longer one is the first when it is strictly longer; when equal, the second counts as the longer and keeps the odd token.
        string a20 = string.Join(' ', Enumerable.Repeat("a", 20));
        (int[] _, int[] s2) = Pair(a20, b20, 12); // 9 tokens of room: 4 + 5
        Assert.Equal((4, 5), (s2.Count(static s => s == 0) - 2, s2.Count(static s => s == 1) - 1));
        (int[] _, int[] s3) = Pair(a20 + " a", b20, 12);
        Assert.Equal((5, 4), (s3.Count(static s => s == 0) - 2, s3.Count(static s => s == 1) - 1));
        (int[] _, int[] s4) = Pair(a20, b20 + " b", 12);
        Assert.Equal((4, 5), (s4.Count(static s => s == 0) - 2, s4.Count(static s => s == 1) - 1));
    }

    [Fact]
    public void TheTextThatFitsIsNotCutAndTheEndsAreAlwaysSeparators()
    {
        (int[] ids, int[] segments) = Pair("a b", "a b a b", 9);
        Assert.Equal(9, ids.Length);
        Assert.Equal(2, ids[0]);
        Assert.Equal(3, ids[^1]);
        Assert.Equal(1, segments[^1]);
        Assert.Throws<ArgumentException>(() => Tokenizer.EncodePair("a", "b", new int[2], new int[2]));
        Assert.Throws<ArgumentException>(() => Tokenizer.EncodePair("a", "b", new int[8], new int[4]));
        var (e3, g3) = Pair("a", "b", 3); // no room for text at all
        Assert.Equal([2, 3, 3], e3);
        Assert.Equal([0, 0, 1], g3);
    }
}

/// <summary>
/// The default reranker end to end. Runs against the weights packed in the assembly or, if the build has none, the model files in
/// ONEINDEX_RERANKER_DIR (model.int8.onemodel and vocab.txt, written by tools/models/gate.sh setup-reranker); skipped when neither exists.
/// </summary>
public class MiniLmRerankerTests : IDisposable
{
    private readonly MiniLmReranker? _reranker;

    public MiniLmRerankerTests()
    {
        try
        {
            _reranker = MiniLmReranker.Load();
        }
        catch (FileNotFoundException)
        {
            string? dir = Environment.GetEnvironmentVariable("ONEINDEX_RERANKER_DIR");
            if (dir is not null && File.Exists(Path.Combine(dir, "model.int8.onemodel")))
            {
                _reranker = MiniLmReranker.Open(Path.Combine(dir, "model.int8.onemodel"), Path.Combine(dir, "vocab.txt"), "int8");
            }
        }
    }

    public void Dispose()
    {
        _reranker?.Dispose();
        GC.SuppressFinalize(this);
    }

    private MiniLmReranker Model
    {
        get
        {
            if (_reranker is null)
            {
                Assert.Skip("no packed weights and ONEINDEX_RERANKER_DIR is not set");
            }

            return _reranker!;
        }
    }

    private static readonly string[] Passages =
    [
        "Photosynthesis is the process by which plants turn sunlight, water and carbon dioxide into sugar and oxygen.",
        "The stock market closed higher on Tuesday after a strong earnings report from several large banks.",
        "Plants make their food using light energy in a process called photosynthesis, which happens in the leaves.",
        "The recipe calls for two cups of flour, a pinch of salt and three eggs.",
    ];

    [Fact]
    public void RelevantPassagesOutscoreIrrelevantOnes()
    {
        float[] s = Model.Score("how do plants make food", Passages);
        Assert.True(s[0] > s[1] && s[0] > s[3]);
        Assert.True(s[2] > s[1] && s[2] > s[3]);
        Assert.Equal("ms-marco-MiniLM-L6-v2/int8", Model.ModelId);
    }

    [Fact]
    public async Task ScoresDoNotDependOnBatchingAndTheAsyncTwinAgrees()
    {
        const string query = "how do plants make food";
        float[] together = Model.Score(query, Passages);
        for (int i = 0; i < Passages.Length; i++)
        {
            Assert.Equal(together[i], Model.Score(query, [Passages[i]])[0], 3);
        }

        float[] viaAsync = await Model.ScoreAsync(query, Passages);
        Assert.Equal(together, viaAsync);
        Assert.Empty(Model.Score(query, []));
        Assert.Throws<ArgumentException>(() => Model.Score(query, Passages, new float[3]));
        Assert.Throws<ArgumentNullException>(() => Model.Score(query, [null!]));
        Assert.Throws<OperationCanceledException>(() => Model.Score(query, Passages, new float[4], new CancellationToken(true)));
        // A pair longer than the model's limit is cut, not refused.
        Assert.True(float.IsFinite(Model.Score(query, [string.Join(' ', Enumerable.Repeat("photosynthesis", 2000))])[0]));
    }

    [Fact]
    public void TheSearchStageRerankesWithTheRealModel()
    {
        using SearchIndex<PlainDoc> index = SearchIndex.InMemory<PlainDoc>(null, Model);
        PlainDoc[] docs =
        [
            new() { Id = "photo", Title = "Photosynthesis", Body = Passages[0] },
            new() { Id = "market", Title = "Markets", Body = Passages[1] + " food" },
            new() { Id = "leaves", Title = "Leaves", Body = Passages[2] },
        ];
        foreach (PlainDoc d in docs)
        {
            index.Upsert(d);
        }

        index.Commit();
        const string query = "how do plants make food";
        float[] scores = Model.Score(query, [.. docs.Select(static d => d.Title + " " + d.Body)]);
        string[] want = [.. docs.Select((d, i) => (d.Id, Score: scores[i])).OrderByDescending(static x => x.Score).Select(static x => x.Id)];
        List<SearchHit<PlainDoc>> hits = index.Search("plants OR food").Rerank(query: query).ToList();
        Assert.Equal(want, hits.Select(static h => h.Id));
        Assert.Equal("market", want[^1]);
        Assert.Equal(scores.Max(), hits[0].Score);
    }
}
