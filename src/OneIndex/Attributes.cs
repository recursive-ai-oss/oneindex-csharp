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
/// Marks a <see cref="GeoPoint"/> property for geo search: radius and bounding box filters (<see cref="Geo"/>) and sorting by distance
/// (<c>OrderByDistance</c>). Without it a <see cref="GeoPoint"/> is only stored and returned with the document.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class GeoAttribute : Attribute
{
}
