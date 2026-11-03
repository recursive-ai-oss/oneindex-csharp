using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>A document with a token-vector property, the generated mapping for it and MaxSim over it.</summary>
public sealed class TokenPassage
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Text { get; set; } = "";

    [MultiVector(Dimensions = 3)]
    public float[][]? Tokens { get; set; }
}

internal static class MultiVectorScenario
{
    public static void Run()
    {
        using SearchIndex<TokenPassage> index = SearchIndex.InMemory<TokenPassage>();
        index.Upsert(new TokenPassage { Id = "a", Text = "alpha river", Tokens = [[1, 0, 0], [0, 1, 0]] });
        index.Upsert(new TokenPassage { Id = "b", Text = "beta river", Tokens = [[0, 0, 1]] });
        index.Commit();
        var best = MaxSim.Rerank(index.Search("river").Take(10).Execute(), p => p.Tokens, [[0f, 1f, 0f]], take: 2);
        if (best.Count != 2 || best[0].Hit.Id != "a" || Math.Abs(best[0].Score - 1) > 1e-6)
        {
            throw new InvalidOperationException("MaxSim over a [MultiVector] property gave the wrong order.");
        }

        if (index.Search("alpha").Take(1).Select(h => h.Document.Tokens!.Length).Single() != 2)
        {
            throw new InvalidOperationException("The token vectors did not round-trip.");
        }
    }
}
