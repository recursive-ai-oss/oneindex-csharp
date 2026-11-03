using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.VectorData;

/// <summary>
/// A collection of records stored in a OneIndex index. Upserts are committed before they return. Vector search is nearest-neighbour search
/// with the filter applied before neighbours are chosen, so a full page comes back whenever enough records pass; hybrid search fuses the keywords
/// (BM25F over the full-text properties) with the vector by reciprocal rank fusion.
/// </summary>
/// <typeparam name="TKey">The key type: <see cref="string"/>, <see cref="Guid"/>, <see cref="int"/> or <see cref="long"/>.</typeparam>
/// <typeparam name="TRecord">The record class.</typeparam>
[RequiresDynamicCode("Records are read and written through reflection over their properties.")]
[RequiresUnreferencedCode("Records are read and written through reflection over their properties.")]
public sealed class OneIndexRecords<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>, IKeywordHybridSearchable<TRecord>
    where TKey : notnull
    where TRecord : class
{
    private readonly OneIndexVectorStore _store;
    private readonly RecordModel _model;
    private readonly RecordMapping<TRecord> _mapping;

    internal OneIndexRecords(OneIndexVectorStore store, string name, VectorStoreCollectionDefinition? definition)
    {
        OneIndexVectorStore.ValidateName(name);
        _store = store;
        Name = name;
        _model = RecordModel.Build(typeof(TRecord), definition);
        Type declaredKey = Nullable.GetUnderlyingType(_model.KeyProperty.Type) ?? _model.KeyProperty.Type;
        if (typeof(TKey) != declaredKey && typeof(TKey) != typeof(object))
        {
            throw new InvalidOperationException($"The key property of {typeof(TRecord).Name} is a {declaredKey.Name}, but the collection was asked for keys of type {typeof(TKey).Name}.");
        }
        _mapping = RecordMapping<TRecord>.Create(_model);
    }

    /// <inheritdoc />
    public override string Name { get; }

    private SearchIndex<TRecord> Index(bool create = false) => _store.GetIndex(Name, _mapping, create);

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.Exists(Name));
    }

    /// <inheritdoc />
    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Index(create: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
        => _store.EnsureCollectionDeletedAsync(Name, cancellationToken);

    /// <inheritdoc />
    public override Task<TRecord?> GetAsync(TKey key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Lookup(Index(), RecordMapping<TRecord>.KeyToString(key) ?? string.Empty, options?.IncludeVectors ?? false));
    }

    private TRecord? Lookup(SearchIndex<TRecord> index, string id, bool includeVectors)
    {
        SearchResults<TRecord> r = index.SearchBy(new TermQuery(_mapping.Schema.IdField!, id)).Take(1).Execute();
        return r.Count == 0 ? null : Shape(r[0].Document, includeVectors);
    }

    private TRecord Shape(TRecord record, bool includeVectors)
    {
        if (!includeVectors)
        {
            foreach (PropModel v in _model.VectorProperties)
            {
                Type t = Nullable.GetUnderlyingType(v.Type) ?? v.Type;
                if (t == typeof(ReadOnlyMemory<float>))
                {
                    v.Set(record, new ReadOnlyMemory<float>());
                }
                else
                {
                    v.Set(record, null);
                }
            }
        }

        return record;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<TRecord> GetAsync(IEnumerable<TKey> keys, RecordRetrievalOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        SearchIndex<TRecord> index = Index();
        foreach (TKey key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Lookup(index, RecordMapping<TRecord>.KeyToString(key) ?? string.Empty, options?.IncludeVectors ?? false) is { } record)
            {
                yield return record;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<TRecord> GetAsync(Expression<Func<TRecord, bool>> filter, int top, FilteredRecordRetrievalOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new FilteredRecordRetrievalOptions<TRecord>();
        SearchQuery<TRecord> q = Index().Search().Where(filter);
        bool first = true;
        foreach (FilteredRecordRetrievalOptions<TRecord>.OrderByDefinition.SortInfo s in options.OrderBy?.Invoke(new FilteredRecordRetrievalOptions<TRecord>.OrderByDefinition()).Values ?? [])
        {
            q = (first, s.Ascending) switch
            {
                (true, true) => q.OrderBy(s.PropertySelector),
                (true, false) => q.OrderByDescending(s.PropertySelector),
                (false, true) => q.ThenBy(s.PropertySelector),
                _ => q.ThenByDescending(s.PropertySelector),
            };
            first = false;
        }

        SearchResults<TRecord> r = q.Skip(options.Skip).Take(top).Execute(cancellationToken);
        foreach (SearchHit<TRecord> hit in r)
        {
            yield return Shape(hit.Document, options.IncludeVectors);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return DeleteAsync([key], cancellationToken);
    }

    /// <inheritdoc />
    public override Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        SearchIndex<TRecord> index = Index();
        bool any = false;
        foreach (TKey key in keys)
        {
            any |= index.Delete(RecordMapping<TRecord>.KeyToString(key) ?? string.Empty);
        }

        if (any)
        {
            index.Commit(cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return UpsertAsync([record], cancellationToken);
    }

    /// <inheritdoc />
    public override Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        SearchIndex<TRecord> index = Index(create: true);
        var batch = new List<TRecord>();
        foreach (TRecord record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            PropModel key = _model.KeyProperty;
            object? value = key.Get(record);
            if (key.IsAutoGenerated && (Nullable.GetUnderlyingType(key.Type) ?? key.Type) == typeof(Guid) && (value is null || (value is Guid g && g == Guid.Empty)))
            {
                key.Set(record, Guid.NewGuid());
            }
            else if (RecordMapping<TRecord>.KeyToString(value) is null)
            {
                throw new ArgumentException("A record needs a key.", nameof(records));
            }

            batch.Add(record);
        }

        if (batch.Count > 0)
        {
            index.Upsert(batch, cancellationToken);
            index.Commit(cancellationToken);
        }

        return Task.CompletedTask;
    }

    private (PropModel Property, float[] Vector) Query<TInput>(TInput searchValue, Expression<Func<TRecord, object?>>? selector, string what)
    {
        ArgumentNullException.ThrowIfNull(searchValue);
        PropModel property = _model.VectorProperties.Count switch
        {
            0 => throw new InvalidOperationException($"The record type {typeof(TRecord).Name} has no vector property to {what}."),
            1 => _model.VectorProperties[0],
            _ => selector is null ? throw new InvalidOperationException($"The '{typeof(TRecord).Name}' type has multiple vector properties, please specify your chosen property via options.") : _model.VectorProperties.FirstOrDefault(p => p.Name == PropertyName(selector)) ?? throw new InvalidOperationException("The options name a property that is not a vector property."),
        };
        float[] vector = RecordMapping<TRecord>.ToArray(searchValue as object) ?? throw new ArgumentException("The search value must be a vector (ReadOnlyMemory<float> or float[]); this collection does not generate embeddings.", nameof(searchValue));
        return (property, vector);
    }

    private static string PropertyName(Expression<Func<TRecord, object?>> selector)
    {
        Expression body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
        {
            body = u.Operand;
        }

        return body is MemberExpression m ? m.Member.Name : throw new ArgumentException("The property selector must name a property, like r => r.Embedding.");
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new VectorSearchOptions<TRecord>();
        (PropModel property, float[] vector) = Query(searchValue, options.VectorProperty, "search");
        int window = checked(top + options.Skip);
        SearchQuery<TRecord> q = Index().SearchVector(property.Name, vector, Math.Min(window, KnnQueryLimit));
        if (options.Filter is { } filter)
        {
            q = q.Where(filter);
        }

        SearchResults<TRecord> r = q.Take(window).Execute(cancellationToken);
        string? function = property.DistanceFunction;
        int skipped = 0;
        int returned = 0;
        foreach (SearchHit<TRecord> hit in r)
        {
            double score = ConvertScore(hit.Score, function);
            if (options.ScoreThreshold is { } threshold && !(IsDistance(function) ? score <= threshold : score >= threshold))
            {
                continue;
            }

            if (skipped < options.Skip)
            {
                skipped++;
                continue;
            }

            if (returned++ >= top)
            {
                break;
            }

            yield return new VectorSearchResult<TRecord>(Shape(hit.Document, options.IncludeVectors), score);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private const int KnnQueryLimit = 10_000;

    /// <inheritdoc />
    IAsyncEnumerable<VectorSearchResult<TRecord>> IKeywordHybridSearchable<TRecord>.HybridSearchAsync<TInput>(TInput searchValue, ICollection<string> keywords, int top, HybridSearchOptions<TRecord>? options, CancellationToken cancellationToken)
        => HybridCore(searchValue, keywords, top, options, cancellationToken);

    private async IAsyncEnumerable<VectorSearchResult<TRecord>> HybridCore<TInput>(TInput searchValue, ICollection<string> keywords, int top, HybridSearchOptions<TRecord>? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new HybridSearchOptions<TRecord>();
        (PropModel property, float[] vector) = Query(searchValue, options.VectorProperty, "search");
        // The keywords search one text property: the one named in the options, else the only full-text indexed one.
        List<PropModel> textProperties = [.. _model.DataProperties.Where(static p => p.IsFullTextIndexed)];
        PropModel text;
        if (options.AdditionalProperty is { } additional)
        {
            string name = PropertyName(additional);
            text = textProperties.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException($"The property '{name}' does not have full text indexing enabled.");
        }
        else
        {
            text = textProperties.Count switch
            {
                0 => throw new InvalidOperationException("Hybrid search needs a data property with IsFullTextIndexed = true."),
                1 => textProperties[0],
                _ => throw new InvalidOperationException($"The '{typeof(TRecord).Name}' type has multiple full text properties, please specify your chosen property via options."),
            };
        }

        int window = checked(top + options.Skip);
        SearchQuery<TRecord> q = Index().SearchStrict(KeywordQuery(text.Name, keywords)).Hybrid(property.Name, vector, window: Math.Max(100, window));
        if (options.Filter is { } filter)
        {
            q = q.Where(filter);
        }

        SearchResults<TRecord> r = q.Skip(options.Skip).Take(top).Execute(cancellationToken);
        foreach (SearchHit<TRecord> hit in r)
        {
            if (options.ScoreThreshold is { } threshold && hit.Score < threshold)
            {
                continue;
            }

            yield return new VectorSearchResult<TRecord>(Shape(hit.Document, options.IncludeVectors), hit.Score);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // Strict syntax: Field:(a OR b), each keyword one literal word (a backslash before every character the grammar uses).
    private static string KeywordQuery(string field, ICollection<string> keywords)
    {
        var sb = new System.Text.StringBuilder(field).Append(":(");
        bool first = true;
        foreach (string keyword in keywords)
        {
            foreach (string word in keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!first)
                {
                    sb.Append(" OR ");
                }

                first = false;
                foreach (char c in word)
                {
                    if (c is '(' or ')' or '"' or '^' or '~' or ':' or '[' or ']' or '{' or '}' or '\\' or '*' or '-' or '/' or '>' or '<')
                    {
                        sb.Append('\\');
                    }

                    sb.Append(c);
                }
            }
        }

        return first ? "NOT *" : sb.Append(')').ToString();
    }

    private static bool IsDistance(string? function) => function is DistanceFunction.CosineDistance or DistanceFunction.EuclideanDistance or DistanceFunction.EuclideanSquaredDistance or DistanceFunction.NegativeDotProductSimilarity or DistanceFunction.HammingDistance or DistanceFunction.ManhattanDistance;

    /// <summary>Turns the engine's similarity score (docs/vectors.md) back into the number the chosen distance function would report.</summary>
    private static double ConvertScore(float score, string? function)
    {
        double s = score;
        switch (function)
        {
            case null or "" or DistanceFunction.CosineSimilarity:
                return (2 * s) - 1;
            case DistanceFunction.CosineDistance:
                return 2 - (2 * s);
            case DistanceFunction.DotProductSimilarity:
                return Dot(s);
            case DistanceFunction.NegativeDotProductSimilarity:
                return -Dot(s);
            case DistanceFunction.EuclideanSquaredDistance:
                return (1 / s) - 1;
            case DistanceFunction.EuclideanDistance:
                return Math.Sqrt(Math.Max(0, (1 / s) - 1));
            default:
                return s;
        }
    }

    private static double Dot(double s) => s >= 1 ? s - 1 : 1 - (1 / s);

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(VectorStoreCollectionMetadata)
            ? new VectorStoreCollectionMetadata { VectorStoreSystemName = "recursiveai.oneindex", VectorStoreName = "OneIndex", CollectionName = Name }
            : serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // The store owns the open index; collection objects come and go.
        base.Dispose(disposing);
    }
}
