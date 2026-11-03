using System.Globalization;
using Microsoft.Extensions.VectorData;
using System.Diagnostics.CodeAnalysis;
using OneIndex.Index;
using OneIndex.Vectors;

namespace OneIndex.VectorData;

/// <summary>
/// The mapping between a record class described by a <see cref="RecordModel"/> and an index document. Built when the collection is
/// created: the model comes from the abstraction's attributes or a collection definition, and property access goes through the model.
/// </summary>
[RequiresUnreferencedCode("Records are read and written through reflection over their properties.")]
[RequiresDynamicCode("Records are read and written through reflection over their properties.")]
internal sealed class RecordMapping<TRecord> : SearchMapping<TRecord>
    where TRecord : class
{
    private readonly RecordModel _model;
    private readonly Dictionary<string, PropModel> _byName = new(StringComparer.Ordinal);

    private RecordMapping(RecordModel model, MappedMember[] members)
        : base(members)
    {
        _model = model;
        foreach (PropModel p in model.Properties)
        {
            _byName[p.Name] = p;
        }
    }

    public static RecordMapping<TRecord> Create(RecordModel model)
    {
        var members = new List<MappedMember>();
        PropModel key = model.KeyProperty;
        members.Add(new MappedMember(key.Name, FieldType.Keyword, multi: false, indexed: true, column: true, facet: false, fullText: false, isId: true, boost: 1f, analyzer: null));
        foreach (PropModel d in model.DataProperties)
        {
            Type t = Nullable.GetUnderlyingType(d.Type) ?? d.Type;
            bool multi = IsStringCollection(t);
            // A Guid is kept as its text, a DateOnly as its day number and a TimeOnly as its ticks, so that equality and ordering filters work.
            FieldType kind = multi || t == typeof(string) || t == typeof(Guid)
                ? (d.IsFullTextIndexed ? FieldType.Text : FieldType.Keyword)
                : t == typeof(bool) ? FieldType.Bool
                : t == typeof(DateTime) || t == typeof(DateTimeOffset) ? FieldType.Date
                : IsIntegral(t) || t == typeof(DateOnly) || t == typeof(TimeOnly) ? FieldType.Integral
                : IsFractional(t) ? FieldType.Fractional
                : throw new NotSupportedException($"The property '{d.Name}' has the type {t.Name}, which OneIndex cannot store. Use a string, number, bool, date or a collection of strings.");
            bool text = kind == FieldType.Text;
            bool indexed = text || d.IsIndexed;
            members.Add(new MappedMember(d.Name, kind, multi, indexed, column: d.IsIndexed && !text, facet: false, fullText: text, isId: false, boost: 1f, analyzer: null));
        }

        foreach (PropModel v in model.VectorProperties)
        {
            VectorMetric metric = v.DistanceFunction switch
            {
                null or "" or DistanceFunction.CosineSimilarity or DistanceFunction.CosineDistance => VectorMetric.Cosine,
                DistanceFunction.DotProductSimilarity or DistanceFunction.NegativeDotProductSimilarity => VectorMetric.DotProduct,
                DistanceFunction.EuclideanDistance or DistanceFunction.EuclideanSquaredDistance => VectorMetric.Euclidean,
                _ => throw new NotSupportedException($"The distance function '{v.DistanceFunction}' of '{v.Name}' is not supported; use cosine, dot product or Euclidean."),
            };
            members.Add(new MappedMember(v.Name, FieldType.Vector, multi: false, indexed: true, column: false, facet: false, fullText: false, isId: false, boost: 1f, analyzer: null,
                dimensions: v.Dimensions, metric: metric));
        }

        return new RecordMapping<TRecord>(model, [.. members]);
    }

    public override string? GetId(TRecord value) => KeyToString(_model.KeyProperty.Get(value));

    internal static string? KeyToString(object? key) => key switch
    {
        null => null,
        string s => s.Length == 0 ? null : s,
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Keys of type {key.GetType().Name} are not supported; use string, Guid, int or long."),
    };

    internal static object ParseKey(string id, Type keyType)
    {
        Type t = Nullable.GetUnderlyingType(keyType) ?? keyType;
        if (t == typeof(string) || t == typeof(object))
        {
            return id;
        }

        if (t == typeof(Guid))
        {
            return Guid.Parse(id);
        }

        if (t == typeof(int))
        {
            return int.Parse(id, CultureInfo.InvariantCulture);
        }

        if (t == typeof(long))
        {
            return long.Parse(id, CultureInfo.InvariantCulture);
        }

        throw new NotSupportedException($"Keys of type {t.Name} are not supported; use string, Guid, int or long.");
    }

    public override Document ToDocument(TRecord value)
    {
        var doc = new Document();
        string id = GetId(value) ?? throw new ArgumentException("The record has no key.");
        doc.Add(_model.KeyProperty.Name, id);
        foreach (PropModel d in _model.DataProperties)
        {
            object? v = d.Get(value);
            if (v is null)
            {
                continue;
            }

            switch (v)
            {
                case string s:
                    doc.Add(d.Name, s);
                    break;
                case bool b:
                    doc.Add(d.Name, b);
                    break;
                case Guid g:
                    doc.Add(d.Name, g.ToString("D", CultureInfo.InvariantCulture));
                    break;
                case DateOnly day:
                    doc.Add(d.Name, (long)day.DayNumber);
                    break;
                case TimeOnly time:
                    doc.Add(d.Name, time.Ticks);
                    break;
                case DateTime dt:
                    doc.Add(d.Name, SearchMapping.ToDate(dt));
                    break;
                case DateTimeOffset dto:
                    doc.Add(d.Name, dto);
                    break;
                case float or double or decimal:
                    doc.Add(d.Name, Convert.ToDouble(v, CultureInfo.InvariantCulture));
                    break;
                case IEnumerable<string> many:
                    foreach (string item in many)
                    {
                        doc.Add(d.Name, item);
                    }

                    break;
                default:
                    doc.Add(d.Name, Convert.ToInt64(v, CultureInfo.InvariantCulture));
                    break;
            }
        }

        foreach (PropModel p in _model.VectorProperties)
        {
            float[]? vec = ToArray(p.Get(value));
            if (vec is not null)
            {
                doc.Add(p.Name, vec);
            }
        }

        return doc;
    }

    internal static float[]? ToArray(object? v) => v switch
    {
        null => null,
        float[] a => a,
        ReadOnlyMemory<float> m => m.IsEmpty ? null : m.ToArray(),
        Memory<float> m => m.IsEmpty ? null : m.ToArray(),
        _ => throw new NotSupportedException($"Vector properties of type {v.GetType().Name} are not supported; use ReadOnlyMemory<float> or float[]."),
    };

    public override TRecord FromDocument(Document document)
    {
        TRecord record = RecordModel.Create<TRecord>();
        Dictionary<string, List<string>>? many = null;
        foreach (DocumentField f in document.Fields)
        {
            if (!_byName.TryGetValue(f.Name, out PropModel? p))
            {
                continue;
            }

            if (p.Kind == PropKind.Key)
            {
                p.Set(record, ParseKey(f.Value.AsText(), p.Type));
                continue;
            }

            if (p.Kind == PropKind.Vector)
            {
                float[] arr = f.Value.AsVector().ToArray();
                p.Set(record, (Nullable.GetUnderlyingType(p.Type) ?? p.Type) == typeof(float[]) ? (object)arr : new ReadOnlyMemory<float>(arr));
                continue;
            }

            Type t = Nullable.GetUnderlyingType(p.Type) ?? p.Type;
            if (IsStringCollection(t))
            {
                many ??= new(StringComparer.Ordinal);
                if (!many.TryGetValue(f.Name, out List<string>? list))
                {
                    many[f.Name] = list = [];
                }

                list.Add(f.Value.AsText());
            }
            else if (t == typeof(string))
            {
                p.Set(record, f.Value.AsText());
            }
            else if (t == typeof(bool))
            {
                p.Set(record, f.Value.AsBool());
            }
            else if (t == typeof(Guid))
            {
                p.Set(record, Guid.Parse(f.Value.AsText()));
            }
            else if (t == typeof(DateOnly))
            {
                p.Set(record, DateOnly.FromDayNumber((int)f.Value.AsInt64()));
            }
            else if (t == typeof(TimeOnly))
            {
                p.Set(record, new TimeOnly(f.Value.AsInt64()));
            }
            else if (t == typeof(DateTimeOffset))
            {
                p.Set(record, DateTimeOffset.FromUnixTimeMilliseconds(f.Value.AsInt64()));
            }
            else if (t == typeof(DateTime))
            {
                p.Set(record, SearchMapping.FromDate(f.Value));
            }
            else if (IsFractional(t))
            {
                p.Set(record, Convert.ChangeType(f.Value.AsFloat64(), t, CultureInfo.InvariantCulture));
            }
            else
            {
                p.Set(record, Convert.ChangeType(f.Value.AsInt64(), t, CultureInfo.InvariantCulture));
            }
        }

        if (many is not null)
        {
            foreach ((string name, List<string> list) in many)
            {
                PropModel p = _byName[name];
                Type t = Nullable.GetUnderlyingType(p.Type) ?? p.Type;
                p.Set(record, t == typeof(string[]) ? list.ToArray() : list);
            }
        }

        return record;
    }

    private static bool IsStringCollection(Type t) => t == typeof(string[]) || t == typeof(List<string>) || t == typeof(IEnumerable<string>) || t == typeof(IReadOnlyList<string>) || t == typeof(IList<string>) || t == typeof(ICollection<string>);

    private static bool IsIntegral(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(uint) || t == typeof(ushort);

    private static bool IsFractional(Type t) => t == typeof(float) || t == typeof(double) || t == typeof(decimal);
}
