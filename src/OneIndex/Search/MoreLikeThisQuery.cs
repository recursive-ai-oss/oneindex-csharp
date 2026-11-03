using System.Text;
using OneIndex.Analysis;
using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>
/// Documents that resemble a piece of text or an indexed document. The engine analyzes the source with each field's analyzer, counts the
/// terms, keeps the <see cref="MaxQueryTerms"/> terms with the highest term frequency times inverse document frequency (the usual
/// relevance-feedback selection of the IR textbooks: frequent in the source, rare in the collection), and matches documents that contain
/// enough of them, scored by BM25F with each term optionally boosted by its selection weight. The query is expanded when the weight is
/// made, against the snapshot being searched, so the chosen terms follow the data. See docs/more-like-this.md.
/// </summary>
public sealed record MoreLikeThisQuery : Query
{
    /// <summary>The largest number of selected terms accepted.</summary>
    public const int MaxTermsLimit = 1024;

    /// <summary>Creates a query that resembles a text.</summary>
    /// <param name="text">The text to resemble.</param>
    /// <returns>The query. Fields default to every full-text field.</returns>
    public static MoreLikeThisQuery FromText(string text) => new() { Text = text ?? throw new ArgumentNullException(nameof(text)) };

    /// <summary>Creates a query that resembles an indexed document, read from its stored source. The document itself is left out of the results.</summary>
    /// <param name="id">The document identifier (the value of the schema's identifier field).</param>
    /// <returns>The query.</returns>
    public static MoreLikeThisQuery FromDocument(string id) => new() { DocumentId = id ?? throw new ArgumentNullException(nameof(id)) };

    /// <summary>The text to resemble; exactly one of <see cref="Text"/> and <see cref="DocumentId"/> is set. It is analyzed once per field.</summary>
    public string? Text { get; init; }

    /// <summary>The identifier of the indexed document to resemble. A committed live document with stored source is required, otherwise the query raises a <see cref="QueryException"/>.</summary>
    public string? DocumentId { get; init; }

    /// <summary>The full-text fields to read from the source and to match in. Empty (default) means every indexed full-text field.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>How many terms the query keeps, 1 to <see cref="MaxTermsLimit"/>. Default 25.</summary>
    public int MaxQueryTerms { get; init; } = 25;

    /// <summary>A term must occur at least this often in the source, 1 or more. Default 1.</summary>
    public int MinTermFrequency { get; init; } = 1;

    /// <summary>A term must occur in at least this many indexed documents, 1 or more. Default 1. For <see cref="DocumentId"/> the source counts as one, so a term only the source holds is dropped whatever this is: it cannot find another document.</summary>
    public int MinDocumentFrequency { get; init; } = 1;

    /// <summary>The share of the selected terms a document must contain, above 0 up to 1. Default 0.3, rounded up; at least one term is always needed.</summary>
    public double MinimumShouldMatchFraction { get; init; } = 0.3;

    /// <summary>Whether each selected term is boosted by its selection weight relative to the best term (so the best term has boost 1). Default <see langword="true"/>.</summary>
    public bool BoostTerms { get; init; } = true;

    /// <summary>Whether the source document may appear among the results (only for <see cref="DocumentId"/>). Default <see langword="false"/>.</summary>
    public bool IncludeSource { get; init; }

    /// <summary>Compares fields one by one (the generated record equality would compare the list by reference).</summary>
    /// <param name="other">The other query.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(MoreLikeThisQuery? other)
        => other is not null && Text == other.Text && DocumentId == other.DocumentId && MaxQueryTerms == other.MaxQueryTerms && MinTermFrequency == other.MinTermFrequency
            && MinDocumentFrequency == other.MinDocumentFrequency && MinimumShouldMatchFraction.Equals(other.MinimumShouldMatchFraction) && BoostTerms == other.BoostTerms
            && IncludeSource == other.IncludeSource && Boost.Equals(other.Boost) && Fields.SequenceEqual(other.Fields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Text);
        hash.Add(DocumentId);
        hash.Add(MaxQueryTerms);
        hash.Add(Boost);
        foreach (string f in Fields)
        {
            hash.Add(f);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitMoreLikeThis(this);
}

/// <summary>Turns a <see cref="MoreLikeThisQuery"/> into a boolean query of the selected terms against one snapshot.</summary>
internal static class MoreLikeThisExpander
{
    public static Query Expand(SearchContext context, MoreLikeThisQuery q)
    {
        if ((q.Text is null) == (q.DocumentId is null))
        {
            throw new QueryException("A more-like-this query needs exactly one of Text and DocumentId.");
        }

        if (q.MaxQueryTerms is < 1 or > MoreLikeThisQuery.MaxTermsLimit || q.MinTermFrequency < 1 || q.MinDocumentFrequency < 1
            || !(q.MinimumShouldMatchFraction > 0 && q.MinimumShouldMatchFraction <= 1))
        {
            throw new QueryException($"A more-like-this query needs MaxQueryTerms from 1 to {MoreLikeThisQuery.MaxTermsLimit}, MinTermFrequency and MinDocumentFrequency of at least 1, and a MinimumShouldMatchFraction above 0 and at most 1.");
        }

        if (!float.IsFinite(q.Boost) || q.Boost < 0f)
        {
            throw new QueryException($"A query boost must be finite and not negative, got {q.Boost}.");
        }

        var fields = new List<FieldDefinition>();
        if (q.Fields.Count == 0)
        {
            foreach (FieldDefinition f in context.Schema.Fields)
            {
                if (f.Type == FieldType.Text && f.Indexed && f.IndexOptions != IndexOptions.None)
                {
                    fields.Add(f);
                }
            }
        }
        else
        {
            foreach (string name in q.Fields)
            {
                if (!context.Schema.TryGetField(name, out FieldDefinition? def))
                {
                    throw new QueryException($"The schema has no field '{name}'.");
                }

                if (def.Type != FieldType.Text || !def.Indexed || def.IndexOptions == IndexOptions.None)
                {
                    throw new QueryException($"Field '{name}' is not a full-text field, so more-like-this cannot use it.");
                }

                if (!fields.Contains(def))
                {
                    fields.Add(def);
                }
            }
        }

        Document? source = null;
        if (q.DocumentId is { } id)
        {
            source = FindSource(context, id) ?? throw new QueryException($"No live document '{id}' with stored source was found for more-like-this.");
        }

        // Candidate terms with their selection weight tf * idf (field by field: a term is judged against its own field's statistics).
        var candidates = new List<(string Field, string Term, float Weight)>();
        foreach (FieldDefinition def in fields)
        {
            string analyzerName = def.Analyzer ?? FieldDefinition.DefaultAnalyzer;
            if (!AnalyzerRegistry.TryCreate(analyzerName, out Analyzer? analyzer))
            {
                throw new QueryException($"Field '{def.Name}' uses analyzer '{analyzerName}', which is not registered.");
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (q.Text is { } text)
            {
                Count(analyzer, text, counts);
            }
            else
            {
                foreach (DocumentField df in source!.Fields)
                {
                    if (string.Equals(df.Name, def.Name, StringComparison.Ordinal) && df.Value.Kind == FieldValueKind.Text)
                    {
                        Count(analyzer, df.Value.AsText(), counts);
                    }
                }
            }

            FieldStatistics stats = context.GetFieldStatistics(def.Name);
            long minDf = Math.Max(q.MinDocumentFrequency, source is null ? 1 : 2);
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (kv.Value < q.MinTermFrequency)
                {
                    continue;
                }

                long df = context.GetDocFreq(def.Name, Encoding.UTF8.GetBytes(kv.Key));
                if (df < minDf)
                {
                    continue;
                }

                candidates.Add((def.Name, kv.Key, kv.Value * Bm25.Idf(stats.DocCount, df)));
            }
        }

        // Best weight first; ties by field and term in ordinal order so the choice never depends on dictionary order.
        candidates.Sort(static (a, b) =>
        {
            int c = b.Weight.CompareTo(a.Weight);
            c = c != 0 ? c : string.CompareOrdinal(a.Field, b.Field);
            return c != 0 ? c : string.CompareOrdinal(a.Term, b.Term);
        });
        if (candidates.Count > q.MaxQueryTerms)
        {
            candidates.RemoveRange(q.MaxQueryTerms, candidates.Count - q.MaxQueryTerms);
        }

        if (candidates.Count == 0)
        {
            return new BooleanQuery([]); // nothing to resemble: matches nothing
        }

        float best = candidates[0].Weight;
        var clauses = new List<BooleanClause>(candidates.Count + 1);
        foreach ((string field, string term, float weight) in candidates)
        {
            float boost = q.BoostTerms && best > 0f ? weight / best : 1f;
            clauses.Add(new BooleanClause(new TermQuery(field, term) { Boost = boost }, Occur.Should));
        }

        int needed = Math.Max(1, (int)Math.Ceiling(q.MinimumShouldMatchFraction * candidates.Count - 1e-9));
        var matching = new BooleanQuery(clauses) { MinimumShouldMatch = needed };
        if (source is null || q.IncludeSource)
        {
            return matching with { Boost = q.Boost };
        }

        string idField = context.Schema.IdField ?? throw new QueryException("The schema has no identifier field.");
        return new BooleanQuery([new BooleanClause(matching, Occur.Must), new BooleanClause(new TermQuery(idField, q.DocumentId!), Occur.MustNot)]) { Boost = q.Boost };
    }

    private static void Count(Analyzer analyzer, string text, Dictionary<string, int> counts)
    {
        foreach (AnalyzedToken t in analyzer.Analyze(text))
        {
            counts[t.Term] = counts.GetValueOrDefault(t.Term) + 1;
        }
    }

    // The stored source of the live document with an identifier; null when there is none or segments keep no source.
    private static Document? FindSource(SearchContext context, string id)
    {
        string? idField = context.Schema.IdField;
        if (idField is null)
        {
            return null;
        }

        byte[] term = Encoding.UTF8.GetBytes(id);
        foreach (LeafContext leaf in context.Leaves)
        {
            if (leaf.Reader.GetTextField(idField) is not { } reader || !reader.TryGetTerm(term, out TermInfo info) || leaf.Reader.StoredFields is not { } stored)
            {
                continue;
            }

            PostingsIterator it = reader.GetPostings(info, PostingsFeatures.None);
            for (int d = it.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = it.NextDoc())
            {
                if (leaf.Reader.LiveDocs is { } live && !live.IsLive(d))
                {
                    continue;
                }

                return stored.GetDocument(d);
            }
        }

        return null;
    }
}
