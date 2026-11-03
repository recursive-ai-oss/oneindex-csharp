using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace OneIndex.EntityFrameworkCore;

/// <summary>Adds <c>Search</c> to a <see cref="DbSet{TEntity}"/>.</summary>
public static class OneIndexDbSetExtensions
{
    /// <summary>
    /// Starts a search over a registered entity type. The index finds and ranks; the database supplies the entities, so every result is a
    /// current row and never a stale copy. See docs/entity-framework.md.
    /// </summary>
    /// <typeparam name="T">The entity type, registered with <see cref="OneIndexOptions.Index{T}"/>.</typeparam>
    /// <param name="set">The set.</param>
    /// <param name="text">What the user typed (see docs/user-text.md); <see langword="null"/> or blank matches every document.</param>
    /// <returns>The search, which runs on <c>ToListAsync</c> or <c>ToList</c>.</returns>
    /// <exception cref="InvalidOperationException">The context was not configured with <c>UseOneIndex</c>, or the type is not registered.</exception>
    public static EfSearchQuery<T> Search<T>(this DbSet<T> set, string? text = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        DbContext context = ((IInfrastructure<IServiceProvider>)set).GetService<ICurrentDbContext>().Context;
        OneIndexRuntime runtime = OneIndexRuntime.For(context);
        _ = runtime.Entity<T>();
        return new EfSearchQuery<T>(runtime, set, text, [], [], [], 20, 0, false, false);
    }
}

/// <summary>
/// A search over the index joined with an EF query. Predicates that OneIndex can translate (<c>[Keyword]</c>, <c>[Filterable]</c> and
/// <c>[Facet]</c> members, see docs/linq.md) run in the index; the others run in the database over the candidates the index returns. The
/// candidates are fetched in growing windows until the page is full or the index is exhausted, and the results are in the index's order.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class EfSearchQuery<T>
    where T : class
{
    private readonly OneIndexRuntime _runtime;
    private readonly DbSet<T> _set;
    private readonly string? _text;
    private readonly List<Expression<Func<T, bool>>> _indexed;
    private readonly List<Expression<Func<T, bool>>> _database;
    private readonly List<Func<SearchQuery<T>, SearchQuery<T>>> _refine;
    private readonly int _take;
    private readonly int _skip;
    private readonly bool _after;
    private readonly bool _track;

    internal EfSearchQuery(OneIndexRuntime runtime, DbSet<T> set, string? text, List<Expression<Func<T, bool>>> indexed, List<Expression<Func<T, bool>>> database,
        List<Func<SearchQuery<T>, SearchQuery<T>>> refine, int take, int skip, bool after, bool track)
    {
        _runtime = runtime;
        _set = set;
        _text = text;
        _indexed = indexed;
        _database = database;
        _refine = refine;
        _take = take;
        _skip = skip;
        _after = after;
        _track = track;
    }

    /// <summary>
    /// Narrows the search with a predicate. Each <c>&amp;&amp;</c>-joined part is tried in the index first; a part OneIndex cannot translate
    /// (an unindexed member, a navigation, a method EF knows) is given to the database instead. Parts evaluated in the index see the index's
    /// copy of the entity; parts evaluated in the database see the row.
    /// </summary>
    /// <param name="predicate">The predicate.</param>
    /// <returns>The narrowed search.</returns>
    public EfSearchQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var indexed = new List<Expression<Func<T, bool>>>(_indexed);
        var database = new List<Expression<Func<T, bool>>>(_database);
        SearchIndex<T> index = _runtime.Index<T>();
        foreach (Expression part in Conjuncts(predicate.Body))
        {
            Expression<Func<T, bool>> lambda = Expression.Lambda<Func<T, bool>>(part, predicate.Parameters);
            try
            {
                _ = index.Search().Where(lambda);
                indexed.Add(lambda);
            }
            catch (NotSupportedException)
            {
                database.Add(lambda);
            }
        }

        return new EfSearchQuery<T>(_runtime, _set, _text, indexed, database, _refine, _take, _skip, _after, _track);
    }

    private static IEnumerable<Expression> Conjuncts(Expression e)
    {
        if (e is BinaryExpression { NodeType: ExpressionType.AndAlso } b)
        {
            foreach (Expression l in Conjuncts(b.Left))
            {
                yield return l;
            }

            foreach (Expression r in Conjuncts(b.Right))
            {
                yield return r;
            }
        }
        else
        {
            yield return e;
        }
    }

    /// <summary>Applies index-side stages to the underlying search: sorting, facets, ranking, highlighting. Hits come back in the order the search defines.</summary>
    /// <param name="refine">Receives the search and returns it with the stages added.</param>
    /// <returns>The refined search.</returns>
    public EfSearchQuery<T> Configure(Func<SearchQuery<T>, SearchQuery<T>> refine)
    {
        ArgumentNullException.ThrowIfNull(refine);
        return new EfSearchQuery<T>(_runtime, _set, _text, _indexed, _database, [.. _refine, refine], _take, _skip, _after, _track);
    }

    /// <summary>The page size (default 20). The page is full unless fewer entities match.</summary>
    /// <param name="count">At least 1.</param>
    /// <returns>The search.</returns>
    public EfSearchQuery<T> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return new EfSearchQuery<T>(_runtime, _set, _text, _indexed, _database, _refine, count, _skip, _after, _track);
    }

    /// <summary>The number of matching entities to skip (default 0). Skip plus take may not exceed 10,000 candidates read from the index.</summary>
    /// <param name="count">At least 0.</param>
    /// <returns>The search.</returns>
    public EfSearchQuery<T> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new EfSearchQuery<T>(_runtime, _set, _text, _indexed, _database, _refine, _take, count, _after, _track);
    }

    /// <summary>Before searching, applies every outbox row written so far to the index (read-your-writes). Costs one catch-up per search; off by default.</summary>
    /// <returns>The search.</returns>
    public EfSearchQuery<T> AfterPendingChanges()
        => new(_runtime, _set, _text, _indexed, _database, _refine, _take, _skip, true, _track);

    /// <summary>Tracks the returned entities in the context like an ordinary query (the default does not).</summary>
    /// <returns>The search.</returns>
    public EfSearchQuery<T> AsTracking()
        => new(_runtime, _set, _text, _indexed, _database, _refine, _take, _skip, _after, true);

    private SearchQuery<T> BaseQuery()
    {
        SearchQuery<T> q = _runtime.Index<T>().Search(_text);
        foreach (Expression<Func<T, bool>> p in _indexed)
        {
            q = q.Where(p);
        }

        foreach (Func<SearchQuery<T>, SearchQuery<T>> r in _refine)
        {
            q = r(q);
        }

        return q;
    }

    private DbContext Context => ((IInfrastructure<IServiceProvider>)_set).GetService<ICurrentDbContext>().Context;

    /// <summary>Runs the search.</summary>
    /// <param name="cancellationToken">Cancels between windows.</param>
    /// <returns>Up to <c>Take</c> entities, best first.</returns>
    public async Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        if (_after)
        {
            await _runtime.CatchUpAsync<T>(cancellationToken).ConfigureAwait(false);
        }

        SearchQuery<T> baseQuery = BaseQuery();
        IndexedEntity<T> entity = _runtime.Entity<T>();
        int need = _skip + _take;
        var matched = new List<T>();
        int offset = 0;
        int window = Math.Min(Math.Max(need * 2, 32), 1000);
        while (matched.Count < need && offset < SearchQuery<T>.MaxResultWindow)
        {
            int size = Math.Min(window, SearchQuery<T>.MaxResultWindow - offset);
            SearchResults<T> hits = await baseQuery.Skip(offset).Take(size).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                break;
            }

            List<string> ids = [.. hits.Select(h => h.Id)];
            List<T> rows = await IndexedEntity<T>.LoadAsync(Context, ids, entity.IdName, _database, _track, cancellationToken).ConfigureAwait(false);
            AppendInOrder(matched, ids, rows, entity);
            offset += hits.Count;
            if (hits.Count < size)
            {
                break;
            }

            window = Math.Min(window * 2, 1000);
        }

        return Page(matched);
    }

    /// <summary>The synchronous twin of <see cref="ToListAsync(CancellationToken)"/>.</summary>
    /// <returns>Up to <c>Take</c> entities, best first.</returns>
    public List<T> ToList()
    {
        if (_after)
        {
            _runtime.CatchUp();
        }

        SearchQuery<T> baseQuery = BaseQuery();
        IndexedEntity<T> entity = _runtime.Entity<T>();
        int need = _skip + _take;
        var matched = new List<T>();
        int offset = 0;
        int window = Math.Min(Math.Max(need * 2, 32), 1000);
        while (matched.Count < need && offset < SearchQuery<T>.MaxResultWindow)
        {
            int size = Math.Min(window, SearchQuery<T>.MaxResultWindow - offset);
            SearchResults<T> hits = baseQuery.Skip(offset).Take(size).Execute();
            if (hits.Count == 0)
            {
                break;
            }

            List<string> ids = [.. hits.Select(h => h.Id)];
            List<T> rows = IndexedEntity<T>.Load(Context, ids, entity.IdName, _database, _track);
            AppendInOrder(matched, ids, rows, entity);
            offset += hits.Count;
            if (hits.Count < size)
            {
                break;
            }

            window = Math.Min(window * 2, 1000);
        }

        return Page(matched);
    }

    // The database returns rows in its own order; the page follows the index's. An identifier without a row (deleted, or failing a database
    // predicate) is skipped.
    private static void AppendInOrder(List<T> matched, List<string> ids, List<T> rows, IndexedEntity<T> entity)
    {
        var byId = new Dictionary<string, T>(rows.Count, StringComparer.Ordinal);
        foreach (T r in rows)
        {
            byId[entity.IdOf(r)!] = r;
        }

        foreach (string id in ids)
        {
            if (byId.TryGetValue(id, out T? row))
            {
                matched.Add(row);
            }
        }
    }

    private List<T> Page(List<T> matched) => matched.Skip(_skip).Take(_take).ToList();
}
