using OneIndex.Analysis;

namespace OneIndex.Search;

/// <summary>
/// Turns a <see cref="TokenGraph"/> (a query with its synonym readings) into query objects. Two shapes, matching the two ways
/// people search: all the words anywhere (<see cref="ToWords"/>), and the words as a phrase (<see cref="ToPhrase"/>). Both
/// are exact over the graph: a document matches iff some reading of the query matches it.
/// </summary>
public static class TokenGraphQueries
{
    /// <summary>The default boost of a synonym reading relative to the original words (1).</summary>
    public const float DefaultSynonymBoost = 0.8f;

    /// <summary>
    /// Every word must match, in any position. Between consecutive cut nodes of the graph (see <see cref="TokenGraph.CutNodes"/>)
    /// the readings are alternatives (a Should group); the stretches are combined with Must, so the number of clauses grows with the
    /// sum, not the product, of the stretches' readings. A synonym of several words is a phrase; the original words are plain
    /// terms. A document that has the original words ranks above one with only a synonym because synonym parts carry the lower boost.
    /// </summary>
    /// <param name="graph">The query graph.</param>
    /// <param name="field">The field to search.</param>
    /// <param name="synonymBoost">The boost of synonym parts; finite and not negative. Default <see cref="DefaultSynonymBoost"/>.</param>
    /// <returns>The query; <see cref="MatchAllQuery"/> when the query has no words (empty, or only stop words), as an empty search returns every document.</returns>
    public static Query ToWords(TokenGraph graph, string field, float synonymBoost = DefaultSynonymBoost)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(field);
        if (graph.SlotCount == 0)
        {
            return new MatchAllQuery();
        }

        IReadOnlyList<int> cuts = graph.CutNodes();
        var stretches = new List<Query>();
        for (int c = 0; c + 1 < cuts.Count; c++)
        {
            var readings = new List<Query>();
            foreach (IReadOnlyList<TokenGraphEdge> path in graph.PathsBetween(cuts[c], cuts[c + 1]))
            {
                readings.Add(PathAsWords(path, field, synonymBoost));
            }

            stretches.Add(readings.Count == 1 ? readings[0] : Group(readings, Occur.Should));
        }

        return stretches.Count == 1 ? stretches[0] : Group(stretches, Occur.Must);
    }

    /// <summary>
    /// The words as a phrase. Each reading of the whole query is one phrase (the original words, or the words with synonym spans
    /// replaced by the synonym's words), and the readings are alternatives. Positions are what a phrase over the reading's
    /// terms means: a multi-word synonym that replaces one word makes the phrase longer, and replacing two words by one makes
    /// it shorter, so the rest of the phrase stays adjacent to it.
    /// <para>
    /// Where stop words left gaps (<see cref="StopWordSettings.PreserveGaps"/>), a reading's slop is the base slop plus the
    /// gaps between its words: an over-approximation that also accepts a little extra distance. With the default of no gaps the
    /// phrase is exact.
    /// </para>
    /// </summary>
    /// <param name="graph">The query graph.</param>
    /// <param name="field">The field to search.</param>
    /// <param name="slop">The slop every reading gets, 0 or more. Default 0.</param>
    /// <param name="synonymBoost">The boost of readings that use a synonym; finite and not negative. Default <see cref="DefaultSynonymBoost"/>.</param>
    /// <returns>The query; <see cref="MatchAllQuery"/> when the query has no words.</returns>
    public static Query ToPhrase(TokenGraph graph, string field, int slop = 0, float synonymBoost = DefaultSynonymBoost)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegative(slop);
        if (graph.SlotCount == 0)
        {
            return new MatchAllQuery();
        }

        var readings = new List<Query>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IReadOnlyList<TokenGraphEdge> path in graph.Paths())
        {
            var terms = new List<string>();
            int gaps = 0;
            bool synonym = false;
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0)
                {
                    gaps += path[i].GapBefore;
                }

                terms.AddRange(path[i].Terms);
                synonym |= path[i].IsSynonym;
            }

            string identity = string.Join('\u001F', terms) + "\u001E" + gaps;
            if (!seen.Add(identity))
            {
                continue;
            }

            float boost = synonym ? synonymBoost : 1f;
            readings.Add(terms.Count == 1 && slop == 0
                ? new TermQuery(field, terms[0]) { Boost = boost }
                : new PhraseQuery(field, terms) { Slop = slop + gaps, Boost = boost });
        }

        return readings.Count == 1 ? readings[0] : Group(readings, Occur.Should);
    }

    private static Query PathAsWords(IReadOnlyList<TokenGraphEdge> path, string field, float synonymBoost)
    {
        var parts = new List<Query>(path.Count);
        foreach (TokenGraphEdge e in path)
        {
            float boost = e.IsSynonym ? synonymBoost : 1f;
            parts.Add(e.Terms.Count == 1
                ? new TermQuery(field, e.Terms[0]) { Boost = boost }
                : new PhraseQuery(field, e.Terms) { Boost = boost });
        }

        return parts.Count == 1 ? parts[0] : Group(parts, Occur.Must);
    }

    private static BooleanQuery Group(List<Query> queries, Occur occur)
    {
        var clauses = new BooleanClause[queries.Count];
        for (int i = 0; i < clauses.Length; i++)
        {
            clauses[i] = new BooleanClause(queries[i], occur);
        }

        return new BooleanQuery(clauses);
    }
}
