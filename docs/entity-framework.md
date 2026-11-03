# Entity Framework Core integration

`RecursiveAI.OneIndex.EntityFrameworkCore` keeps OneIndex indexes in step with an EF Core database and lets a search compose with an EF query. It is **not an EF provider**: the database stays the source of truth, and the index is a derived copy that can always be rebuilt from it.

This document is normative for the package's behaviour. Preview status: the package is a preview and its guarantees are those tested below, not yet reviewed as a durability product.

## The sample

```csharp
public class Invoice                                   // an EF entity and a OneIndex document at once
{
    [SearchId] public string Id { get; set; } = "";    // a string key
    [FullText(Boost = 2)] public string Title { get; set; } = "";
    [Keyword, Facet] public string Status { get; set; } = "";
    [Filterable, Sortable] public int Amount { get; set; }
    public int CustomerId { get; set; }                // not indexed: only the database can filter on it
    [Dynamic] public JsonElement Meta { get; set; }
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.UseOneIndexOutbox();   // the log tables
}

var options = new DbContextOptionsBuilder<AppDb>()
    .UseSqlite("Data Source=app.db")
    .UseOneIndex("data/search", o => { o.Index<Invoice>(); o.ApplyInline = true; })   // console app: apply before SaveChanges returns
    .Options;

using var db = new AppDb(options);
db.Invoices.Add(inv); await db.SaveChangesAsync();     // the outbox row is written in the same transaction
var list = await db.Invoices.Search("overdue acme").Where(x => x.CustomerId == 7 && x.Amount > 100).Take(10).ToListAsync();
```

`tests/OneIndex.EntityFrameworkCore.Tests` runs this model (`tests/EfDurability/Model.cs`).

With a host, leave `ApplyInline` off and run the applier loop from a hosted service (the package has no dependency on the hosting abstractions, so it does not ship the `BackgroundService`):

```csharp
public sealed class IndexApplier(IDbContextFactory<AppDb> factory) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var db = factory.CreateDbContext();
        return OneIndexRuntime.For(db).RunAsync(stoppingToken);   // set o.ContextFactory = () => factory.CreateDbContext() in UseOneIndex
    }
}
```

## How it works

1. **The log.** `UseOneIndexOutbox()` maps two tables: the outbox (`Seq`, `EntityType`, `EntityId`, `Operation`) and a one-row state table (`LogId`, `PrunedThrough`). Names and schema are parameters. They are part of the model, so `dotnet ef migrations add` creates them like any other table; apps without migrations get them from `EnsureCreated`.
2. **The interceptor** runs in `SavingChanges`/`SavingChangesAsync`. It detects changes, and for each added, modified or deleted entity of a registered type it adds one `OutboxEntry` (one per entity per save) to the same context, so the row is written by the same `SaveChanges` and the same transaction: both commit or neither does. A change to an owned entity or JSON column of a registered entity logs the owner. A failed save detaches its rows so a retry does not duplicate them.
3. **The applier** (one per index, all driven by `OneIndexRuntime`) reads the log rows after the index's position for its entity type, in `Seq` order and in batches (default 500), loads the **current database state** of the changed ids, upserts the rows that exist, deletes the ids that do not, and commits the index with the new position in the same commit (`SearchIndex<T>.Commit(position)`). The log says "this entity changed"; the database says how. Applying a row twice, or a stale row, therefore gives the same index.
4. **The position** is stored as the commit's applied position (40 bytes: log id, sequence, rebuild base, applied-row count). Documents and position become durable together or not at all, so after any crash the index is exactly at some position and replays from there.
5. **Startup reconcile and retention.** At each catch-up the applier compares **positions, not data**. An index with no position (new), a position from another log (restored or replaced database), a position below `PrunedThrough` (retention removed rows it has not applied) or above the log's highest position is **rebuilt from the tables**: the log position is read first, every row is upserted page by page, documents without a row are deleted, and one commit publishes documents and position. Changes after that position are replayed (harmlessly, if the tables already show them). Retention keeps the newest `RetainRows` positions (default 100,000; `Prune`/`PruneAsync` for manual control); the mark moves forward only, and an applier re-checks it after reading rows, so a prune racing a read cannot leave a hole.
6. **One index per process.** Each registered type has its own index directory `<path>/<TypeName>` with the engine's single writer ([processes.md](processes.md)); two app instances sharing a database each have their own index directory and apply the same log independently.

### Guarantees

- **Exactly-once per index**: every log row is reflected in each index's commit, and the position says which. The position also carries `Count`, the number of rows applied since the last rebuild; while retention has not removed rows after the rebuild position it equals the number of such rows in the log, so a row applied twice or skipped shows. The kill campaign checks it.
- **No lost commits**: a position acknowledged by a returned commit is present after any kill; the campaign records it in a side file and compares.
- **Eventual consistency by default**: a search sees the index's last commit; the entities it returns are the database's current rows.
- **Cooperative and threaded give the same index**: `CatchUp()` (inline), `CatchUpAsync()` and the `RunAsync` loop use the same code; a test runs one seeded workload through each and compares the three indexes.

### Limits and the documented gap

- **Commit order of the log.** The applier assumes a row with a lower `Seq` is visible no later than a row with a higher one. SQLite (one writer at a time) gives that. A database that lets concurrent transactions insert and commit out of order (SQL Server, PostgreSQL, MySQL under concurrency) can expose a higher `Seq` before a lower one; the index could then pass the lower row. Per-provider change feeds are future work; until then use those databases only with serialized writers for the indexed tables, or reindex periodically.
- **Bulk statements.** `ExecuteUpdate`, `ExecuteDelete` and raw SQL do not go through `SaveChanges`, so they log nothing. `OneIndexRuntime.ReindexAsync(query)` re-reads the rows a query returns and upserts them (it does not delete); `ReindexAsync<T>()` rebuilds the whole index, including deletions.
- **Owned types** are not mapped into the index as such. Their changes log the owner; the owner's document is whatever the entity's attributed properties hold (a `[Dynamic] JsonElement` property can carry the JSON of an owned type or JSON column, converted by EF).
- **A directory is opened by one process at a time.** Inline apply and the loop of the same process share one runtime per directory.
- **Inline apply is skipped inside an explicit transaction** (the rows are not committed yet); the loop or the next catch-up covers it. An inline failure is kept in `LastApplyError`, never thrown from `SaveChanges`, because the database change has committed.
- **Package shape.** The package references `Microsoft.EntityFrameworkCore` and its relational layer `Microsoft.EntityFrameworkCore.Relational` (table mapping), nothing else. It uses reflection in one place (building a context when none is configured) and EF Core itself is not trim- or AOT-clean, so the package is not marked AOT-compatible. The core package is unchanged in that respect; the AOT smoke app still publishes warning-free and runs.

## Searching

`db.Invoices.Search(text)` returns an `EfSearchQuery<T>`:

- `Where(predicate)`: the predicate is split on `&&`; each part OneIndex can translate ([linq.md](linq.md)) runs **in the index**, every other part runs **in the database**, over the candidates the index returns. A part that neither side can translate fails with EF's message naming the expression; an index-side failure names the sub-expression ([linq.md](linq.md)).
- `Take(n)` (default 20), `Skip(n)`: the page is **full** unless fewer entities match. Candidates are read from the index in windows (starting at twice the page, doubling to 1,000) and checked against the database until `Skip + Take` survive or the index is exhausted (at most 10,000 candidates). Results are in the index's order; entities deleted since the last apply are dropped; the rows returned are the database's current rows.
- `Configure(q => q.OrderBy(...))` adds index-side stages (sort, facets, ranking, highlight); `AsTracking()`; `AfterPendingChanges()`; `ToList()`/`ToListAsync()`.
- **Read-your-writes is opt-in.** `await db.SaveChangesAndIndexAsync()` saves and then catches the indexes up; `.AfterPendingChanges()` catches up before one search. By default a search may lag the database by the polling interval (250 ms) or by the time to the next `RunAsync` round.

## Tests and measurements

- `tests/OneIndex.EntityFrameworkCore.Tests` (16 tests): outbox atomic with the save and rolled back with it, mirrors of insert/update/delete, position durable across reopen with no re-application, inline apply, opt-in read-your-writes, cooperative vs inline vs threaded identical to each other and to the database (150 seeded saves), retention rebuild and in-window continuation, `ReindexAsync(query)` and `ReindexAsync<T>()`, a context factory, full pages under a 1-in-20 database-only filter, conjunct splitting with a `[Dynamic]` path, rows deleted behind the index, paging equals the unpaged list, and a differential of 300 random searches (text, 0-3 predicates from seven shapes mixing indexed and database-only, random `Skip`/`Take`) against the index's own hit list filtered by LINQ-to-Objects over the database rows (600 rows; every page equal).
- `tests/EfDurability` (`kill`): per cycle two application processes (instances `a` and `b`, each with its own index directory) write seeded inserts, updates and deletes of a 400-id space to **one SQLite file** (write-ahead log) while applying (threaded loop or inline) with a small retention window. Each is killed with SIGKILL at a random time or at a named stage reached by counting probe calls (`save.outbox-added`, `apply.read`, `apply.before-commit`, `apply.after-commit`, `reindex.start`, `reindex.page`, `reindex.before-commit`, `reindex.after-commit`). In 40% of cycles the harness then writes more rows and prunes the log past the indexes so the next start must rebuild. After kills (every fourth cycle, at the end and at random) each index is reopened, its position compared with the positions its process acknowledged (lost commit), caught up, and compared with the database: same ids and count, every row equal in version, title, status and amount, and the applied count equal to the log rows after the rebuild position. A failed open or any difference is a failure. `tools/ef_kill_campaign.sh` runs lanes in parallel. Eight seeded campaigns of 200 cycles (1,600 cycles, 3,093 kills: 768 at named stages, 2,325 at random times; 628 index rebuilds after retention seen at checks, and, in seeds 5-8, 29 kills inside a rebuild at the named stages) found no failed open, lost commit or difference; the campaign had found and led to the fix of one bug: a retention pass that read the old pruning mark could lower a newer pass's mark and leave an index silently behind the log.
- `tests/EfDurability` (`bench`): SaveChanges overhead and applier throughput (indicative, 2,000 rows, three runs on an x64 Linux host with 32 cores under other load). One insert per `SaveChanges`: 22.5 / 29.9 / 25.2 ms without the interceptor, 22.1 / 27.5 / 30.0 ms with the outbox row, 28.9 / 29.8 / 24.4 ms with inline apply: the SQLite commit dominates and the outbox row is within the noise. Applier: a rebuild of 2,000 rows 6,300-6,700 rows/s; replay of 2,000 logged updates 4,760-5,510 rows/s.

## Method notes

SQLite is the only provider run. PostgreSQL was not added: it needs a server image on the worker, and the commit-order limit above applies to it anyway.
