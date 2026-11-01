using System.Diagnostics.CodeAnalysis;
using OneIndex.Analysis;
using OneIndex.Index;

namespace OneIndex;

/// <summary>
/// One mapped member of a document type: how a property becomes a schema field. Instances are created by generated code from the
/// attributes on the property.
/// </summary>
public sealed class MappedMember
{
    /// <summary>Creates a mapped member.</summary>
    /// <param name="name">The property name, which is also the field name.</param>
    /// <param name="type">The field type.</param>
    /// <param name="multi">Whether the property holds several values (a collection of strings).</param>
    /// <param name="indexed">Whether queries can match the field: terms for text and keywords, ranges and equality for numbers, dates and booleans.</param>
    /// <param name="column">Whether the field is kept column-wise for filtering, sorting and facets.</param>
    /// <param name="facet">Whether results may report counts of the field's values.</param>
    /// <param name="fullText">Whether the field is analyzed text searched by user text.</param>
    /// <param name="isId">Whether the field identifies the document.</param>
    /// <param name="boost">The BM25F weight of a full-text field.</param>
    /// <param name="analyzer">The registered analyzer name of a full-text field, or <see langword="null"/> for the default.</param>
    /// <param name="dimensions">The length of a vector field; 0 for other fields.</param>
    /// <param name="metric">The metric of a vector field.</param>
    /// <param name="hnswM">HNSW's M for a vector field.</param>
    /// <param name="efConstruction">HNSW's efConstruction for a vector field.</param>
    /// <param name="flatThreshold">The segment size below which a vector field is searched exactly.</param>
    /// <param name="from">The text member a vector field is embedded from automatically, or <see langword="null"/>.</param>
    public MappedMember(string name, FieldType type, bool multi, bool indexed, bool column, bool facet, bool fullText, bool isId, float boost, string? analyzer,
        int dimensions = 0, Vectors.VectorMetric metric = Vectors.VectorMetric.Cosine, int hnswM = FieldDefinition.DefaultHnswM, int efConstruction = FieldDefinition.DefaultEfConstruction, int flatThreshold = FieldDefinition.DefaultFlatThreshold, string? from = null)
    {
        From = from;
        Dimensions = dimensions;
        Metric = metric;
        HnswM = hnswM;
        EfConstruction = efConstruction;
        FlatThreshold = flatThreshold;
        Name = name;
        Type = type;
        Multi = multi;
        Indexed = indexed;
        Column = column;
        Facet = facet;
        FullText = fullText;
        IsId = isId;
        Boost = boost;
        Analyzer = analyzer;
    }

    /// <summary>The property name, which is also the field name.</summary>
    public string Name { get; }

    /// <summary>The field type.</summary>
    public FieldType Type { get; }

    /// <summary>Whether the property holds several values.</summary>
    public bool Multi { get; }

    /// <summary>Whether queries can match the field.</summary>
    public bool Indexed { get; }

    /// <summary>Whether the field is kept column-wise.</summary>
    public bool Column { get; }

    /// <summary>Whether results may report counts of the field's values.</summary>
    public bool Facet { get; }

    /// <summary>Whether the field is analyzed text searched by user text.</summary>
    public bool FullText { get; }

    /// <summary>Whether the field identifies the document.</summary>
    public bool IsId { get; }

    /// <summary>The BM25F weight of a full-text field.</summary>
    public float Boost { get; }

    /// <summary>The analyzer name of a full-text field, or <see langword="null"/>.</summary>
    public string? Analyzer { get; }

    /// <summary>The text member a vector member is embedded from automatically, or null when the application supplies the vector.</summary>
    public string? From { get; }

    /// <summary>The length of a vector field; 0 otherwise.</summary>
    public int Dimensions { get; }

    /// <summary>The metric of a vector field.</summary>
    public Vectors.VectorMetric Metric { get; }

    /// <summary>HNSW's M for a vector field.</summary>
    public int HnswM { get; }

    /// <summary>HNSW's efConstruction for a vector field.</summary>
    public int EfConstruction { get; }

    /// <summary>The segment size below which a vector field is searched exactly.</summary>
    public int FlatThreshold { get; }

    internal FieldDefinition ToDefinition()
    {
        if (Type == FieldType.Text)
        {
            string analyzer = Analyzer ?? FieldDefinition.DefaultAnalyzer;
            if (!AnalyzerRegistry.TryCreate(analyzer, out _))
            {
                throw new InvalidOperationException($"Member '{Name}': no analyzer or language named '{analyzer}' is registered.");
            }

            return new FieldDefinition(Name, FieldType.Text) { Boost = Boost, Analyzer = analyzer, Multi = Multi, Indexed = Indexed, Column = Column };
        }

        if (Type == FieldType.Vector)
        {
            return new FieldDefinition(Name, FieldType.Vector) { Dimensions = Dimensions, Metric = Metric, HnswM = HnswM, EfConstruction = EfConstruction, FlatThreshold = FlatThreshold };
        }

        if (Type == FieldType.Sparse)
        {
            return new FieldDefinition(Name, FieldType.Sparse);
        }

        return new FieldDefinition(Name, Type) { Multi = Multi, Indexed = Indexed, Column = Column, Facet = Facet };
    }
}

/// <summary>
/// The generated mapping between a document type and an index document. The source generator writes one per type that carries
/// OneIndex attributes; applications do not derive from this class.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public abstract class SearchMapping<T>
    where T : class
{
    private readonly MappedMember[] _members;
    private readonly Dictionary<string, MappedMember> _byName = new(StringComparer.Ordinal);
    private Schema? _schema;

    /// <summary>Creates the mapping from the members the generator found.</summary>
    /// <param name="members">The mapped members in declaration order.</param>
    protected SearchMapping(MappedMember[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        _members = members;
        foreach (MappedMember m in members)
        {
            _byName[m.Name] = m;
        }
    }

    /// <summary>The mapped members in declaration order.</summary>
    public IReadOnlyList<MappedMember> Members => _members;

    /// <summary>The schema built from the members.</summary>
    /// <exception cref="InvalidOperationException">The type has no <see cref="SearchIdAttribute"/> member, or an analyzer is not registered.</exception>
    public Schema Schema => _schema ??= BuildSchema();

    /// <summary>Finds a member by property name.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="member">The member when found.</param>
    /// <returns><see langword="true"/> when the type maps a property of that name.</returns>
    public bool TryGetMember(string name, [NotNullWhen(true)] out MappedMember? member) => _byName.TryGetValue(name, out member);

    /// <summary>Reads the identifier of a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The identifier, or <see langword="null"/> when the type has none or the property is null.</returns>
    public abstract string? GetId(T value);

    /// <summary>Converts a value to an index document.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The document holding every mapped, non-null member.</returns>
    public abstract Document ToDocument(T value);

    /// <summary>Creates a value from an index document. Members the document lacks keep the type's constructor defaults.</summary>
    /// <param name="document">The document, as stored.</param>
    /// <returns>The value.</returns>
    public abstract T FromDocument(Document document);

    private Schema BuildSchema()
    {
        var builder = new SchemaBuilder();
        MappedMember? id = null;
        foreach (MappedMember m in _members)
        {
            if (m.IsId)
            {
                id = m;
            }
        }

        if (id is null)
        {
            throw new InvalidOperationException($"{typeof(T).Name} has no member marked [SearchId], so documents cannot be identified, replaced or deleted.");
        }

        foreach (MappedMember m in _members)
        {
            if (m.IsId)
            {
                builder.Id(m.Name);
            }
            else
            {
                builder.Add(m.ToDefinition());
                if (m.From is not null && m.Type == FieldType.Sparse)
                {
                    if (!_byName.TryGetValue(m.From, out MappedMember? sparseSource) || sparseSource.Type is not (FieldType.Text or FieldType.Keyword))
                    {
                        throw new InvalidOperationException($"Member '{m.Name}': [Sparse(From = \"{m.From}\")] must name a text or keyword member of {typeof(T).Name}.");
                    }

                    builder.Add(new FieldDefinition(AutoSparse.KeyFieldName(m.Name), FieldType.Bytes));
                }
                else if (m.From is not null)
                {
                    if (m.Type != FieldType.Vector || !_byName.TryGetValue(m.From, out MappedMember? source) || source.Type is not (FieldType.Text or FieldType.Keyword))
                    {
                        throw new InvalidOperationException($"Member '{m.Name}': [Vector(From = \"{m.From}\")] must name a text or keyword member of {typeof(T).Name}.");
                    }

                    // The cache key of the embedded text, kept in stored source beside the vector (docs/auto-embedding.md).
                    builder.Add(new FieldDefinition(AutoEmbedding.KeyFieldName(m.Name), FieldType.Bytes));
                }
            }
        }

        return builder.Build();
    }
}

/// <summary>Conversions the generated mappings share.</summary>
public static class SearchMapping
{
    /// <summary>Converts a date to the index's form: UTC, with <see cref="DateTimeKind.Unspecified"/> read as UTC. Precision is one millisecond.</summary>
    /// <param name="value">The date.</param>
    /// <returns>The same instant as a <see cref="DateTimeOffset"/>.</returns>
    public static DateTimeOffset ToDate(DateTime value)
        => new(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>Reads a stored date as a UTC <see cref="DateTime"/>.</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The date, in UTC, to the millisecond.</returns>
    public static DateTime FromDate(FieldValue value) => DateTimeOffset.FromUnixTimeMilliseconds(value.AsInt64()).UtcDateTime;
}

/// <summary>The mappings known to the process. Generated code registers each from a module initializer.</summary>
public static class SearchMappings
{
    /// <summary>Registers the mapping of a type.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="mapping">The mapping.</param>
    public static void Register<T>(SearchMapping<T> mapping)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(mapping);
        Holder<T>.Mapping = mapping;
    }

    /// <summary>Finds the mapping of a type.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="mapping">The mapping when the type has one.</param>
    /// <returns><see langword="true"/> when the generator produced a mapping for the type.</returns>
    public static bool TryGet<T>([NotNullWhen(true)] out SearchMapping<T>? mapping)
        where T : class
    {
        mapping = Holder<T>.Mapping;
        return mapping is not null;
    }

    /// <summary>Finds the mapping of a type or explains how to get one.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <returns>The mapping.</returns>
    /// <exception cref="InvalidOperationException">The type has no generated mapping.</exception>
    public static SearchMapping<T> Require<T>()
        where T : class
        => TryGet(out SearchMapping<T>? mapping)
            ? mapping
            : throw new InvalidOperationException($"{typeof(T).FullName} has no OneIndex mapping. Mark its properties with OneIndex attributes ([SearchId], [FullText], [Keyword], ...) in a project that references the OneIndex package, so that the source generator can write it.");

    private static class Holder<T>
        where T : class
    {
        public static SearchMapping<T>? Mapping;
    }
}
