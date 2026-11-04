using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace OneIndex.EntityFrameworkCore;

/// <summary>Adds <c>UseOneIndex</c> to the context options.</summary>
public static class OneIndexDbContextOptionsExtensions
{
    /// <summary>
    /// Keeps OneIndex indexes in step with the database: <c>SaveChanges</c> appends one outbox row per changed registered entity inside the
    /// app's transaction, and an applier (see <see cref="OneIndexRuntime"/>) applies the rows to the indexes. The model must call
    /// <see cref="OutboxModelBuilderExtensions.UseOneIndexOutbox(ModelBuilder, string, string?)"/>. This is not an EF provider.
    /// </summary>
    /// <param name="builder">The options builder.</param>
    /// <param name="path">The directory under which each registered entity type gets its own index directory. One process may open it at a time.</param>
    /// <param name="configure">Registers the entity types (<see cref="OneIndexOptions.Index{T}"/>) and sets the other options. Only the first call for a directory in a process is used.</param>
    /// <returns>The options builder.</returns>
    public static DbContextOptionsBuilder UseOneIndex(this DbContextOptionsBuilder builder, string path, Action<OneIndexOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var options = new OneIndexOptions();
        configure?.Invoke(options);
        if (options.BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "BatchSize must be at least 1.");
        }

        OneIndexRuntime runtime = OneIndexRuntime.GetOrCreate(path, options);
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new OneIndexOptionsExtension(runtime));
        return builder.AddInterceptors(runtime.Interceptor);
    }

    /// <summary>Typed overload of <see cref="UseOneIndex(DbContextOptionsBuilder, string, Action{OneIndexOptions}?)"/>.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="builder">The options builder.</param>
    /// <param name="path">The directory of the indexes.</param>
    /// <param name="configure">Registers the entity types and sets the other options.</param>
    /// <returns>The options builder.</returns>
    public static DbContextOptionsBuilder<TContext> UseOneIndex<TContext>(this DbContextOptionsBuilder<TContext> builder, string path, Action<OneIndexOptions>? configure = null)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).UseOneIndex(path, configure);
        return builder;
    }

    /// <summary>
    /// Saves like <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> and then waits until the indexes reflect the save (read-your-writes).
    /// Without it a search may not see the change for a polling interval.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">Cancels the save or the wait.</param>
    /// <returns>The number of state entries written, as <c>SaveChangesAsync</c> returns.</returns>
    public static async Task<int> SaveChangesAndIndexAsync(this DbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        int n = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await OneIndexRuntime.For(context).CatchUpAsync(cancellationToken).ConfigureAwait(false);
        return n;
    }
}

internal sealed class OneIndexOptionsExtension(OneIndexRuntime runtime) : IDbContextOptionsExtension
{
    public OneIndexRuntime Runtime { get; } = runtime;

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(OneIndexOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using OneIndex ";

        public override int GetServiceProviderHashCode() => RuntimeHelpers.GetHashCode(((OneIndexOptionsExtension)Extension).Runtime);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o && ReferenceEquals(((OneIndexOptionsExtension)o.Extension).Runtime, ((OneIndexOptionsExtension)Extension).Runtime);

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["OneIndex:Path"] = ((OneIndexOptionsExtension)Extension).Runtime.Path;
    }
}

/// <summary>Appends outbox rows to the context being saved, so they commit or roll back with the change.</summary>
internal sealed class OutboxInterceptor(OneIndexRuntime runtime) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<OutboxEntry>> _added = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Release(eventData.Context))
        {
            if (runtime.Options.ApplyInline && eventData.Context!.Database.CurrentTransaction is null)
            {
                try
                {
                    runtime.CatchUp();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    runtime.LastApplyError = e;
                }
            }

            runtime.Wake();
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Release(eventData.Context))
        {
            if (runtime.Options.ApplyInline && eventData.Context!.Database.CurrentTransaction is null)
            {
                try
                {
                    await runtime.CatchUpAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    runtime.LastApplyError = e;
                }
            }

            runtime.Wake();
        }

        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Release(eventData.Context);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    // Detaches the rows this save added (they must not be written twice by a retry) and says whether any were.
    private bool Release(DbContext? context)
    {
        if (context is null || !_added.TryGetValue(context, out List<OutboxEntry>? rows))
        {
            return false;
        }

        _added.Remove(context);
        foreach (OutboxEntry row in rows)
        {
            context.Entry(row).State = EntityState.Detached;
        }

        return rows.Count > 0;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // Inline apply builds its own context to read the database. Learn how from the first context saved, before its change is written, so
        // that the first save is applied like every other; and if that is impossible, say so now rather than skip the index silently.
        runtime.RememberFactory(context);
        if (runtime.Options.ApplyInline)
        {
            runtime.ThrowIfCannotCreateContext();
        }

        Release(context);
        context.ChangeTracker.DetectChanges();
        var wanted = new Dictionary<(string Type, string Id), OutboxOperation>();
        foreach (EntityEntry entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            OutboxOperation op = entry.State == EntityState.Deleted ? OutboxOperation.Delete : OutboxOperation.Upsert;
            if (runtime.TryGetEntity(entry.Metadata.ClrType, out IndexedEntity? indexed))
            {
                string id = indexed.IdOf(entry.Entity) ?? throw new InvalidOperationException($"{entry.Metadata.ClrType.FullName} has no search identifier: its [SearchId] member is null.");
                wanted[(indexed.Name, id)] = op;
            }
            else if (entry.Metadata.IsOwned() && entry.Metadata.FindOwnership() is { } fk && runtime.TryGetEntity(fk.PrincipalEntityType.ClrType, out IndexedEntity? owner)
                     && fk.Properties.Count == 1 && entry.Property(fk.Properties[0].Name).CurrentValue?.ToString() is { } ownerId)
            {
                // A change to an owned part (or JSON column) changes the owner's document.
                wanted[(owner.Name, ownerId)] = OutboxOperation.Upsert;
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var rows = new List<OutboxEntry>(wanted.Count);
        foreach (KeyValuePair<(string Type, string Id), OutboxOperation> w in wanted.OrderBy(w => w.Key.Type, StringComparer.Ordinal).ThenBy(w => w.Key.Id, StringComparer.Ordinal))
        {
            var row = new OutboxEntry { EntityType = w.Key.Type, EntityId = w.Key.Id, Operation = w.Value };
            context.Add(row);
            rows.Add(row);
        }

        _added.Add(context, rows);
        runtime.Probe("save.outbox-added");
    }
}
