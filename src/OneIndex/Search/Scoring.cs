using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>Walks the documents that match something, in increasing segment-local document id.</summary>
public abstract class DocIdSetIterator
{
    /// <summary>The id returned once the iterator is exhausted. It is larger than every real document id.</summary>
    public const int NoMoreDocs = int.MaxValue;

    /// <summary>The current document: -1 before the first <see cref="NextDoc"/>, <see cref="NoMoreDocs"/> after the last.</summary>
    public abstract int DocId { get; }

    /// <summary>Moves to the next matching document.</summary>
    /// <returns>Its id, or <see cref="NoMoreDocs"/>.</returns>
    public abstract int NextDoc();

    /// <summary>Moves to the first matching document at or after <paramref name="target"/>.</summary>
    /// <param name="target">A document id greater than the current <see cref="DocId"/>.</param>
    /// <returns>Its id, or <see cref="NoMoreDocs"/>.</returns>
    public abstract int Advance(int target);

    /// <summary>An estimate of how many documents the iterator will visit, used to order clauses (cheapest first).</summary>
    public abstract long Cost { get; }
}

/// <summary>
/// A <see cref="DocIdSetIterator"/> that also scores the current document. The two hooks beyond scoring let a collector's
/// threshold prune work: <see cref="AdvanceShallow"/> and <see cref="GetMaxScore"/> bound what the next documents can score, and
/// <see cref="SetMinCompetitiveScore"/> tells the scorer which scores no longer matter (block-max WAND).
/// </summary>
public abstract class Scorer : DocIdSetIterator
{
    /// <summary>The score of the current document. Finite and not NaN.</summary>
    /// <returns>The score.</returns>
    public abstract float Score();

    /// <summary>
    /// Looks ahead without moving: prepares <see cref="GetMaxScore"/> for the run of documents that starts at
    /// <paramref name="target"/>. The default does nothing and reports that the run extends to the end.
    /// </summary>
    /// <param name="target">The document to look ahead to; at least the current document.</param>
    /// <returns>The last document that the next <see cref="GetMaxScore"/> bound covers.</returns>
    public virtual int AdvanceShallow(int target) => NoMoreDocs;

    /// <summary>An upper bound for the score of every document from the last <see cref="AdvanceShallow"/> target up to <paramref name="upTo"/>.</summary>
    /// <param name="upTo">The last document to bound, at most the value <see cref="AdvanceShallow"/> returned.</param>
    /// <returns>A bound; <see cref="float.PositiveInfinity"/> when the scorer cannot say (the default).</returns>
    public virtual float GetMaxScore(int upTo) => float.PositiveInfinity;

    /// <summary>Tells the scorer that documents scoring below <paramref name="minScore"/> will be ignored. Documents scoring exactly that may still be returned. The default ignores the hint.</summary>
    /// <param name="minScore">The threshold; only rises during a search.</param>
    public virtual void SetMinCompetitiveScore(float minScore)
    {
    }
}

/// <summary>How much of scoring a collector needs.</summary>
public enum ScoreMode : byte
{
    /// <summary>Visit every matching document and compute every score; counts are exact.</summary>
    Complete,

    /// <summary>Visit every matching document; scores are not needed.</summary>
    CompleteNoScores,

    /// <summary>Only the best scores matter, so non-competitive documents may be skipped and the total count becomes a lower bound.</summary>
    TopScores,
}

/// <summary>A segment inside a search: the reader, its place in the segment list and its offset in the global document numbering.</summary>
/// <param name="Reader">The segment reader.</param>
/// <param name="Ordinal">The segment's index in <see cref="SearchContext.Leaves"/>.</param>
/// <param name="DocBase">The number added to a local document id to get the global one.</param>
public sealed record LeafContext(ISegmentReader Reader, int Ordinal, int DocBase);

/// <summary>
/// Everything a query needs to be compiled against a snapshot: the schema, the segments and index-wide statistics (document
/// frequencies and field lengths are summed across segments so every segment scores with the same numbers).
/// </summary>
public sealed class SearchContext
{
    /// <summary>Creates a context over open segment readers. The caller keeps ownership of the readers.</summary>
    /// <param name="schema">The schema of the snapshot.</param>
    /// <param name="segments">The segment readers, in commit order.</param>
    public SearchContext(Schema schema, IReadOnlyList<ISegmentReader> segments)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        ArgumentNullException.ThrowIfNull(segments);
        var leaves = new List<LeafContext>(segments.Count);
        long docBase = 0;
        foreach (ISegmentReader segment in segments)
        {
            leaves.Add(new LeafContext(segment, leaves.Count, checked((int)docBase)));
            docBase += segment.MaxDoc;
        }

        if (docBase > int.MaxValue)
        {
            throw new ArgumentException("The segments hold more than 2^31-1 documents together.", nameof(segments));
        }

        Leaves = leaves;
        MaxDoc = (int)docBase;
    }

    /// <summary>The schema of the snapshot.</summary>
    public Schema Schema { get; }

    /// <summary>The segments with their offsets.</summary>
    public IReadOnlyList<LeafContext> Leaves { get; }

    /// <summary>The number of global document ids, deleted documents included.</summary>
    public int MaxDoc { get; }

    /// <summary>The number of documents across segments that contain a term.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="term">The term in UTF-8.</param>
    /// <returns>The document frequency (deleted documents included, as their postings are still present).</returns>
    public long GetDocFreq(string field, ReadOnlySpan<byte> term)
    {
        long total = 0;
        foreach (LeafContext leaf in Leaves)
        {
            if (leaf.Reader.GetTextField(field) is { } reader && reader.TryGetTerm(term, out TermInfo info))
            {
                total += info.DocFreq;
            }
        }

        return total;
    }

    /// <summary>The counts of a field summed across segments.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The summed statistics; zeros when no segment has the field.</returns>
    public FieldStatistics GetFieldStatistics(string field)
    {
        long docCount = 0;
        long sumTotal = 0;
        long sumDoc = 0;
        foreach (LeafContext leaf in Leaves)
        {
            if (leaf.Reader.GetTextField(field) is { } reader)
            {
                FieldStatistics s = reader.Statistics;
                docCount += s.DocCount;
                sumTotal += s.SumTotalTermFreq;
                sumDoc += s.SumDocFreq;
            }
        }

        return new FieldStatistics(checked((int)docCount), sumTotal, sumDoc);
    }
}

/// <summary>A query prepared against a <see cref="SearchContext"/>: index-wide statistics are fixed, and it yields a scorer per segment.</summary>
public abstract class Weight
{
    /// <summary>Creates the scorer for one segment.</summary>
    /// <param name="leaf">The segment.</param>
    /// <returns>The scorer, or <see langword="null"/> when nothing in the segment can match.</returns>
    public abstract Scorer? GetScorer(LeafContext leaf);
}

/// <summary>Turns queries into weights. The query engine implements it with a <see cref="QueryVisitor{TResult}"/>.</summary>
public interface IWeightFactory
{
    /// <summary>Prepares a query for a snapshot.</summary>
    /// <param name="query">The query.</param>
    /// <param name="context">The snapshot.</param>
    /// <param name="scoreMode">What the collector needs; a factory may choose cheaper scorers for <see cref="ScoreMode.CompleteNoScores"/>.</param>
    /// <returns>The weight.</returns>
    /// <exception cref="NotSupportedException">The factory does not handle the kind of query.</exception>
    Weight CreateWeight(Query query, SearchContext context, ScoreMode scoreMode);
}

/// <summary>Receives matches. One collector serves one search; it asks for a <see cref="LeafCollector"/> per segment.</summary>
public abstract class Collector
{
    /// <summary>What the collector needs from scoring.</summary>
    public abstract ScoreMode ScoreMode { get; }

    /// <summary>Starts collecting from a segment.</summary>
    /// <param name="leaf">The segment.</param>
    /// <returns>The collector for the segment.</returns>
    public abstract LeafCollector GetLeafCollector(LeafContext leaf);
}

/// <summary>Receives the matches of one segment.</summary>
public abstract class LeafCollector
{
    /// <summary>Gives the collector the scorer, to read scores from and to hand thresholds to. Called before the first <see cref="Collect"/>.</summary>
    /// <param name="scorer">The scorer, positioned on the document being collected.</param>
    public abstract void SetScorer(Scorer scorer);

    /// <summary>Receives a matching, live document.</summary>
    /// <param name="docId">The segment-local document id.</param>
    public abstract void Collect(int docId);
}

/// <summary>Runs queries over a snapshot: it asks the factory for a weight, walks each segment's scorer, skips deleted documents and feeds the collector.</summary>
/// <param name="context">The snapshot.</param>
/// <param name="factory">The query engine.</param>
public sealed class IndexSearcher(SearchContext context, IWeightFactory factory)
{
    private const int CancellationCheckInterval = 1024;

    /// <summary>The snapshot being searched.</summary>
    public SearchContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    private IWeightFactory Factory { get; } = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>Searches and feeds <paramref name="collector"/>.</summary>
    /// <param name="query">The query.</param>
    /// <param name="collector">Receives the matches.</param>
    /// <param name="cancellationToken">Cancels the search; checked every 1,024 documents.</param>
    public void Search(Query query, Collector collector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(collector);
        Weight weight = Factory.CreateWeight(query, Context, collector.ScoreMode);
        foreach (LeafContext leaf in Context.Leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scorer? scorer = weight.GetScorer(leaf);
            if (scorer is null)
            {
                continue;
            }

            LeafCollector leafCollector = collector.GetLeafCollector(leaf);
            leafCollector.SetScorer(scorer);
            ILiveDocs? live = leaf.Reader.LiveDocs;
            int visited = 0;
            for (int doc = scorer.NextDoc(); doc != DocIdSetIterator.NoMoreDocs; doc = scorer.NextDoc())
            {
                if ((++visited % CancellationCheckInterval) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (live is null || live.IsLive(doc))
                {
                    leafCollector.Collect(doc);
                }
            }
        }
    }

    /// <summary>Searches for the best-scoring documents.</summary>
    /// <param name="query">The query.</param>
    /// <param name="count">How many documents to return, at least 1.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The top documents by score, ties broken by lower global document id first.</returns>
    public TopDocs Search(Query query, int count, CancellationToken cancellationToken = default)
    {
        var collector = new TopScoreCollector(count);
        Search(query, collector, cancellationToken);
        return collector.GetTopDocs();
    }
}
