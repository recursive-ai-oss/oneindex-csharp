namespace OneIndex.Vectors;

/// <summary>Exact nearest-neighbour search by comparing the query with every accepted vector. It serves small segments and is the ground truth the graph search is measured against.</summary>
internal static class FlatSearch
{
    /// <summary>Finds the <paramref name="k"/> nearest accepted vectors.</summary>
    /// <param name="values">The vectors.</param>
    /// <param name="kind">The distance.</param>
    /// <param name="query">The query, scaled as the vectors are.</param>
    /// <param name="k">How many to return.</param>
    /// <param name="accept">Which ordinals may be results; <see langword="null"/> accepts all.</param>
    /// <param name="output">Receives the results nearest first; at least <paramref name="k"/> long.</param>
    /// <returns>How many results were written.</returns>
    public static int Search(VectorValues values, DistanceKind kind, ReadOnlySpan<float> query, int k, IOrdinalFilter? accept, Span<Candidate> output)
    {
        if (k <= 0)
        {
            return 0;
        }

        CandidateHeap heap = HnswScratch.Current.Results;
        heap.Clear();
        int n = values.Count;
        for (int i = 0; i < n; i++)
        {
            if (accept is not null && !accept.Accepts(i))
            {
                continue;
            }

            float d = FloatNodeScorer.Distance(kind, query, values.Get(i));
            if (heap.Count < k)
            {
                heap.Push(new Candidate(d, i));
            }
            else if (d < heap.Peek.Key)
            {
                heap.Pop();
                heap.Push(new Candidate(d, i));
            }
        }

        return HnswSearch.Drain(heap, k, output);
    }

    /// <summary>
    /// Answers many queries in one pass over the vectors: each group of vectors is compared with every query before moving on, so a
    /// vector read from storage is used by all queries while it is still in cache. This is the shape of a matrix product, which is
    /// how ground truth for thousands of queries is computed.
    /// </summary>
    /// <param name="values">The vectors.</param>
    /// <param name="kind">The distance.</param>
    /// <param name="queries">The queries, scaled as the vectors are.</param>
    /// <param name="k">How many neighbours per query.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>For each query, its nearest ordinals, nearest first, and their distances.</returns>
    public static (int[] Ordinals, float[] Distances)[] SearchBatch(VectorValues values, DistanceKind kind, IReadOnlyList<float[]> queries, int k, CancellationToken cancellationToken)
    {
        const int Tile = 128;
        int n = values.Count;
        var heaps = new CandidateHeap[queries.Count];
        for (int q = 0; q < heaps.Length; q++)
        {
            heaps[q] = new CandidateHeap(farthestOnTop: true);
        }

        for (int start = 0; start < n; start += Tile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(n, start + Tile);
            for (int q = 0; q < heaps.Length; q++)
            {
                CandidateHeap heap = heaps[q];
                float[] query = queries[q];
                for (int i = start; i < end; i++)
                {
                    float d = FloatNodeScorer.Distance(kind, query, values.Get(i));
                    if (heap.Count < k)
                    {
                        heap.Push(new Candidate(d, i));
                    }
                    else if (d < heap.Peek.Key)
                    {
                        heap.Pop();
                        heap.Push(new Candidate(d, i));
                    }
                }
            }
        }

        var result = new (int[], float[])[heaps.Length];
        var buffer = new Candidate[k];
        for (int q = 0; q < heaps.Length; q++)
        {
            int count = HnswSearch.Drain(heaps[q], k, buffer);
            var ords = new int[count];
            var dist = new float[count];
            for (int i = 0; i < count; i++)
            {
                ords[i] = buffer[i].Id;
                dist[i] = buffer[i].Key;
            }

            result[q] = (ords, dist);
        }

        return result;
    }
}
