using System.Linq.Expressions;
using OneIndex.Index;
using OneIndex.Aggregations;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex;

/// <summary>The rerank setting of a <see cref="SearchQuery{T}"/>.</summary>
/// <param name="Window">How many first-stage hits are reranked.</param>
/// <param name="Fields">The text members that make the passage, or null for the default.</param>
/// <param name="Reranker">The reranker, or null for the index's.</param>
/// <param name="Query">The query text the reranker reads, or null for the search text.</param>
/// <param name="Disabled">Switches off automatic reranking.</param>
internal sealed record RerankSpec(int Window, string[]? Fields, IReranker? Reranker, string? Query, bool Disabled = false);

public sealed partial class SearchQuery<T>
{
    /// <summary>The default number of first-stage hits that <see cref="Rerank(int, IReranker, string)"/> rescores.</summary>
    public const int DefaultRerankWindow = 100;

    /// <summary>
    /// Adds a rerank stage: the best <paramref name="window"/> hits of the search (text, vector or hybrid, after filters) are rescored by a
    /// cross-encoder that reads the query and each hit's text together, and come back in that order (docs/rerank.md). The passage of a hit is its
    /// <c>[FullText(Rerank = true)]</c> fields, or all its full-text fields when none is marked, joined by a space. Each hit's <see cref="SearchHit{T}.Score"/>
    /// is then the reranker's score and <see cref="SearchHit{T}.FirstStageScore"/> the score it had before. Facets and aggregations still count all matches,
    /// not the window; <see cref="SearchResults{T}.TotalHits"/> is the first stage's. A page deeper than the window reranks as many hits as the page needs.
    /// Not combined with <c>OrderBy</c>, <c>SearchAfter</c> or <c>OrderByDistance</c> (the reranker decides the order).
    /// </summary>
    /// <param name="window">How many hits to rescore, 1 to <see cref="MaxResultWindow"/>. Default 100: the cost is one model pass over the query and a passage per hit.</param>
    /// <param name="reranker">The reranker; <see langword="null"/> (default) uses the index's (<see cref="SearchRerankers.Default"/> when it was opened).</param>
    /// <param name="query">The text the reranker reads as the query; <see langword="null"/> (default) is the search text as typed, which a search made with query objects does not have.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The window is out of range.</exception>
    public SearchQuery<T> Rerank(int window = DefaultRerankWindow, IReranker? reranker = null, string? query = null)
        => RerankCore(window, null, reranker, query);

    /// <summary>Like <see cref="Rerank(int, IReranker, string)"/> but reads one property as the passage.</summary>
    /// <typeparam name="TField">The property type, a string or a collection of strings.</typeparam>
    /// <param name="passage">The property that holds the text to read, like <c>x =&gt; x.Body</c>; it must be a text property.</param>
    /// <param name="window">How many hits to rescore. Default 100.</param>
    /// <param name="reranker">The reranker, or the index's.</param>
    /// <param name="query">The query text, or the search text.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The property is not a text property.</exception>
    public SearchQuery<T> Rerank<TField>(Expression<Func<T, TField>> passage, int window = DefaultRerankWindow, IReranker? reranker = null, string? query = null)
    {
        MappedMember m = MemberNames.Resolve(_index.Mapping, passage, nameof(Rerank));
        if (m.Type != FieldType.Text && m.Type != FieldType.Keyword)
        {
            throw new ArgumentException($"Rerank: '{m.Name}' is not a text property.", nameof(passage));
        }

        return RerankCore(window, [m.Name], reranker, query);
    }

    /// <summary>Turns off the rerank stage, including the automatic one of a type with <c>[FullText(Rerank = true)]</c> fields.</summary>
    /// <returns>The search.</returns>
    public SearchQuery<T> WithoutRerank()
        => With((_extras ?? QueryExtras.None) with { Rerank = new RerankSpec(DefaultRerankWindow, null, null, null, Disabled: true) });

    private SearchQuery<T> RerankCore(int window, string[]? fields, IReranker? reranker, string? query)
    {
        if (window < 1 || window > MaxResultWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window), $"window must be from 1 to {MaxResultWindow}.");
        }

        return With((_extras ?? QueryExtras.None) with { Rerank = new RerankSpec(window, fields, reranker, query) });
    }

    internal SearchQuery<T> RerankSpecCore(RerankSpec spec)
        => With((_extras ?? QueryExtras.None) with { Rerank = spec });

    private (RerankSpec Spec, IReranker Reranker, string Query)? EffectiveRerank()
    {
        RerankSpec? spec = _extras?.Rerank;
        if (spec is { Disabled: true })
        {
            return null;
        }

        bool automatic = false;
        if (spec is null)
        {
            // A type opts in with [FullText(Rerank = true)]; the search must be plain user text on an index that has a reranker.
            if (_index.Reranker is null || _query is not null || string.IsNullOrWhiteSpace(_text) || _stages?.Sort is not null
                || !_index.Mapping.Members.Any(static m => m.Rerank))
            {
                return null;
            }

            spec = new RerankSpec(DefaultRerankWindow, null, null, null);
            automatic = true;
        }

        IReranker reranker = spec.Reranker ?? _index.Reranker
            ?? throw new InvalidOperationException("Rerank needs a reranker: pass one to Rerank, open the index with one, or set SearchRerankers.Default (docs/rerank.md).");
        string? query = spec.Query ?? _text;
        if (string.IsNullOrWhiteSpace(query))
        {
            if (automatic)
            {
                return null;
            }

            throw new InvalidOperationException("Rerank reads the search text as the query, but this search has none. Use Search(text), or Rerank(window, reranker, query).");
        }

        if (_stages?.Sort is not null)
        {
            if (automatic)
            {
                return null;
            }

            throw new NotSupportedException("Rerank cannot be combined with OrderBy, SearchAfter or OrderByDistance: the reranker decides the order.");
        }

        return (spec, reranker, query);
    }

    // The first stage: the same search with the window as its page (paging, highlights and explain are applied after the rerank).
    private SearchQuery<T> FirstStage(RerankSpec spec)
    {
        int n = Math.Max(spec.Window, Math.Min(MaxResultWindow, _skip + _take));
        return new SearchQuery<T>(_index, _text, _query, _filters, n, 0, _exact, _highlight, _stages, (_extras ?? QueryExtras.None) with { Rerank = new RerankSpec(spec.Window, spec.Fields, spec.Reranker, spec.Query, Disabled: true) });
    }

    private string[] PassageNames(RerankSpec spec)
    {
        if (spec.Fields is { } fields)
        {
            return fields;
        }

        IReadOnlyList<MappedMember> members = _index.Mapping.Members;
        string[] marked = [.. members.Where(static m => m.FullText && m.Rerank).Select(static m => m.Name)];
        return marked.Length > 0 ? marked : [.. members.Where(static m => m.FullText).Select(static m => m.Name)];
    }

    private static string PassageOf(Document stored, string[] names)
    {
        var parts = new List<string>(names.Length);
        foreach (string name in names)
        {
            foreach (DocumentField f in stored.Fields)
            {
                if (f.Name == name && f.Value.Kind == FieldValueKind.Text && f.Value.AsText().Length > 0)
                {
                    parts.Add(f.Value.AsText());
                }
            }
        }

        return string.Join(' ', parts);
    }

    private SearchResults<T> ApplyRerank(SearchResults<T> first, RerankSpec spec, IReranker reranker, float[] scores, int count)
    {
        // Best score first; equal scores keep their first-stage order, so the result is deterministic.
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            if (!float.IsFinite(scores[i]))
            {
                throw new RerankException($"The reranker '{reranker.ModelId}' returned a score that is not a finite number.");
            }
        }

        Array.Sort(order, (a, b) => scores[b] != scores[a] ? scores[b].CompareTo(scores[a]) : a.CompareTo(b));
        var hits = new List<SearchHit<T>>(Math.Max(0, Math.Min(_take, count - _skip)));
        for (int rank = _skip; rank < count && hits.Count < _take; rank++)
        {
            SearchHit<T> h = first[order[rank]];
            float s = scores[order[rank]];
            Explanation? explanation = h.Explanation is null
                ? null
                : new Explanation(s, $"rerank by {reranker.ModelId}: the score is the reranker's; the first-stage score is kept as FirstStageScore", h.Explanation);
            hits.Add(h.WithRerank(s, explanation));
        }

        return new SearchResults<T>(hits, first.TotalHits, first.TotalHitsExact)
        {
            Facets = first.Facets,
            Aggregations = first.Aggregations,
        };
    }

    private SearchResults<T> ExecuteReranked(RerankSpec spec, IReranker reranker, string query, CancellationToken cancellationToken)
    {
        SearchResults<T> first = FirstStage(spec).Execute(cancellationToken);
        if (first.Count == 0)
        {
            return new SearchResults<T>([], first.TotalHits, first.TotalHitsExact) { Facets = first.Facets, Aggregations = first.Aggregations };
        }

        string[] names = PassageNames(spec);
        var passages = new string[first.Count];
        for (int i = 0; i < passages.Length; i++)
        {
            passages[i] = PassageOf(first[i].Stored, names);
        }

        var scores = new float[passages.Length];
        try
        {
            reranker.Score(query, passages, scores, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException and not RerankException)
        {
            throw new RerankException($"The reranker '{reranker.ModelId}' failed: {e.Message}", e);
        }

        return ApplyRerank(first, spec, reranker, scores, passages.Length);
    }

    private async ValueTask<SearchResults<T>> ExecuteRerankedAsync(RerankSpec spec, IReranker reranker, string query, CancellationToken cancellationToken)
    {
        SearchResults<T> first = FirstStage(spec).Execute(cancellationToken);
        if (first.Count == 0)
        {
            return new SearchResults<T>([], first.TotalHits, first.TotalHitsExact) { Facets = first.Facets, Aggregations = first.Aggregations };
        }

        string[] names = PassageNames(spec);
        var passages = new string[first.Count];
        for (int i = 0; i < passages.Length; i++)
        {
            passages[i] = PassageOf(first[i].Stored, names);
        }

        var scores = new float[passages.Length];
        try
        {
            await reranker.ScoreAsync(query, passages, scores, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not RerankException)
        {
            throw new RerankException($"The reranker '{reranker.ModelId}' failed: {e.Message}", e);
        }

        return ApplyRerank(first, spec, reranker, scores, passages.Length);
    }
}
