using System.Linq.Expressions;
using OneIndex.Aggregations;
using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex;

/// <summary>The hybrid setting of a <see cref="SearchQuery{T}"/>.</summary>
internal sealed record HybridSpec(double Ratio, FusionMethod Fusion, int RankConstant, int Window, string? Field, float[]? Vector, string? Text);

/// <summary>The sparse arm of a <see cref="SearchQuery{T}"/>: the member (null for the only <c>[Sparse(From = ...)]</c> member) and the list weight.</summary>
internal sealed record SparseSpec(string? Field, double? Weight);

public sealed partial class SearchQuery<T>
{
    /// <summary>
    /// Adds a learned sparse list to the search (docs/sparse.md): the search text is encoded with the index's sparse encoder (query role) and the
    /// best matches by dot product are fused with the text list, and with the vector list when the search is hybrid. The sparse member is the only
    /// <c>[Sparse(From = ...)]</c> member. Combinations: text + sparse; text + vector + sparse (<c>Hybrid(0)</c> drops the vector list).
    /// </summary>
    /// <param name="weight">The sparse list's weight on the scale of the other lists (see <see cref="HybridQuery.SparseWeight"/>). Default 1 under reciprocal rank fusion and 0.5 under linear fusion.</param>
    /// <returns>The search. It needs search text and a sparse encoder; <c>TotalHits</c> counts the documents in any list.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The weight is negative or not finite.</exception>
    public SearchQuery<T> Sparse(double? weight = null) => SparseCore(new SparseSpec(null, CheckSparseWeight(weight)));

    /// <summary>Like <see cref="Sparse(double?)"/> for a named sparse property.</summary>
    /// <typeparam name="TSparse">The property type, <see cref="SparseVector"/>.</typeparam>
    /// <param name="member">The sparse property, such as <c>x =&gt; x.Expansion</c>.</param>
    /// <param name="weight">As above.</param>
    /// <returns>The search.</returns>
    public SearchQuery<T> Sparse<TSparse>(Expression<Func<T, TSparse>> member, double? weight = null)
    {
        MappedMember m = MemberNames.Resolve(_index.Mapping, member, nameof(Sparse));
        if (m.Type != FieldType.Sparse)
        {
            throw new ArgumentException($"Sparse: '{m.Name}' is not a [Sparse] property.", nameof(member));
        }

        return SparseCore(new SparseSpec(m.Name, CheckSparseWeight(weight)));
    }

    private static double? CheckSparseWeight(double? weight)
    {
        if (weight is { } w && !(w >= 0.0 && double.IsFinite(w)))
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "The sparse weight must be finite and not negative.");
        }

        return weight;
    }

    internal SearchQuery<T> SparseCore(SparseSpec spec) => With((_extras ?? QueryExtras.None) with { Sparse = spec });

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

    /// <summary>Like <see cref="Hybrid{TVector}(Expression{Func{T, TVector}}, float[], double, FusionMethod, int, int)"/> for a vector property named by a string.</summary>
    /// <param name="vectorMember">The vector property name.</param>
    /// <param name="vector">The query vector: as long as the property's <c>Dimensions</c> and finite.</param>
    /// <param name="semanticRatio">As above.</param>
    /// <param name="fusion">As above.</param>
    /// <param name="rankConstant">As above.</param>
    /// <param name="window">As above.</param>
    /// <returns>The search.</returns>
    public SearchQuery<T> Hybrid(string vectorMember, float[] vector, double semanticRatio = HybridQuery.DefaultSemanticRatio, FusionMethod fusion = FusionMethod.ReciprocalRank, int rankConstant = HybridQuery.DefaultRankConstant, int window = 100)
    {
        ArgumentNullException.ThrowIfNull(vectorMember);
        ArgumentNullException.ThrowIfNull(vector);
        if (!_index.Mapping.TryGetMember(vectorMember, out MappedMember? m) || m.Type != FieldType.Vector)
        {
            throw new ArgumentException($"Hybrid: '{vectorMember}' is not a vector property of {typeof(T).Name}.", nameof(vectorMember));
        }

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
    // A requested sparse list makes the search a fusion even without a vector list.
    private HybridSpec? EffectiveHybrid()
    {
        HybridSpec? spec = _extras?.Hybrid;
        bool sparse = _extras?.Sparse is not null;
        if (spec is not null)
        {
            // A ratio of 0 is the text search alone: nothing to embed or fuse (unless a sparse list is asked for).
            return spec.Ratio == 0.0 && !sparse ? null : spec;
        }

        if (_query is null && !string.IsNullOrWhiteSpace(_text) && (_index.Auto is { Slots.Length: 1 } || sparse))
        {
            return new HybridSpec(HybridQuery.DefaultSemanticRatio, FusionMethod.ReciprocalRank, HybridQuery.DefaultRankConstant, 100, null, null, null);
        }

        return null;
    }

    private Search.Query BuildHybrid(Search.Query textQuery, HybridSpec spec)
    {
        SparseSpec? sparseSpec = _extras?.Sparse;
        AutoEmbedding? auto = _index.Auto;
        string? field = spec.Field ?? (auto is { Slots.Length: 1 } ? auto.Slots[0].Vector : null);
        // A sparse list without a usable vector list (ratio 0, or no embedder) is text + sparse.
        bool dense = spec.Ratio > 0.0 && (field is not null || sparseSpec is null);
        if (dense && field is null)
        {
            throw new InvalidOperationException(auto is null && spec.Vector is null
                ? "Hybrid search needs an embedder: open the index with one, or set SearchEmbedders.Default (docs/auto-embedding.md)."
                : "Hybrid search needs to know the vector property: use Hybrid(x => x.Member).");
        }

        if (dense && spec.Vector is null && auto is null)
        {
            throw new InvalidOperationException("Hybrid search needs an embedder: open the index with one, or set SearchEmbedders.Default (docs/auto-embedding.md).");
        }

        string? text = spec.Text ?? _text;
        float[]? vector = null;
        if (dense)
        {
            if (spec.Vector is not null)
            {
                vector = spec.Vector;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("Hybrid search embeds the search text, but this search has none. Use Search(text), or Hybrid(member, vector).");
                }

                vector = auto!.EmbedQuery(text);
            }
        }

        Search.Query? sparseArm = null;
        Search.Query? knnExclusion = null;
        if (sparseSpec is not null)
        {
            AutoSparse auto2 = _index.AutoSparse ?? throw new InvalidOperationException("Sparse search needs a sparse encoder: open the index with one, or set SearchSparseEncoders.Default (docs/sparse.md).");
            string? sparseField = sparseSpec.Field ?? (auto2.Slots.Length == 1 ? auto2.Slots[0].Sparse : null);
            if (sparseField is null)
            {
                throw new InvalidOperationException("Sparse search needs to know the sparse property: use Sparse(x => x.Member).");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Sparse search encodes the search text, but this search has none. Use Search(text).");
            }

            sparseArm = new SparseQuery(sparseField, auto2.EncodeQuery(text));
        }

        int window = spec.Window;
        // An excluded word of the text (-draft) removes documents from every list, not only the keyword one: the vector list would otherwise bring them back.
        IReadOnlyList<Search.Query> exclusions = _query is null && !string.IsNullOrWhiteSpace(text) ? new UserTextCompiler(_index.Mapping.Schema).CompileExclusions(text) : [];
        if (exclusions.Count > 0)
        {
            var not = new List<BooleanClause>(exclusions.Count + 1) { new(new MatchAllQuery(), Occur.Filter) };
            not.AddRange(exclusions.Select(static e => new BooleanClause(e, Occur.MustNot)));
            var excluding = new BooleanQuery(not);
            if (vector is not null)
            {
                knnExclusion = excluding;
            }

            if (sparseArm is not null)
            {
                sparseArm = new BooleanQuery([new BooleanClause(sparseArm, Occur.Must), .. not.Skip(1)]);
            }
        }

        Search.Query textArm = _extras is null && _filters.Length == 0 ? textQuery : WithExtras(textQuery, applyFunction: false);
        KnnQuery? knnArm = null;
        if (vector is not null)
        {
            var knn = new KnnQuery(field!, vector, window) { Filter = knnExclusion };
            if (_index.ExternalVectorArm is { } arm && arm.Handles(field!))
            {
                // The static index's IVF candidates still pass through the query's filter (the excluded words included) in KnnWeight.FromCandidates.
                float[] queryVector = vector;
                string vectorField = field!;
                knn = knn with { CandidateSource = count => arm.Nearest(vectorField, queryVector, count) };
            }

            knnArm = (KnnQuery)(_extras is null && _filters.Length == 0 ? knn : WithExtras(knn, applyFunction: false));
        }

        if (sparseArm is not null && (_extras is not null || _filters.Length != 0))
        {
            sparseArm = WithExtras(sparseArm, applyFunction: false);
        }

        var hybrid = new HybridQuery(textArm, knnArm)
        {
            Sparse = sparseArm,
            SparseWeight = sparseSpec?.Weight,
            Fusion = spec.Fusion,
            RankConstant = spec.RankConstant,
            SemanticRatio = spec.Ratio,
            Window = window,
        };
        return _extras?.Function is { } fn ? Wrap(hybrid, fn) : hybrid;
    }
}
