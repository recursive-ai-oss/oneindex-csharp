namespace OneIndex.Search;

/// <summary>
/// Feeds every match to several collectors in one pass. It asks for the strictest scoring any of them needs: it can skip
/// non-competitive documents only when all of them accept that, so a collector that needs every match (facets, aggregations, an exact
/// count) makes the pass visit every match for all.
/// </summary>
public sealed class MultiCollector : Collector
{
    private readonly Collector[] _collectors;

    /// <summary>Combines collectors.</summary>
    /// <param name="collectors">At least one.</param>
    /// <exception cref="ArgumentException">There are none, or one is null.</exception>
    public MultiCollector(IEnumerable<Collector> collectors)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        _collectors = [.. collectors];
        if (_collectors.Length == 0 || Array.IndexOf(_collectors, null) >= 0)
        {
            throw new ArgumentException("Give at least one collector, none of them null.", nameof(collectors));
        }

        // A collector that accepts skipped documents hands the scorer hints, which would starve the others: it may only run alone.
        ScoreMode = _collectors.Length == 1 ? _collectors[0].ScoreMode : ScoreMode.CompleteNoScores;
        foreach (Collector c in _collectors)
        {
            if (c.ScoreMode == ScoreMode.TopScores && _collectors.Length > 1)
            {
                throw new ArgumentException("A collector that accepts skipped documents cannot share a pass; make it count exactly.", nameof(collectors));
            }

            if (c.ScoreMode == ScoreMode.Complete && _collectors.Length > 1)
            {
                ScoreMode = ScoreMode.Complete;
            }
        }
    }

    /// <inheritdoc/>
    public override ScoreMode ScoreMode { get; }

    /// <inheritdoc/>
    public override LeafCollector GetLeafCollector(LeafContext leaf)
    {
        var leaves = new LeafCollector[_collectors.Length];
        for (int i = 0; i < leaves.Length; i++)
        {
            leaves[i] = _collectors[i].GetLeafCollector(leaf);
        }

        return new Leaf(leaves);
    }

    private sealed class Leaf(LeafCollector[] leaves) : LeafCollector
    {
        public override void SetScorer(Scorer scorer)
        {
            foreach (LeafCollector l in leaves)
            {
                l.SetScorer(scorer);
            }
        }

        public override void Collect(int docId)
        {
            for (int i = 0; i < leaves.Length; i++)
            {
                leaves[i].Collect(docId);
            }
        }
    }
}
