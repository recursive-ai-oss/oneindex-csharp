using System.Diagnostics.CodeAnalysis;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>The kinds of value a field holds.</summary>
public enum FieldType : byte
{
    /// <summary>Analyzed text, searched by terms.</summary>
    Text = 1,

    /// <summary>A whole string matched exactly: identifiers, tags, categories.</summary>
    Keyword = 2,

    /// <summary>A 64-bit signed integer.</summary>
    Integral = 3,

    /// <summary>A 64-bit floating-point number.</summary>
    Fractional = 4,

    /// <summary>A point in time, held as milliseconds since 1970-01-01T00:00:00Z.</summary>
    Date = 5,

    /// <summary>A true or false value.</summary>
    Bool = 6,

    /// <summary>A latitude and longitude in degrees.</summary>
    GeoPoint = 7,

    /// <summary>Opaque bytes; stored only.</summary>
    Bytes = 8,

    /// <summary>A dense vector of a fixed dimension (the vector codecs fill this in later).</summary>
    Vector = 9,

    /// <summary>A JSON value, stored whole and indexed by leaf path (docs/dynamic.md).</summary>
    Json = 10,
}

/// <summary>How much of each posting a text field records.</summary>
public enum IndexOptions : byte
{
    /// <summary>The field is not searchable by term.</summary>
    None = 0,

    /// <summary>Only which documents contain a term.</summary>
    Docs = 1,

    /// <summary>Documents and term frequencies (needed to score).</summary>
    Freqs = 2,

    /// <summary>Documents, frequencies and token positions (needed for phrases).</summary>
    Positions = 3,

    /// <summary>Documents, frequencies, positions and character offsets (needed for fast highlighting).</summary>
    Offsets = 4,
}

/// <summary>One declared field. Fields are immutable; build them with the initializer syntax or the <see cref="SchemaBuilder"/> shortcuts.</summary>
public sealed class FieldDefinition
{
    /// <summary>Declares a field with the defaults of its type (see the property remarks).</summary>
    /// <param name="name">The field name: 1 to 255 characters, no control characters.</param>
    /// <param name="type">The kind of value.</param>
    public FieldDefinition(string name, FieldType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Type = type;
        Indexed = type != FieldType.Bytes;
        Stored = true;
        IndexOptions = type == FieldType.Text ? IndexOptions.Positions : IndexOptions.None;
        Analyzer = type == FieldType.Text ? DefaultAnalyzer : null;
    }

    /// <summary>The analyzer name a text field uses unless it names another.</summary>
    public const string DefaultAnalyzer = "standard";

    /// <summary>The field name.</summary>
    public string Name { get; }

    /// <summary>The kind of value.</summary>
    public FieldType Type { get; }

    /// <summary>Whether the field is searchable (terms for text and keyword, points for numbers, the vector index for vectors). Default true except for bytes.</summary>
    public bool Indexed { get; init; }

    /// <summary>Whether the original value is kept in the stored source. Default true.</summary>
    public bool Stored { get; init; }

    /// <summary>Whether a per-document column is kept for filtering, sorting and aggregation. Default false.</summary>
    public bool Column { get; init; }

    /// <summary>Whether the field is a facet. Requires a column-capable type (keyword, numbers, date, bool). Default false.</summary>
    public bool Facet { get; init; }

    /// <summary>Whether a document may hold several values for the field. Default false.</summary>
    public bool Multi { get; init; }

    /// <summary>The scoring weight of the field; finite and above 0. Default 1.</summary>
    public float Boost { get; init; } = 1f;

    /// <summary>The analyzer name for a text field, resolved by the analyzer registry. Default <see cref="DefaultAnalyzer"/>; null for other types.</summary>
    public string? Analyzer { get; init; }

    /// <summary>What a text field records per posting. Default <see cref="Index.IndexOptions.Positions"/> for text, none otherwise.</summary>
    public IndexOptions IndexOptions { get; init; }

    /// <summary>The dimension of a vector field, 1 to <see cref="IndexLimits.MaxVectorDimensions"/>; 0 for other types.</summary>
    public int Dimensions { get; init; }

    /// <summary>
    /// For a field the engine derived from the leaf paths of a JSON field (docs/dynamic.md): the name of that JSON field; otherwise
    /// <see langword="null"/>. Derived fields are never declared by hand and are never stored, since the JSON value is.
    /// </summary>
    public string? DynamicParent { get; init; }
}

/// <summary>
/// The declared fields of an index: strict, ordered and immutable. A field's position is its stable field id. A schema
/// serializes to a deterministic byte string that goes into every commit file, so a schema read back is the one written.
/// </summary>
public sealed class Schema : IEquatable<Schema>
{
    /// <summary>The newest schema format version this build reads and writes.</summary>
    public const int FormatVersion = 2;

    /// <summary>The section that lists derived (dynamic) fields: pairs of field index and parent field index.</summary>
    private const int SectionDynamicFields = 1;

    private static readonly byte[] MagicBytes = "OIXS"u8.ToArray();
    private readonly string[] _jsonFields;
    private readonly Dictionary<string, int> _ids;
    private readonly byte[] _serialized;

    internal Schema(IReadOnlyList<FieldDefinition> fields, string? idField, IndexLimits limits)
    {
        Fields = [.. fields];
        IdField = idField;
        _ids = new Dictionary<string, int>(fields.Count, StringComparer.Ordinal);
        for (int i = 0; i < fields.Count; i++)
        {
            _ids.Add(fields[i].Name, i);
        }

        _jsonFields = [.. fields.Where(static f => f.Type == FieldType.Json && f.Indexed && f.DynamicParent is null).Select(static f => f.Name)];
        _serialized = Write(this);
        if (_serialized.Length > limits.MaxSchemaBytes)
        {
            throw new ArgumentException($"The serialized schema is {_serialized.Length} bytes, over the limit of {limits.MaxSchemaBytes}.");
        }
    }

    /// <summary>The empty schema.</summary>
    public static Schema Empty { get; } = new SchemaBuilder().Build();

    /// <summary>The fields in declaration order; a field's index is its field id.</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; }

    /// <summary>The name of the keyword field that identifies a document, or <see langword="null"/> if documents have no identifier.</summary>
    public string? IdField { get; }

    /// <summary>Finds a field by name.</summary>
    /// <param name="name">The field name (case-sensitive).</param>
    /// <param name="field">The field when found.</param>
    /// <returns><see langword="true"/> when the schema declares the field.</returns>
    public bool TryGetField(string name, [NotNullWhen(true)] out FieldDefinition? field)
    {
        if (_ids.TryGetValue(name, out int id))
        {
            field = Fields[id];
            return true;
        }

        field = null;
        return false;
    }

    /// <summary>
    /// Treats a name as a path inside an indexed JSON field: <c>meta.customer.name</c> for a JSON field <c>meta</c>. The result is a
    /// stand-in definition of type <see cref="FieldType.Json"/>; the physical fields it stands for depend on the kind of value compared
    /// (see docs/dynamic.md). Declared and derived fields are not paths: use <see cref="TryGetField"/> for those.
    /// </summary>
    /// <param name="name">The logical name, the JSON field name, a dot, then the path.</param>
    /// <param name="path">The stand-in when the name lies under an indexed JSON field of this schema.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool TryGetPath(string name, [NotNullWhen(true)] out FieldDefinition? path)
    {
        path = null;
        if (name is null || _ids.ContainsKey(name))
        {
            return false;
        }

        foreach (string json in _jsonFields)
        {
            if (name.Length > json.Length && name[json.Length] == '.' && name.StartsWith(json, StringComparison.Ordinal))
            {
                path = new FieldDefinition(name, FieldType.Json) { Stored = false, Multi = true };
                return true;
            }
        }

        return false;
    }

    /// <summary>The schema without the fields derived from JSON paths: what a type declares.</summary>
    /// <returns>This schema when it has no derived fields.</returns>
    public Schema WithoutDynamicFields()
    {
        if (!Fields.Any(static f => f.DynamicParent is not null))
        {
            return this;
        }

        var builder = new SchemaBuilder(IndexLimits.Default) { IdFieldName = IdField };
        foreach (FieldDefinition f in Fields.Where(static f => f.DynamicParent is null))
        {
            builder.Add(f);
        }

        return builder.Build();
    }

    /// <summary>This schema with more derived fields appended (field ids of the existing fields do not change).</summary>
    /// <param name="added">The derived fields, each with <see cref="FieldDefinition.DynamicParent"/> set.</param>
    /// <param name="limits">The limits the result must respect.</param>
    /// <returns>The extended schema.</returns>
    internal Schema WithAppended(IEnumerable<FieldDefinition> added, IndexLimits limits)
    {
        // Declared fields keep their positions; derived fields follow in name order, so the schema depends on which paths exist and
        // not on the order they were met in.
        var builder = new SchemaBuilder(limits) { IdFieldName = IdField };
        foreach (FieldDefinition f in Fields.Where(static f => f.DynamicParent is null))
        {
            builder.Add(f);
        }

        foreach (FieldDefinition f in Fields.Where(static f => f.DynamicParent is not null).Concat(added).OrderBy(static f => f.Name, StringComparer.Ordinal))
        {
            builder.Add(f);
        }

        return builder.Build();
    }

    /// <summary>The field id (position) of a field, or -1 if the schema does not declare it.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The id, or -1.</returns>
    public int GetFieldId(string name) => _ids.TryGetValue(name, out int id) ? id : -1;

    /// <summary>Checks a document against the schema.</summary>
    /// <param name="document">The document.</param>
    /// <exception cref="ArgumentException">A field is unknown, has the wrong type, repeats without being multi-valued, has the wrong vector dimension, or the identifier is missing. The message names the field.</exception>
    public void Validate(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Dictionary<string, int>? counts = null;
        foreach (DocumentField f in document.Fields)
        {
            if (!TryGetField(f.Name, out FieldDefinition? def))
            {
                throw new ArgumentException($"Field '{f.Name}' is not in the schema.", nameof(document));
            }

            if (!Accepts(def.Type, f.Value.Kind))
            {
                throw new ArgumentException($"Field '{f.Name}' is {def.Type} but the value is {f.Value.Kind}.", nameof(document));
            }

            if (def.Type == FieldType.Json && f.Value.Kind == FieldValueKind.Json)
            {
                DynamicFields.EnsureReadable(f.Value.AsJson(), f.Name);
            }

            if (def.Type == FieldType.Vector && f.Value.AsVector().Length != def.Dimensions)
            {
                throw new ArgumentException($"Field '{f.Name}' has {f.Value.AsVector().Length} dimensions but the schema declares {def.Dimensions}.", nameof(document));
            }

            if (!def.Multi)
            {
                counts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                counts[f.Name] = counts.GetValueOrDefault(f.Name) + 1;
                if (counts[f.Name] > 1)
                {
                    throw new ArgumentException($"Field '{f.Name}' is not multi-valued but the document gives it several values.", nameof(document));
                }
            }
        }

        if (IdField is not null && !document.Fields.Any(f => f.Name == IdField))
        {
            throw new ArgumentException($"The document has no value for the identifier field '{IdField}'.", nameof(document));
        }
    }

    /// <summary>Serializes the schema deterministically (see docs/commit-format.md).</summary>
    /// <returns>A copy of the serialized form; equal schemas give equal bytes.</returns>
    public byte[] Serialize() => (byte[])_serialized.Clone();

    /// <summary>Reads a serialized schema.</summary>
    /// <param name="data">The bytes from <see cref="Serialize"/>.</param>
    /// <param name="limits">The limits to enforce; default <see cref="IndexLimits.Default"/>.</param>
    /// <returns>The schema.</returns>
    /// <exception cref="CorruptIndexException">The bytes are not a valid schema; the file name in the message is <c>schema</c>.</exception>
    /// <exception cref="UnsupportedFormatException">The schema was written by a newer format.</exception>
    public static Schema Deserialize(ReadOnlySpan<byte> data, IndexLimits? limits = null)
    {
        limits ??= IndexLimits.Default;
        if (data.Length > limits.MaxSchemaBytes)
        {
            throw new CorruptIndexException("schema", $"{data.Length} bytes exceeds the schema limit of {limits.MaxSchemaBytes}");
        }

        try
        {
            return Read(data, limits);
        }
        catch (MalformedInputException e)
        {
            throw new CorruptIndexException("schema", e.Message, e);
        }
        catch (ArgumentException e)
        {
            throw new CorruptIndexException("schema", e.Message, e);
        }
    }

    /// <summary>Reads a serialized schema without throwing for malformed input.</summary>
    /// <param name="data">The bytes from <see cref="Serialize"/>.</param>
    /// <param name="limits">The limits to enforce; <see langword="null"/> for the defaults.</param>
    /// <param name="schema">The schema when the method returns <see langword="true"/>.</param>
    /// <param name="error">The reason when it returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the bytes are a valid, supported schema.</returns>
    public static bool TryDeserialize(ReadOnlySpan<byte> data, IndexLimits? limits, [NotNullWhen(true)] out Schema? schema, [NotNullWhen(false)] out string? error)
    {
        try
        {
            schema = Deserialize(data, limits);
            error = null;
            return true;
        }
        catch (CorruptIndexException e)
        {
            schema = null;
            error = e.Message;
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Equals(Schema? other) => other is not null && _serialized.AsSpan().SequenceEqual(other._serialized);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Schema);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // For in-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.AddBytes(_serialized);
        return hash.ToHashCode();
    }

    private static bool Accepts(FieldType type, FieldValueKind kind) => type switch
    {
        FieldType.Text or FieldType.Keyword => kind == FieldValueKind.Text,
        FieldType.Integral => kind == FieldValueKind.Integral,
        FieldType.Fractional => kind is FieldValueKind.Fractional or FieldValueKind.Integral,
        FieldType.Date => kind == FieldValueKind.Date,
        FieldType.Bool => kind == FieldValueKind.Bool,
        FieldType.GeoPoint => kind == FieldValueKind.GeoPoint,
        FieldType.Bytes => kind == FieldValueKind.Bytes,
        FieldType.Vector => kind == FieldValueKind.Vector,
        FieldType.Json => kind == FieldValueKind.Json,
        _ => false,
    };

    private static byte[] Write(Schema schema)
    {
        var sink = new ByteSink();
        foreach (byte b in MagicBytes)
        {
            sink.WriteByte(b);
        }

        bool hasDerived = schema.Fields.Any(static f => f.DynamicParent is not null);
        sink.WriteUInt32(hasDerived ? 2u : 1u);
        sink.WriteVarUInt((ulong)schema.Fields.Count);
        foreach (FieldDefinition f in schema.Fields)
        {
            sink.WriteString(f.Name);
            sink.WriteByte((byte)f.Type);
            sink.WriteByte((byte)((f.Indexed ? 1 : 0) | (f.Stored ? 2 : 0) | (f.Column ? 4 : 0) | (f.Facet ? 8 : 0) | (f.Multi ? 16 : 0)));
            sink.WriteUInt32(BitConverter.SingleToUInt32Bits(f.Boost));
            sink.WriteString(f.Analyzer ?? string.Empty);
            sink.WriteByte((byte)f.IndexOptions);
            sink.WriteVarUInt((ulong)f.Dimensions);
        }

        sink.WriteString(schema.IdField ?? string.Empty);
        if (hasDerived)
        {
            // Version 2 appends sections: a count, then for each an id (ascending) and its bytes.
            var section = new ByteSink();
            var pairs = new List<(int Field, int Parent)>();
            for (int i = 0; i < schema.Fields.Count; i++)
            {
                if (schema.Fields[i].DynamicParent is { } parent)
                {
                    pairs.Add((i, schema.GetFieldId(parent)));
                }
            }

            section.WriteVarUInt((ulong)pairs.Count);
            foreach ((int field, int parent) in pairs)
            {
                section.WriteVarUInt((ulong)field);
                section.WriteVarUInt((ulong)parent);
            }

            sink.WriteVarUInt(1);
            sink.WriteVarUInt(SectionDynamicFields);
            sink.WriteBytes(section.ToArray());
        }

        return sink.ToArray();
    }

    private static Schema Read(ReadOnlySpan<byte> data, IndexLimits limits)
    {
        var src = new ByteSource(data);
        foreach (byte expected in MagicBytes)
        {
            if (src.ReadByte() != expected)
            {
                throw new MalformedInputException("not a schema (wrong magic)");
            }
        }

        uint version = src.ReadUInt32();
        if (version == 0 || version > FormatVersion)
        {
            throw new UnsupportedFormatException("schema", $"schema format version {version} is not supported (newest understood: {FormatVersion})");
        }

        int count = src.ReadCount(limits.MaxFields, "field count");
        var defs = new List<(string Name, byte Type, byte Flags, float Boost, string Analyzer, byte Options, int Dimensions)>(Math.Min(count, 1024));
        for (int i = 0; i < count; i++)
        {
            string name = src.ReadString(limits.MaxStringBytes, "field name");
            byte type = src.ReadByte();
            byte flags = src.ReadByte();
            float boost = BitConverter.UInt32BitsToSingle(src.ReadUInt32());
            string analyzer = src.ReadString(limits.MaxStringBytes, "analyzer name");
            byte options = src.ReadByte();
            int dimensions = src.ReadCount(limits.MaxVectorDimensions, "vector dimensions");
            if (!Enum.IsDefined((FieldType)type) || options > (byte)IndexOptions.Offsets || (flags & ~31) != 0)
            {
                throw new MalformedInputException($"field '{name}' has an unknown type, option or flag");
            }

            defs.Add((name, type, flags, boost, analyzer, options, dimensions));
        }

        string id = src.ReadString(limits.MaxStringBytes, "identifier field");
        int[] parents = new int[count];
        Array.Fill(parents, -1);
        bool skippedSection = false;
        if (version >= 2)
        {
            int sections = src.ReadCount(16, "section count");
            int previous = 0;
            for (int k = 0; k < sections; k++)
            {
                ulong sectionId = src.ReadVarUInt();
                if (sectionId <= (ulong)previous || sectionId > int.MaxValue)
                {
                    throw new MalformedInputException("schema sections are not in strictly ascending order");
                }

                previous = (int)sectionId;
                ReadOnlySpan<byte> payload = src.ReadBytes(limits.MaxSchemaBytes, "schema section");
                if (sectionId == SectionDynamicFields)
                {
                    var inner = new ByteSource(payload);
                    int pairs = inner.ReadCount(count, "dynamic field count");
                    int last = -1;
                    for (int p = 0; p < pairs; p++)
                    {
                        int field = inner.ReadCount(count - 1, "dynamic field index");
                        int parent = inner.ReadCount(count - 1, "dynamic parent index");
                        if (field <= last || parent >= field)
                        {
                            throw new MalformedInputException("dynamic field entries are not in order or name a later parent");
                        }

                        parents[field] = parent;
                        last = field;
                    }

                    if (inner.Remaining != 0 || pairs == 0)
                    {
                        throw new MalformedInputException("the dynamic field section is empty or has trailing bytes");
                    }
                }
                else
                {
                    // A section from a newer build (a settings record, say) is skipped: sections only add information, and the
                    // fields above are complete without it. The payload was already length- and limit-checked by ReadBytes.
                    skippedSection = true;
                }
            }
        }

        if (src.Remaining != 0)
        {
            throw new MalformedInputException($"{src.Remaining} unexpected bytes follow the schema");
        }

        if (version >= 2 && !skippedSection && Array.TrueForAll(parents, static p => p < 0))
        {
            throw new MalformedInputException("a version 2 schema lists no dynamic field");
        }

        var builder = new SchemaBuilder(limits);
        for (int i = 0; i < defs.Count; i++)
        {
            (string name, byte type, byte flags, float boost, string analyzer, byte options, int dimensions) = defs[i];
            builder.Add(new FieldDefinition(name, (FieldType)type)
            {
                Indexed = (flags & 1) != 0,
                Stored = (flags & 2) != 0,
                Column = (flags & 4) != 0,
                Facet = (flags & 8) != 0,
                Multi = (flags & 16) != 0,
                Boost = boost,
                Analyzer = analyzer.Length == 0 ? null : analyzer,
                IndexOptions = (IndexOptions)options,
                Dimensions = dimensions,
                DynamicParent = parents[i] >= 0 ? defs[parents[i]].Name : null,
            });
        }

        builder.IdFieldName = id.Length == 0 ? null : id;
        return builder.Build();
    }
}

/// <summary>Builds a <see cref="Schema"/>, validating it as a whole.</summary>
public sealed class SchemaBuilder
{
    private readonly List<FieldDefinition> _fields = [];
    private readonly IndexLimits _limits;

    /// <summary>Starts an empty schema with the default limits.</summary>
    public SchemaBuilder()
        : this(IndexLimits.Default)
    {
    }

    /// <summary>Starts an empty schema.</summary>
    /// <param name="limits">The limits the schema must respect.</param>
    public SchemaBuilder(IndexLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    internal string? IdFieldName { get; set; }

    /// <summary>Adds a field with explicit options.</summary>
    /// <param name="field">The field.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Add(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        _fields.Add(field);
        return this;
    }

    /// <summary>Adds the identifier field: a keyword that is indexed, stored and kept in a column. A schema has at most one.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Id(string name)
    {
        if (IdFieldName is not null)
        {
            throw new InvalidOperationException($"The schema already has the identifier field '{IdFieldName}'.");
        }

        IdFieldName = name;
        return Add(new FieldDefinition(name, FieldType.Keyword) { Column = true });
    }

    /// <summary>Adds an analyzed text field with default options.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Text(string name) => Add(new FieldDefinition(name, FieldType.Text));

    /// <summary>Adds a keyword field with default options.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Keyword(string name) => Add(new FieldDefinition(name, FieldType.Keyword));

    /// <summary>Adds a 64-bit integer field kept in a column.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Integral(string name) => Add(new FieldDefinition(name, FieldType.Integral) { Column = true });

    /// <summary>Adds a 64-bit floating-point field kept in a column.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Fractional(string name) => Add(new FieldDefinition(name, FieldType.Fractional) { Column = true });

    /// <summary>Adds a date field kept in a column.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Date(string name) => Add(new FieldDefinition(name, FieldType.Date) { Column = true });

    /// <summary>Adds a boolean field kept in a column.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Bool(string name) => Add(new FieldDefinition(name, FieldType.Bool) { Column = true });

    /// <summary>Adds a geo point field, indexed for radius and bounding box queries and kept in a column for distance sorting.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder GeoPoint(string name) => Add(new FieldDefinition(name, FieldType.GeoPoint) { Column = true });

    /// <summary>Adds a stored-only bytes field.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Bytes(string name) => Add(new FieldDefinition(name, FieldType.Bytes));

    /// <summary>Adds a vector field.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="dimensions">The vector length.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Vector(string name, int dimensions) => Add(new FieldDefinition(name, FieldType.Vector) { Dimensions = dimensions });

    /// <summary>Adds a dynamic JSON field.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>This builder.</returns>
    public SchemaBuilder Json(string name) => Add(new FieldDefinition(name, FieldType.Json));

    /// <summary>Validates and builds the schema.</summary>
    /// <returns>The immutable schema.</returns>
    /// <exception cref="ArgumentException">A field is invalid; the message names it.</exception>
    public Schema Build()
    {
        if (_fields.Count > _limits.MaxFields)
        {
            throw new ArgumentException($"A schema may declare at most {_limits.MaxFields} fields.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldDefinition f in _fields)
        {
            Validate(f);
            if (!names.Add(f.Name))
            {
                throw new ArgumentException($"Field '{f.Name}' is declared twice.");
            }
        }

        if (IdFieldName is not null)
        {
            FieldDefinition? id = _fields.Find(f => f.Name == IdFieldName);
            if (id is null || id.Type != FieldType.Keyword || !id.Stored || id.Multi)
            {
                throw new ArgumentException($"The identifier field '{IdFieldName}' must be a stored, single-valued keyword field.");
            }
        }

        return new Schema(_fields, IdFieldName, _limits);
    }

    private void Validate(FieldDefinition f)
    {
        string n = f.Name;
        if (n.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(n) > _limits.MaxStringBytes || n.Any(char.IsControl))
        {
            throw new ArgumentException($"Field name '{n}' must be 1 or more characters, within the length limit, with no control characters.");
        }

        if (f.DynamicParent is { } parent)
        {
            FieldDefinition? owner = _fields.Find(p => p.Name == parent);
            if (owner is null || owner.Type != FieldType.Json || owner.DynamicParent is not null || !n.StartsWith(parent + ".", StringComparison.Ordinal)
                || f.Type is not (FieldType.Keyword or FieldType.Fractional or FieldType.Date or FieldType.Bool) || f.Stored)
            {
                throw new ArgumentException($"Field '{n}' claims to be derived from '{parent}', which is not an earlier JSON field it is named under, or it is not an unstored keyword, number, date or bool field.");
            }
        }

        if (!float.IsFinite(f.Boost) || f.Boost <= 0)
        {
            throw new ArgumentException($"Field '{n}' has boost {f.Boost}; it must be finite and above 0.");
        }

        bool textual = f.Type == FieldType.Text;
        if (!textual && (f.Analyzer is not null || f.IndexOptions != IndexOptions.None))
        {
            throw new ArgumentException($"Field '{n}' is {f.Type}; analyzers and index options apply to text fields only.");
        }

        if (textual && (f.Analyzer is null || f.Analyzer.Length == 0))
        {
            throw new ArgumentException($"Text field '{n}' needs an analyzer name.");
        }

        if (textual && f.Indexed && f.IndexOptions == IndexOptions.None)
        {
            throw new ArgumentException($"Text field '{n}' is indexed but its index options are None.");
        }

        bool columnable = f.Type is FieldType.Keyword or FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool or FieldType.GeoPoint;
        if ((f.Column || f.Facet) && !columnable)
        {
            throw new ArgumentException($"Field '{n}' is {f.Type}, which cannot be a column or a facet.");
        }

        if (f.Facet && f.Type == FieldType.GeoPoint)
        {
            throw new ArgumentException($"Field '{n}' is a geo point, which cannot be a facet.");
        }

        if (f.Type == FieldType.Bytes && f.Indexed)
        {
            throw new ArgumentException($"Field '{n}' holds bytes, which cannot be indexed.");
        }

        if (f.Type == FieldType.Vector)
        {
            if (f.Dimensions < 1 || f.Dimensions > _limits.MaxVectorDimensions)
            {
                throw new ArgumentException($"Vector field '{n}' needs 1 to {_limits.MaxVectorDimensions} dimensions, not {f.Dimensions}.");
            }
        }
        else if (f.Dimensions != 0)
        {
            throw new ArgumentException($"Field '{n}' is {f.Type}; dimensions apply to vector fields only.");
        }
    }
}
