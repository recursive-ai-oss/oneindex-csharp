using System.Linq.Expressions;
using OneIndex.Aggregations;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex;

/// <summary>The hybrid setting of a <see cref="SearchQuery{T}"/>.</summary>
internal sealed record HybridSpec(double Ratio, FusionMethod Fusion, int RankConstant, int Window, string? Field, float[]? Vector, string? Text);

public sealed partial class SearchQuery<T>
{
    /// <summary>
    /// Fuses this search's text matches with the documents nearest in meaning to the search text, using the index's embedder (docs/hybrid.md).
    /// The vector field is the only <c>[Vector(From = ...)]</c> member. Filters apply to both lists, and sorting, facets and paging work on the fused matches.
    /// A search of plain text on an index whose type has such a member and an embedder is hybrid already with the defaults; <c>Hybrid(0)</c> turns that off.
    /// </summary>
    /// <param name="semanticRatio">How much meaning counts against words, 0 to 1 (Meilisearch's <c>semanticRatio</c>). Default 0.75; 0.5 counts both lists the same. 0 is words only, 1 is meaning only.</param>
    /// <param name="fusion">How the two ranked lists combine. Default reciprocal rank fusion; <see cref="FusionMethod.Linear"/> normalises and weights the scores.</param>
    /// <param name="rankConstant">The reciprocal rank constant, 1 to 1,000. Default 10.</param>
    /// <param name="window">How many documents each list contributes, 1 to 10,000. Default 100. The search returns at most the documents of both lists (up to twice the window), so a deep page needs a larger window.</param>
    /// <returns>The search. It needs search text; <c>TotalHits</c> then counts the documents in either list.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public SearchQuery<T> Hybrid(double semanticRatio = HybridQuery.DefaultSemanticRatio, FusionMethod fusion = FusionMethod.ReciprocalRank, int rankConstant = HybridQuery.DefaultRankConstant, int window = 100)
        => HybridCore(MakeSpec(semanticRatio, fusion, rankConstant, window, null, null));

    /// <summary>Like <see cref="Hybrid(double, FusionMethod, int, int)"/> for a named vector property.</summary>
    /// <typeparam name="TVector">The property type, <c>float[]</c>.</typeparam>
    /// <param name="member">The vector property, such as <c>x =&gt; x.Embedding</c>.</param>
    /// <param name="semanticRatio">As above.</param>
    /// <param name="fusion">As above.</param>
    /// <param name="rankConstant">As above.</param>
    /// <param name="window">As above.</param>
    /// <returns>The search.</returns>
    public SearchQuery<T> Hybrid<TVector>(Expression<Func<T, TVector>> member, double semanticRatio = HybridQuery.DefaultSemanticRatio, FusionMethod fusion = FusionMethod.ReciprocalRank, int rankConstant = HybridQuery.DefaultRankConstant, int window = 100)
        => HybridCore(MakeSpec(semanticRatio, fusion, rankConstant, window, VectorMember(member).Name, null));

    /// <summary>Like <see cref="Hybrid{TVector}(Expression{Func{T, TVector}}, double, FusionMethod, int, int)"/> with a query vector you computed, so no embedder is needed.</summary>
    /// <typeparam name="TVector">The property type, <c>float[]</c>.</typeparam>
    /// <param name="member">The vector property.</param>
    /// <param name="vector">The query vector: as long as the property's <c>Dimensions</c> and finite.</param>
    /// <param name="semanticRatio">As above.</param>
    /// <param name="fusion">As above.</param>
    /// <param name="rankConstant">As above.</param>
    /// <param name="window">As above.</param>
    /// <returns>The search. The text arm uses the search text.</returns>
    public SearchQuery<T> Hybrid<TVector>(Expression<Func<T, TVector>> member, float[] vector, double semanticRatio = HybridQuery.DefaultSemanticRatio, FusionMethod fusion = FusionMethod.ReciprocalRank, int rankConstant = HybridQuery.DefaultRankConstant, int window = 100)
    {
        ArgumentNullException.ThrowIfNull(vector);
        MappedMember m = VectorMember(member);
        if (vector.Length != m.Dimensions || vector.Any(static x => !float.IsFinite(x)))
        {
            throw new ArgumentException($"Hybrid: the query vector must hold {m.Dimensions} finite numbers.", nameof(vector));
        }

        return HybridCore(MakeSpec(semanticRatio, fusion, rankConstant, window, m.Name, (float[])vector.Clone()));
    }

    private MappedMember VectorMember<TVector>(Expression<Func<T, TVector>> member)
    {
        MappedMember m = MemberNames.Resolve(_index.Mapping, member, nameof(Hybrid));
        if (m.Type != FieldType.Vector)
        {
            throw new ArgumentException($"Hybrid: '{m.Name}' is not a [Vector] property.", nameof(member));
        }

        return m;
    }

    private static HybridSpec MakeSpec(double ratio, FusionMethod fusion, int rankConstant, int window, string? field, float[]? vector)
    {
        if (!(ratio >= 0.0 && ratio <= 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), "semanticRatio must be from 0 to 1.");
        }

        if (rankConstant < 1 || rankConstant > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(rankConstant), "rankConstant must be from 1 to 1000.");
        }

        if (window < 1 || window > HybridQuery.MaxWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window), $"window must be from 1 to {HybridQuery.MaxWindow}.");
        }

        return new HybridSpec(ratio, fusion, rankConstant, window, field, vector, null);
    }

    internal SearchQuery<T> WhereQuery(Search.Query filter)
    {
        QueryExtras extras = _extras ?? QueryExtras.None;
        return With(extras with { FilterQueries = [.. extras.FilterQueries, filter] });
    }

    internal SearchQuery<T> HybridCore(HybridSpec spec)
        => With((_extras ?? QueryExtras.None) with { Hybrid = spec });

    // An explicit Hybrid(...) wins. Otherwise plain search text on an index with an embedder and exactly one automatically embedded member is hybrid.
    private HybridSpec? EffectiveHybrid()
    {
        HybridSpec? spec = _extras?.Hybrid;
        if (spec is not null)
        {
            // A ratio of 0 is the text search alone: nothing to embed or fuse.
            return spec.Ratio == 0.0 ? null : spec;
        }

        if (_query is null && !string.IsNullOrWhiteSpace(_text) && _index.Auto is { Slots.Length: 1 })
        {
            return new HybridSpec(HybridQuery.DefaultSemanticRatio, FusionMethod.ReciprocalRank, HybridQuery.DefaultRankConstant, 100, null, null, null);
        }

        return null;
    }

    private Search.Query BuildHybrid(Search.Query textQuery, HybridSpec spec)
    {
        AutoEmbedding? auto = _index.Auto;
        string? field = spec.Field ?? (auto is { Slots.Length: 1 } ? auto.Slots[0].Vector : null);
        if (field is null)
        {
            throw new InvalidOperationException(auto is null && spec.Vector is null
                ? "Hybrid search needs an embedder: open the index with one, or set SearchEmbedders.Default (docs/auto-embedding.md)."
                : "Hybrid search needs to know the vector property: use Hybrid(x => x.Member).");
        }

        float[] vector;
        if (spec.Vector is not null)
        {
            vector = spec.Vector;
        }
        else
        {
            string? text = spec.Text ?? _text;
            if (auto is null)
            {
                throw new InvalidOperationException("Hybrid search needs an embedder: open the index with one, or set SearchEmbedders.Default (docs/auto-embedding.md).");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Hybrid search embeds the search text, but this search has none. Use Search(text), or Hybrid(member, vector).");
            }

            vector = auto.EmbedQuery(text);
        }

        int window = spec.Window;
        Search.Query textArm = _extras is null && _filters.Length == 0 ? textQuery : WithExtras(textQuery, applyFunction: false);
        var knn = new KnnQuery(field, vector, window);
        Search.Query vectorArm = _extras is null && _filters.Length == 0 ? knn : WithExtras(knn, applyFunction: false);
        var hybrid = new HybridQuery(textArm, (KnnQuery)vectorArm)
        {
            Fusion = spec.Fusion,
            RankConstant = spec.RankConstant,
            SemanticRatio = spec.Ratio,
            Window = window,
        };
        return _extras?.Function is { } fn ? Wrap(hybrid, fn) : hybrid;
    }
}
