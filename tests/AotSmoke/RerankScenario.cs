using OneIndex.Models;
using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>A document whose body the reranker reads.</summary>
public sealed class Passage
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Title { get; set; } = "";

    [FullText(Rerank = true)]
    public string Body { get; set; } = "";
}

/// <summary>The cross-encoder reranker and the rerank stage with the packed model, under NativeAOT.</summary>
internal static class RerankScenario
{
    public static void Run()
    {
        MiniLmReranker reranker;
        try
        {
            reranker = MiniLmReranker.Load();
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("rerank: skipped (this build has no packed reranker weights)");
            return;
        }

        using (reranker)
        {
            float[] scores = reranker.Score("how do plants make food",
            [
                "Plants make their food using light energy in a process called photosynthesis.",
                "The recipe calls for two cups of flour, a pinch of salt and three eggs.",
            ]);
            if (!(scores[0] > scores[1]))
            {
                throw new InvalidOperationException($"the reranker scored the unrelated passage higher: {scores[0]} {scores[1]}");
            }

            using SearchIndex<Passage> index = SearchIndex.InMemory<Passage>(null, null, reranker);
            index.Upsert(new Passage { Id = "p1", Title = "Cooking", Body = "The recipe calls for two cups of flour, a pinch of salt and food." });
            index.Upsert(new Passage { Id = "p2", Title = "Biology", Body = "Plants make their food using light energy in a process called photosynthesis." });
            index.Commit();
            SearchHit<Passage>[] hits = [.. index.Search("plants OR food").Rerank(query: "how do plants make food")];
            if (hits.Length != 2 || hits[0].Id != "p2" || hits[0].FirstStageScore is null || Math.Abs(hits[0].Score - scores[0]) > 1e-3)
            {
                throw new InvalidOperationException("the rerank stage did not order the passages by the reranker: " + string.Join(',', hits.Select(static h => h.Id)));
            }

            Console.WriteLine($"rerank: scored pairs and reranked a search ({reranker.Inner.KernelPath})");
        }
    }
}
