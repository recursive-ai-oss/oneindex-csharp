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
