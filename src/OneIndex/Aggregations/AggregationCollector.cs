using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Aggregations;

/// <summary>
/// Computes aggregations over every document a query matches. Pass it to <see cref="IndexSearcher.Search(Query, Collector, CancellationToken)"/>,
/// then read <see cref="GetResults"/>. Counts and sums are exact; only cardinality and percentiles are estimates. It needs no scores
/// but does need every match, so it makes a search visit all of them: block-max WAND skipping does not apply (see docs/aggregations.md).
/// Collecting a document allocates nothing once a segment's state is built. One collector serves one search.
/// </summary>
public sealed class AggregationCollector : Collector
{
    /// <summary>The most buckets a histogram may return: 65,536.</summary>
    public const int MaxBuckets = 65_536;

    private readonly AggNode[] _roots;
    private Leaf? _last;

    /// <summary>Validates the requests against the schema and prepares the collector.</summary>
    /// <param name="schema">The schema of the searched index.</param>
    /// <param name="aggregations">The requests; names must be unique among siblings.</param>
    /// <exception cref="ArgumentException">A field is missing from the schema, keeps no column, or has a type the aggregation cannot read; names are duplicated; a setting is out of range.</exception>
    public AggregationCollector(Schema schema, IEnumerable<Aggregation> aggregations)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(aggregations);
        Names = [.. aggregations];
        _roots = AggNodes.CreateAll(Names, schema);
    }

    internal IReadOnlyList<Aggregation> Names { get; }

    /// <inheritdoc/>
    public override ScoreMode ScoreMode => ScoreMode.CompleteNoScores;

    /// <inheritdoc/>
    public override LeafCollector GetLeafCollector(LeafContext leaf)
    {
        _last?.Finish();
        _last = new Leaf(_roots, leaf);
        return _last;
    }

    /// <summary>The results of all requests, in request order. Call after the search; calling again returns the same values.</summary>
    /// <returns>The results.</returns>
    /// <exception cref="TooManyBucketsException">A histogram would exceed <see cref="MaxBuckets"/>.</exception>
    public AggregationResultSet GetResults()
    {
        _last?.Finish();
        return SubNodes.Build(_roots);
    }

    private sealed class Leaf : LeafCollector
    {
        private readonly AggLeaf[] _leaves;
        private bool _finished;

        public Leaf(AggNode[] roots, LeafContext leaf) => _leaves = SubNodes.Start(roots, leaf);

        public override void SetScorer(Scorer scorer)
        {
        }

        public override void Collect(int docId)
        {
            AggLeaf[] leaves = _leaves;
            for (int i = 0; i < leaves.Length; i++)
            {
                leaves[i].Collect(docId);
            }
        }

        public void Finish()
        {
            if (!_finished)
            {
                _finished = true;
                SubNodes.Finish(_leaves);
            }
        }
    }
}
