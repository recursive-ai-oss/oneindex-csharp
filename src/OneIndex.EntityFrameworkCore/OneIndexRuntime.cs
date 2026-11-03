using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace OneIndex.EntityFrameworkCore;

/// <summary>Configuration of the OneIndex integration, passed to <c>UseOneIndex</c>.</summary>
public sealed class OneIndexOptions
{
    internal List<Func<IndexedEntity>> Entities { get; } = [];

    /// <summary>
    /// Registers an entity type to keep in an index. The type is both the EF entity and the OneIndex document: it carries the OneIndex attributes
    /// (<c>[SearchId]</c> on a string key, <c>[FullText]</c>, ...). Its index lives in the sub-directory named after the type.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <returns>These options.</returns>
    public OneIndexOptions Index<T>()
        where T : class
    {
        Entities.Add(static () => new IndexedEntity<T>());
        return this;
    }

    /// <summary>
    /// When <see langword="true"/>, every <c>SaveChanges</c> that wrote outbox rows applies them to the indexes before it returns (unless an explicit
    /// transaction is still open), which suits console apps without a host. When <see langword="false"/> (the default), rows wait for
    /// <see cref="OneIndexRuntime.RunAsync(CancellationToken)"/>, <see cref="OneIndexRuntime.CatchUpAsync(CancellationToken)"/> or a search that asks for
    /// <c>AfterPendingChanges()</c>.
    /// </summary>
    public bool ApplyInline { get; set; }

    /// <summary>The most log rows applied per index commit, and the page size of a rebuild. Default 500; at least 1.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>How long the applier loop sleeps when the log is caught up. Default 250 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How many of the newest log positions retention keeps; the applier loop prunes older rows. An index that has not applied a pruned row
    /// rebuilds itself from the tables. Default 100,000; 0 or less disables pruning (call <see cref="OneIndexRuntime.PruneAsync(long, CancellationToken)"/> yourself).
    /// </summary>
    public long RetainRows { get; set; } = 100_000;

    /// <summary>
    /// How many positions behind the newest log row a missing position is still waited for. Positions are handed out when a transaction inserts its
    /// row but rows become visible when it commits, so a database with concurrent writers can show a later position first; the applier remembers the
    /// missing ones and applies their rows when they appear (docs/entity-framework.md). A missing position this far behind is given up: it was a rollback.
    /// Default 10,000. Also bounds how many positions one pass looks at.
    /// </summary>
    public long LateRowWindow { get; set; } = 10_000;

    /// <summary>
    /// How long a missing position is waited for, measured from when this process first noticed it, before it is given up as a rollback. Must exceed
    /// the longest transaction that writes entities. Default 30 seconds. Giving up is the only way a late row can be missed.
    /// </summary>
    public TimeSpan LateRowAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock of the applier loop's sleeps and of the waiting time of missing positions. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Creates the context the applier reads with. Needed only when the runtime cannot construct one itself: by default it calls the public
    /// constructor of the context type that takes its <see cref="DbContextOptions"/>. With a host, pass <c>factory.CreateDbContext</c> of an <c>IDbContextFactory</c>.
    /// </summary>
    public Func<DbContext>? ContextFactory { get; set; }

    /// <summary>
    /// Called with a stage name at the points where a crash is interesting ("apply.read", "apply.before-commit", "apply.after-commit",
    /// "reindex.start", "reindex.page", "reindex.before-commit", "reindex.after-commit", "save.outbox-added"). Meant for fault-injection tests.
    /// </summary>
    public Action<string>? Probe { get; set; }
}

/// <summary>
/// The indexes of one <c>UseOneIndex</c> directory and the loops that keep them in step with the database. One runtime exists per directory in
/// a process; it owns the index writers.
/// </summary>
public sealed class OneIndexRuntime : IAsyncDisposable, IDisposable
{
    private static readonly ConcurrentDictionary<string, OneIndexRuntime> Runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, IndexedEntity> _entities = [];
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Func<DbContext>? _factory;
    private bool _disposed;

    private OneIndexRuntime(string path, OneIndexOptions options)
    {
        Path = path;
        Options = options;
        _factory = options.ContextFactory;
        foreach (Func<IndexedEntity> make in options.Entities)
        {
            IndexedEntity e = make();
            _entities[e.ClrType] = e;
        }

        Interceptor = new OutboxInterceptor(this);
    }

    internal string Path { get; }

    internal OneIndexOptions Options { get; }

    internal OutboxInterceptor Interceptor { get; }

    internal IEnumerable<IndexedEntity> Entities => _entities.Values;

    internal static OneIndexRuntime GetOrCreate(string path, OneIndexOptions options)
        => Runtimes.GetOrAdd(System.IO.Path.GetFullPath(path), p => new OneIndexRuntime(p, options));

    /// <summary>The runtime of a context's <c>UseOneIndex</c> call.</summary>
    /// <param name="context">A context configured with <c>UseOneIndex</c>.</param>
    /// <returns>The runtime.</returns>
    /// <exception cref="InvalidOperationException">The context was not configured with <c>UseOneIndex</c>.</exception>
    public static OneIndexRuntime For(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        OneIndexRuntime rt = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)context).GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>()
            .FindExtension<OneIndexOptionsExtension>()?.Runtime
            ?? throw new InvalidOperationException("The context was not configured with UseOneIndex.");
        rt.RememberFactory(context);
        return rt;
    }

    internal void Probe(string stage) => Options.Probe?.Invoke(stage);

    internal void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    // Without an explicit factory the runtime builds contexts like the one it first met: same type, same options.
    internal void RememberFactory(DbContext context)
    {
        if (_factory is not null)
        {
            return;
        }

        Type type = context.GetType();
        object options = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)context).GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>();
        System.Reflection.ConstructorInfo? ctor = type.GetConstructors().FirstOrDefault(c => c.GetParameters() is [var p] && p.ParameterType.IsInstanceOfType(options));
        if (ctor is not null)
        {
            _factory = () => (DbContext)ctor.Invoke([options]);
        }
    }

    internal DbContext CreateContext()
        => _factory?.Invoke()
           ?? throw new InvalidOperationException("OneIndex cannot create a DbContext of its own. Set OneIndexOptions.ContextFactory (for example to IDbContextFactory<T>.CreateDbContext), or give the context a public constructor taking its DbContextOptions.");

    internal bool TryGetEntity(Type type, out IndexedEntity entity) => _entities.TryGetValue(type, out entity!);

    /// <summary>The index of an entity type registered with <see cref="OneIndexOptions.Index{T}"/>. It is opened on first use and kept open until the runtime is disposed.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <returns>The index. Use it for searches; writes belong to the applier.</returns>
    public SearchIndex<T> Index<T>()
        where T : class
    {
        return Entity<T>().Index(this);
    }

    /// <summary>How far an index has applied the log, read from its last commit.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <returns>The position, or <see langword="null"/> when the index has never been built.</returns>
    public OneIndexPosition? GetPosition<T>()
        where T : class
        => OutboxPosition.TryParse(Index<T>().AppliedPosition.Span, out OutboxPosition p) ? new OneIndexPosition(p.LogId, p.Seq, p.Base, p.Count, p.Gaps.Length) : null;

    internal IndexedEntity<T> Entity<T>()
        where T : class
        => _entities.TryGetValue(typeof(T), out IndexedEntity? e) ? (IndexedEntity<T>)e : throw new InvalidOperationException($"{typeof(T).FullName} is not registered with OneIndexOptions.Index<T>().");

    /// <summary>The most recent error of an inline apply (see <see cref="OneIndexOptions.ApplyInline"/>), or <see langword="null"/>. Inline apply never throws into <c>SaveChanges</c>, because the database change has already committed.</summary>
    public Exception? LastApplyError { get; internal set; }

    /// <summary>Applies the log to every registered index until each is caught up; rebuilds an index the log cannot continue (new, restored database, pruned past it).</summary>
    /// <param name="cancellationToken">Cancels between batches; what was committed stays.</param>
    /// <returns>The number of log rows applied.</returns>
    public int CatchUp(CancellationToken cancellationToken = default)
    {
        _applyLock.Wait(cancellationToken);
        try
        {
            ThrowIfDisposed();
            int n = 0;
            foreach (IndexedEntity e in _entities.Values)
            {
                n += e.CatchUp(this, cancellationToken);
            }

            return n;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>The asynchronous twin of <see cref="CatchUp(CancellationToken)"/>.</summary>
    /// <param name="cancellationToken">Cancels between batches.</param>
    /// <returns>The number of log rows applied.</returns>
    public async ValueTask<int> CatchUpAsync(CancellationToken cancellationToken = default)
    {
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            int n = 0;
            foreach (IndexedEntity e in _entities.Values)
            {
                n += await e.CatchUpAsync(this, cancellationToken).ConfigureAwait(false);
            }

            return n;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    internal async ValueTask CatchUpAsync<T>(CancellationToken cancellationToken)
        where T : class
    {
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await Entity<T>().CatchUpAsync(this, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>
    /// The applier loop for apps with a host: catches up, prunes, then sleeps for <see cref="OneIndexOptions.PollInterval"/> (or until a
    /// <c>SaveChanges</c> in this process wakes it) until cancelled. Run it from a <c>BackgroundService</c>. Errors are kept in <see cref="LastApplyError"/> and retried.
    /// </summary>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int rounds = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int n = await CatchUpAsync(cancellationToken).ConfigureAwait(false);
                if (Options.RetainRows > 0 && (n > 0 || ++rounds % 64 == 0))
                {
                    await PruneAsync(Options.RetainRows, cancellationToken).ConfigureAwait(false);
                }

                LastApplyError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                LastApplyError = e;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task wake = _wake.WaitAsync(linked.Token);
            Task delay = Task.Delay(Options.PollInterval, Options.TimeProvider, linked.Token);
            await Task.WhenAny(wake, delay).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(wake, delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The loser of the race was cancelled.
            }
        }
    }

    /// <summary>Deletes log rows older than the newest <paramref name="retain"/> positions. Indexes that have not applied them rebuild themselves at their next catch-up.</summary>
    /// <param name="retain">How many positions to keep; at least 0.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>The number of rows deleted.</returns>
    public async ValueTask<long> PruneAsync(long retain, CancellationToken cancellationToken = default)
    {
        await using DbContext db = CreateContext();
        return await OutboxStore.PruneAsync(db, Math.Max(0, retain), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The synchronous twin of <see cref="PruneAsync(long, CancellationToken)"/>.</summary>
    /// <param name="retain">How many positions to keep; at least 0.</param>
    /// <returns>The number of rows deleted.</returns>
    public long Prune(long retain)
    {
        using DbContext db = CreateContext();
        return OutboxStore.Prune(db, Math.Max(0, retain));
    }

    /// <summary>Rebuilds one entity type's index from the tables: every row is upserted, indexed documents without a row are deleted, and the log position is taken from before the read.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="cancellationToken">Cancels between pages; nothing is committed until the end.</param>
    /// <returns>A task that completes with the commit.</returns>
    public async ValueTask ReindexAsync<T>(CancellationToken cancellationToken = default)
        where T : class
    {
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await Entity<T>().ReindexAsync(this, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>
    /// Repairs changes the interceptor cannot see (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL): reads the rows a query returns and
    /// upserts them into the index. It does not delete; after <c>ExecuteDelete</c> use <see cref="ReindexAsync{T}(CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="query">The rows to read again, for example <c>db.Invoices.Where(i =&gt; i.CustomerId == 7)</c>.</param>
    /// <param name="cancellationToken">Cancels before the commit.</param>
    /// <returns>The number of rows upserted.</returns>
    public async ValueTask<int> ReindexAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(query);
        List<T> rows = await query.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Entity<T>().Refresh(this, rows, cancellationToken);
        }
        finally
        {
            _applyLock.Release();
        }

        return rows.Count;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Closes the indexes and forgets the runtime, so a later <c>UseOneIndex</c> on the same directory starts a new one.</summary>
    public void Dispose()
    {
        _applyLock.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (IndexedEntity e in _entities.Values)
            {
                e.Dispose();
            }

            Runtimes.TryRemove(new KeyValuePair<string, OneIndexRuntime>(Path, this));
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>Closes the indexes (see <see cref="Dispose()"/>).</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An index's progress through the outbox log.</summary>
/// <param name="LogId">The log the index was built against.</param>
/// <param name="Seq">Every row of the entity type up to this position is in the index's last commit.</param>
/// <param name="Base">The position of the last full rebuild.</param>
/// <param name="Count">How many rows of the entity type were applied after <paramref name="Base"/>. After no pruning it equals the number of such rows up to <paramref name="Seq"/>, which makes a row applied twice or skipped visible.</param>
/// <param name="GapCount">How many positions below <paramref name="Seq"/> are still waited for (see <see cref="OneIndexOptions.LateRowWindow"/>).</param>
public readonly record struct OneIndexPosition(Guid LogId, long Seq, long Base, long Count, int GapCount);
