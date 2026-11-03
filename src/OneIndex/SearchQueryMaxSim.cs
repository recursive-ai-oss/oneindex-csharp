using System.Linq.Expressions;
using OneIndex.Aggregations;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex;

/// <summary>The MaxSim stage of a <see cref="SearchQuery{T}"/>.</summary>
/// <param name="Window">How many first-stage hits are scored.</param>
/// <param name="Field">The multi-vector member, or null for the type's only one.</param>
/// <param name="Encoder">The encoder of the query text, or null for the index's.</param>
/// <param name="Query">The query text, or null for the search text.</param>
internal sealed record MaxSimSpec(int Window, string? Field, IMultiVectorEncoder? Encoder, string? Query);

public sealed partial class SearchQuery<T>
{
    /// <summary>
    /// Adds a late-interaction stage (ColBERT, Khattab and Zaharia, SIGIR 2020, section 3.5): the best <paramref name="window"/> hits of the search
    /// (text, vector or hybrid, after filters) are re-ordered by MaxSim, the sum over the query's token vectors of the best cosine among the document's
    /// token vectors, and come back in that order (docs/multi-vector.md). The query text is encoded with the index's <see cref="IMultiVectorEncoder"/>
    /// (or the one given) in the query role. Each hit's <see cref="SearchHit{T}.Score"/> is then the MaxSim score and <see cref="SearchHit{T}.FirstStageScore"/>
    /// the score it had before; documents without token vectors score 0 and sort last, ties keep the first-stage order. Facets and aggregations still count all
    /// matches; <see cref="SearchResults{T}.TotalHits"/> is the first stage's. Not combined with <c>Rerank</c>, <c>OrderBy</c>, <c>SearchAfter</c> or <c>OrderByDistance</c>.
    /// With no search text the first stage is every document that passes the filters, so <c>Search().Where(...).MaxSim(query: "...")</c> is an exact scan
    /// limited to <paramref name="window"/> candidates (at most <see cref="MaxResultWindow"/>).
    /// </summary>
    /// <param name="window">How many hits to score, 1 to <see cref="MaxResultWindow"/>. Default 100.</param>
    /// <param name="encoder">The encoder; <see langword="null"/> (default) uses the index's (<see cref="SearchMultiVectorEncoders.Default"/> when it was opened).</param>
    /// <param name="query">The text to encode as the query; <see langword="null"/> (default) is the search text as typed.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The window is out of range.</exception>
    public SearchQuery<T> MaxSim(int window = DefaultRerankWindow, IMultiVectorEncoder? encoder = null, string? query = null)
        => MaxSimCore(window, null, encoder, query);

    /// <summary>Like <see cref="MaxSim(int, IMultiVectorEncoder, string)"/> for a named <c>[MultiVector]</c> property, for types that have several.</summary>
    /// <typeparam name="TField">The property type, <c>float[][]</c>.</typeparam>
    /// <param name="tokens">The property, like <c>x =&gt; x.Tokens</c>.</param>
    /// <param name="window">How many hits to score. Default 100.</param>
    /// <param name="encoder">The encoder, or the index's.</param>
    /// <param name="query">The query text, or the search text.</param>
    /// <returns>The search.</returns>
    /// <exception cref="ArgumentException">The property is not a <c>[MultiVector]</c> property.</exception>
    public SearchQuery<T> MaxSim<TField>(Expression<Func<T, TField>> tokens, int window = DefaultRerankWindow, IMultiVectorEncoder? encoder = null, string? query = null)
    {
        MappedMember m = MemberNames.Resolve(_index.Mapping, tokens, nameof(MaxSim));
        if (!m.IsMultiVector)
        {
            throw new ArgumentException($"MaxSim: '{m.Name}' is not a [MultiVector] property.", nameof(tokens));
        }

        return MaxSimCore(window, m.Name, encoder, query);
    }

    private SearchQuery<T> MaxSimCore(int window, string? field, IMultiVectorEncoder? encoder, string? query)
    {
        if (window < 1 || window > MaxResultWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window), $"window must be from 1 to {MaxResultWindow}.");
        }

        return With((_extras ?? QueryExtras.None) with { MaxSim = new MaxSimSpec(window, field, encoder, query) });
    }

    private (MaxSimSpec Spec, MappedMember Member, float[] Query, int Dimension)? EffectiveMaxSim()
    {
        MaxSimSpec? spec = _extras?.MaxSim;
        if (spec is null)
        {
            return null;
        }

        if (_extras?.Rerank is { Disabled: false })
        {
            throw new NotSupportedException("MaxSim cannot be combined with Rerank: each decides the final order. Use one of them.");
        }

        if (_stages?.Sort is not null)
        {
            throw new NotSupportedException("MaxSim cannot be combined with OrderBy, SearchAfter or OrderByDistance: MaxSim decides the order.");
        }

        MappedMember? member = null;
        foreach (MappedMember m in _index.Mapping.Members)
        {
            if (!m.IsMultiVector || (spec.Field is not null && m.Name != spec.Field))
            {
                continue;
            }

            if (member is not null)
            {
                throw new InvalidOperationException($"{typeof(T).Name} has several [MultiVector] properties; say which one MaxSim reads: MaxSim(x => x.{member.Name}) or MaxSim(x => x.{m.Name}).");
            }

            member = m;
        }

        if (member is null)
        {
            throw new InvalidOperationException($"{typeof(T).Name} has no [MultiVector] property, so MaxSim has nothing to score.");
        }

        IMultiVectorEncoder encoder = spec.Encoder ?? _index.MultiVectorEncoder
            ?? throw new InvalidOperationException("MaxSim needs an encoder for the query: pass one to MaxSim, open the index with one, or set SearchMultiVectorEncoders.Default (docs/multi-vector.md).");
        if (encoder.Dimension != member.Dimensions)
        {
            throw new InvalidOperationException($"Multi-vector encoder '{encoder.ModelId}' produces {encoder.Dimension} dimensions, but [MultiVector] member '{member.Name}' declares {member.Dimensions}.");
        }

        string? text = spec.Query ?? _text;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("MaxSim encodes the search text as the query, but this search has none. Use Search(text), or MaxSim(query: text).");
        }

        float[][] rows = spec.Encoder is null && _index.AutoMultiVector is { } auto
            ? auto.EncodeQuery(text)
            : AutoMultiVector.EncodeQuery(encoder, text, null, null);
        var flat = new float[rows.Length * member.Dimensions];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i].CopyTo(flat, i * member.Dimensions);
        }

        return (spec, member, flat, member.Dimensions);
    }

    // The first stage: the same search with the window as its page (paging, highlights and explain are applied after the stage).
    private SearchQuery<T> MaxSimFirstStage(MaxSimSpec spec)
    {
        int n = Math.Max(spec.Window, Math.Min(MaxResultWindow, _skip + _take));
        QueryExtras extras = (_extras ?? QueryExtras.None) with { MaxSim = null, Rerank = new RerankSpec(DefaultRerankWindow, null, null, null, Disabled: true) };
        return new SearchQuery<T>(_index, _text, _query, _filters, n, 0, _exact, _highlight, _stages, extras);
    }

    private SearchResults<T> ExecuteMaxSim((MaxSimSpec Spec, MappedMember Member, float[] Query, int Dimension) stage, CancellationToken cancellationToken)
    {
        SearchResults<T> first = MaxSimFirstStage(stage.Spec).Execute(cancellationToken);
        int count = first.Count;
        var scores = new float[count];
        for (int i = 0; i < count; i++)
        {
            FieldValue packed = first[i].Stored.Get(stage.Member.Name);
            if (stage.Query.Length > 0 && packed.Kind == FieldValueKind.Bytes)
            {
                float[] doc = MultiVector.Unpack(packed.AsBytes().Span, out _, out int docDim);
                if (doc.Length > 0 && docDim != stage.Dimension)
                {
                    throw new FormatException($"The token vectors of document '{first[i].Id}' have {docDim} values, but '{stage.Member.Name}' declares {stage.Dimension}.");
                }

                scores[i] = (float)Vectors.MaxSim.Score(stage.Query, doc, stage.Dimension);
            }
        }

        // Best score first; equal scores keep their first-stage order, so the result is deterministic.
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => scores[b] != scores[a] ? scores[b].CompareTo(scores[a]) : a.CompareTo(b));
        var hits = new List<SearchHit<T>>(Math.Max(0, Math.Min(_take, count - _skip)));
        for (int rank = _skip; rank < count && hits.Count < _take; rank++)
        {
            SearchHit<T> h = first[order[rank]];
            float s = scores[order[rank]];
            Explanation? explanation = h.Explanation is null
                ? null
                : new Explanation(s, "MaxSim: the sum over query tokens of the best cosine among the document's tokens; the first-stage score is kept as FirstStageScore", h.Explanation);
            hits.Add(h.WithRerank(s, explanation));
        }

        return new SearchResults<T>(hits, first.TotalHits, first.TotalHitsExact)
        {
            Facets = first.Facets,
            Aggregations = first.Aggregations,
        };
    }
}
