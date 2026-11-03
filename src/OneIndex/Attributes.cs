namespace OneIndex;

/// <summary>Marks the property that identifies a document. It becomes the schema's keyword identifier field; every document needs a value.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class SearchIdAttribute : Attribute
{
}

/// <summary>Marks a property as analyzed text, searched by terms and ranked by BM25F.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class FullTextAttribute : Attribute
{
    /// <summary>The field's weight in scoring. Default 1; finite and above 0.</summary>
    public float Boost { get; set; } = 1f;

    /// <summary>The language of the text (for example <c>"en"</c>), which selects stemming and stop words. Default none: language-neutral analysis.</summary>
    public string? Language { get; set; }

    /// <summary>The name of a registered analyzer; overrides <see cref="Language"/>. Default none.</summary>
    public string? Analyzer { get; set; }

    /// <summary>
    /// Whether a cross-encoder reranker reads this field. Default <see langword="false"/>. When any field of a type sets it and the index has a reranker
    /// (<see cref="Vectors.SearchRerankers"/>), searches of plain text rerank their best hits without a call to <c>Rerank()</c>, reading these fields
    /// (in declaration order, joined by a space) as the passage; <c>WithoutRerank()</c> turns that off. See docs/rerank.md.
    /// </summary>
    public bool Rerank { get; set; }
}

/// <summary>Marks a property as a keyword: the whole value is matched exactly (tags, categories, codes).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class KeywordAttribute : Attribute
{
}

/// <summary>Marks a property that queries may filter on.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class FilterableAttribute : Attribute
{
}

/// <summary>Marks a property that results may be sorted by.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class SortableAttribute : Attribute
{
}

/// <summary>Marks a property as a facet: results report the counts of its values.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class FacetAttribute : Attribute
{
}

/// <summary>Marks a JSON property whose paths are indexed on the fly, for data whose shape is not known in advance.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class DynamicAttribute : Attribute
{
}

/// <summary>Marks a float array property as a dense vector for similarity search.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class VectorAttribute : Attribute
{
    /// <summary>The name of a text property to embed automatically when an embedder is configured. Default none: the application supplies the vector.</summary>
    public string? From { get; set; }

    /// <summary>The vector length. Required: 1 to 65,536. (A model-supplied length is added with automatic embedding.)</summary>
    public int Dimensions { get; set; }

    /// <summary>How closeness is measured. Default <see cref="Vectors.VectorMetric.Cosine"/>.</summary>
    public Vectors.VectorMetric Metric { get; set; }

    /// <summary>HNSW's M, the links per node above layer 0 (twice that on layer 0), 2 to 256. Default 16.</summary>
    public int M { get; set; } = 16;

    /// <summary>HNSW's efConstruction, the candidate list size while building, M to 4096. Default 100.</summary>
    public int EfConstruction { get; set; } = 100;

    /// <summary>A segment with fewer vectors than this is searched exactly and gets no graph. Default 1000.</summary>
    public int FlatThreshold { get; set; } = 1000;

    /// <summary>
    /// A compressed copy for graph search: <see cref="Vectors.VectorQuantization.Scalar8"/> (4 times smaller), <see cref="Vectors.VectorQuantization.Scalar4"/>
    /// (8 times) or the sign-bit <see cref="Vectors.VectorQuantization.OneBit"/> (32 times, cosine or dot product only). The full-precision
    /// vectors are kept and rerank the candidates. Default <see cref="Vectors.VectorQuantization.None"/>.
    /// </summary>
    public Vectors.VectorQuantization Quantization { get; set; }

    /// <summary>
    /// With a <see cref="Quantization"/>: the candidates kept by compressed distance, as a multiple of the results asked for, before the
    /// full-precision rerank, 1 to 100. Default 0, which means the codec's own default (2, 3 and 5 for the three codecs).
    /// </summary>
    public float Oversample { get; set; }

    /// <summary>
    /// The structure built over the vectors of each segment that has at least <see cref="FlatThreshold"/> of them: <see cref="Vectors.VectorIndexKind.Hnsw"/>
    /// (a graph, the default) or <see cref="Vectors.VectorIndexKind.Ivf"/> (clustered lists, docs/ivf.md). Smaller segments are searched exactly either way.
    /// </summary>
    public Vectors.VectorIndexKind Index { get; set; }

    /// <summary>With <see cref="Vectors.VectorIndexKind.Ivf"/>: the lists per segment, 1 to 4,096. Default 0, about the square root of the segment's vector count.</summary>
    public int IvfLists { get; set; }
}

/// <summary>
/// Marks an <see cref="Index.SparseVector"/> property as a learned sparse vector (token to weight), searched by dot product. With
/// <see cref="From"/> the index encodes the named text member automatically when a sparse encoder is configured (docs/sparse.md).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class SparseAttribute : Attribute
{
    /// <summary>The name of a text property to encode automatically. Default none: the application supplies the vector.</summary>
    public string? From { get; set; }
}

/// <summary>
/// Marks a <c>float[][]</c> property as a multi-vector: one vector per token of the document, scored against a query's token vectors by
/// MaxSim (late interaction, docs/multi-vector.md). It is stored with the document in a packed form; <see cref="Vectors.MaxSim"/> searches
/// it as a rerank of a first stage or as an exact scan of the documents a filter admits. Every row must have <see cref="Dimensions"/> values.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class MultiVectorAttribute : Attribute
{
    /// <summary>The length of every token vector, from 1 to 65,536. There is no default.</summary>
    public int Dimensions { get; set; }
}

/// <summary>
/// Marks a <see cref="GeoPoint"/> property for geo search: radius and bounding box filters (<see cref="Geo"/>) and sorting by distance
/// (<c>OrderByDistance</c>). Without it a <see cref="GeoPoint"/> is only stored and returned with the document.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class GeoAttribute : Attribute
{
}

/// <summary>
/// Configures typo tolerance for the document type's user-text searches, with the terms and defaults of Meilisearch's typo tolerance
/// settings. Without this attribute typo tolerance is on with the defaults. The settings are recorded in the index and apply to the next
/// query; they never change what is indexed.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TypoToleranceAttribute : Attribute
{
    /// <summary>Whether typo tolerance is on. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The shortest query word, in characters, that may have one typo. Default 5; at most <see cref="MinWordSizeForTwoTypos"/>.</summary>
    public int MinWordSizeForOneTypo { get; set; } = 5;

    /// <summary>The shortest query word, in characters, that may have two typos. Default 9; at most 255.</summary>
    public int MinWordSizeForTwoTypos { get; set; } = 9;

    /// <summary>Whether words made of digits only are matched without typos. Default <see langword="false"/>.</summary>
    public bool DisableOnNumbers { get; set; }

    /// <summary>Query words that never get typos, compared ignoring case. Default none.</summary>
    public string[]? DisableOnWords { get; set; }

    /// <summary>Names of properties whose matches never involve typos. Default none.</summary>
    public string[]? DisableOnFields { get; set; }
}

/// <summary>
/// Turns as-you-type matching of the last word of user text on or off (Meilisearch's prefix search). On by default: the last word also
/// matches every indexed word that begins with it, unless the text ends in a space.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PrefixSearchAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="enabled">Whether the last word matches as a prefix. Default <see langword="true"/>.</param>
    public PrefixSearchAttribute(bool enabled = true) => Enabled = enabled;

    /// <summary>Whether the last word matches as a prefix.</summary>
    public bool Enabled { get; }
}

/// <summary>A group of words that stand for each other in user text: a query for any member also matches the others. Repeat the attribute for more groups.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class SynonymsAttribute : Attribute
{
    /// <summary>Creates a group.</summary>
    /// <param name="words">Two or more words or phrases (analyzed like the text they are matched against).</param>
    public SynonymsAttribute(params string[] words) => Words = words;

    /// <summary>The members of the group.</summary>
    public string[] Words { get; }
}

/// <summary>A one-way synonym: a query for the word also matches its synonyms, but not the other way round. Repeat the attribute for more rules.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OneWaySynonymsAttribute : Attribute
{
    /// <summary>Creates a rule.</summary>
    /// <param name="word">The word or phrase that is widened.</param>
    /// <param name="synonyms">What it also matches.</param>
    public OneWaySynonymsAttribute(string word, params string[] synonyms)
    {
        Word = word;
        Synonyms = synonyms;
    }

    /// <summary>The word or phrase that is widened.</summary>
    public string Word { get; }

    /// <summary>What it also matches.</summary>
    public string[] Synonyms { get; }
}

/// <summary>Words that user text ignores, such as "the" or "of". Repeat the attribute to add more.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class StopWordsAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="words">The words to ignore.</param>
    public StopWordsAttribute(params string[] words) => Words = words;

    /// <summary>The words to ignore.</summary>
    public string[] Words { get; }
}

/// <summary>
/// Ranks user-text searches by bucket sort over ranking rules, with the names of Meilisearch's ranking rules (docs/ranking-rules.md), instead of by
/// BM25F score: <c>words</c>, <c>typo</c>, <c>proximity</c>, <c>attribute</c>, <c>sort</c>, <c>exactness</c>, and <c>property:asc</c> or <c>property:desc</c> for a
/// sortable property. Meilisearch's default order is words, typo, proximity, attribute, sort, exactness. Without this attribute the score ranks. The rules
/// are recorded in the index and apply to the next query; a search can replace them with <c>RankingRules(...)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RankingRulesAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="rules">The rules in order. A property rule names the index field (the property's name).</param>
    public RankingRulesAttribute(params string[] rules) => Rules = rules;

    /// <summary>The rules in order.</summary>
    public string[] Rules { get; }
}

/// <summary>
/// Sets the weight of the term-proximity bonus that user text with two or more words adds to its BM25F score (docs/scoring.md). The default is 0.1; 0
/// turns the bonus off. It is recorded in the index and applies to the next query.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ProximityAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="weight">0 to 4.</param>
    public ProximityAttribute(float weight) => Weight = weight;

    /// <summary>The weight, 0 to 4.</summary>
    public float Weight { get; }
}
