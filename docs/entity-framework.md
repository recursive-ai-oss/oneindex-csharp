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
4. **The position** is stored as the commit's applied position (at most 1,024 bytes: a version byte, the log id, the high-water mark, the rebuild base, the applied-row count, and the list of gaps below the mark). Documents and position become durable together or not at all, so after any crash the index is exactly at some position and replays from there.
5. **Out-of-order visibility (gaps).** A database hands out a log position when a transaction inserts its row but shows the row only when the transaction commits, so with concurrent writers position 7 can be visible before position 6 (SQL Server, PostgreSQL, MySQL; SQLite serializes writers and never does). Reading "everything after the highest position seen" would skip 6 for good. So the applier works with a *high-water mark* and a list of *gaps*: positions at or below the mark that had no visible row when it passed them. Each pass reads the window after the mark and also looks the gaps up again; a row that has appeared is applied then and the gap is removed. Because applying reads the entity's current state, a row applied early, late or twice leaves the same index. Gaps live in the index commit with the mark, so they survive a crash.
6. **Giving up on a gap.** A rolled-back insert, or a process that died mid-transaction, leaves a position that never fills. Two bounded rules release it, both configurable: the gap is `LateRowWindow` positions behind the newest row (default 10,000), or this process has waited `LateRowAge` since it first noticed it (default 30 seconds; it must exceed the longest transaction that writes indexed entities; the clock restarts when a process restarts, which only waits longer). Giving up is the only way a late row can be missed: a row that appears at a given-up position is applied by the next change to that entity or by `ReindexAsync`. A position left by a failed save is common on PostgreSQL (sequences are not rolled back), so gaps are normal there and cost a byte or two each.
7. **When the gaps would not fit.** The position is capped at the engine's 1,024-byte limit for the marker (about a thousand gaps when they are close together, fewer when far apart). If recording one more gap would exceed it, the mark **stops before that gap**: it does not advance past it and nothing is released early, but the rows beyond are still applied in that pass (and again in the next, which is harmless), so the index stays equal to the database. The mark moves on as gaps fill or are given up. A rebuild uses the same rule for the positions it saw missing.
8. **Startup reconcile and retention.** At each catch-up the applier compares **positions, not data**. An index with no position (new), a position from another log (restored or replaced database), a position, or an unresolved gap, at or below `PrunedThrough` (retention removed rows it has not applied) or above the log's highest position is **rebuilt from the tables**: the log position is read first, every row is upserted page by page, documents without a row are deleted, and one commit publishes documents and position. Changes after that position are replayed (harmlessly, if the tables already show them). Retention keeps the newest `RetainRows` positions (default 100,000; `Prune`/`PruneAsync` for manual control); the mark moves forward only, and an applier re-checks it after reading rows, so a prune racing a read cannot leave a hole.
9. **One index per process.** Each registered type has its own index directory `<path>/<TypeName>` with the engine's single writer ([processes.md](processes.md)); two app instances sharing a database each have their own index directory and apply the same log independently.

### Guarantees

- **Exactly-once per index, on any provider**: after catch-up, every index equals the database, including when transactions commit out of position order. Every log row is reflected in each index's commit, and the position says which; it also carries `Count`, the number of rows applied after the rebuild position, which equals the number of such rows in the log while retention has not removed any of them, so a row applied twice or skipped shows. The bounded exception is a position given up on (item 6 above): a transaction that stays open longer than `LateRowAge` and then commits is applied only with its entity's next change or a `ReindexAsync`.
- **No lost commits**: a position acknowledged by a returned commit is present after any kill; the campaign records it in a side file and compares.
- **Eventual consistency by default**: a search sees the index's last commit; the entities it returns are the database's current rows.
- **Cooperative and threaded give the same index**: `CatchUp()` (inline), `CatchUpAsync()` and the `RunAsync` loop use the same code; a test runs one seeded workload through each and compares the three indexes.

### Limits and the documented gap

- **Providers run.** SQLite and PostgreSQL are exercised (above). SQL Server and MySQL are not; the gap handling assumes nothing provider-specific beyond a position that grows and becomes visible at commit.
- **Bulk statements.** `ExecuteUpdate`, `ExecuteDelete` and raw SQL do not go through `SaveChanges`, so they log nothing. `OneIndexRuntime.ReindexAsync(query)` re-reads the rows a query returns and upserts them (it does not delete); `ReindexAsync<T>()` rebuilds the whole index, including deletions.
- **Owned types and JSON columns** reach the index through `[Dynamic]` on a property whose type is a class of plain properties (docs/dynamic.md): EF maps it as an owned type or a JSON column, the generated mapping writes it as a dynamic JSON field. A change to the owned part logs the owner, so the owner's document is rebuilt.
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

- `tests/OneIndex.EntityFrameworkCore.Tests` (25 tests): outbox atomic with the save and rolled back with it, mirrors of insert/update/delete, position durable across reopen with no re-application, inline apply, opt-in read-your-writes, cooperative vs inline vs threaded identical to each other and to the database (150 seeded saves), retention rebuild and in-window continuation, `ReindexAsync(query)` and `ReindexAsync<T>()`, a context factory, full pages under a 1-in-20 database-only filter, conjunct splitting with a `[Dynamic]` path, rows deleted behind the index, paging equals the unpaged list, a differential of 300 random searches (text, 0-3 predicates from seven shapes mixing indexed and database-only, random `Skip`/`Take`) against the index's own hit list filtered by LINQ-to-Objects over the database rows (600 rows; every page equal), and the out-of-order cases (`GapTests`): a row visible below the mark is applied; five seeded workloads of 160 transactions made visible in shuffled order with catch-ups between random batches end equal to the database with no gaps and `Count` 160; give-up by age (not before 30 s) and by distance; 9,751 gaps that do not fit stall the mark but all 49 rows are applied, the position stays within 1,024 bytes and the mark reaches the newest row once gaps are released; a rebuild records the positions it saw missing; a gap that retention has passed forces a rebuild; the pruning mark never moves backwards (the interleaving that once lowered it); and a change to an owned JSON column reaches the index through its owner. Out-of-order visibility is simulated on SQLite (which serializes writers) by committing the entity change and a log row with an explicit position together, in a chosen order.
- `tests/EfDurability` (`kill`): per cycle two application processes (instances `a` and `b`, each with its own index directory) write seeded inserts, updates and deletes of a 400-id space to **one database** (SQLite in write-ahead mode, or PostgreSQL) while applying (threaded loop or inline) with a small retention window; each process runs one or more concurrent writers, and with `--hold` a writer sometimes keeps its transaction open for up to 40 ms after `SaveChanges`, so later positions commit first. Each process is killed with SIGKILL at a random time or at a named stage reached by counting probe calls (`save.outbox-added`, `apply.read`, `apply.before-commit`, `apply.after-commit`, `reindex.start`, `reindex.page`, `reindex.before-commit`, `reindex.after-commit`). In about 40% of cycles the harness then writes more rows and prunes the log past the indexes so the next start must rebuild. After kills (every fourth cycle, at the end and at random) each index is reopened, its position compared with the positions its process acknowledged (lost commit), caught up, and compared with the database: same ids and count, every row equal in version, title, status, amount and the owned JSON column, and the applied count equal to the log rows after the rebuild position. A failed open or any difference is a failure. `tools/ef_kill_campaign.sh` runs lanes in parallel (`EF_PG` selects PostgreSQL, `EF_WRITERS`, `EF_HOLD`, `EF_HOOK` shape the load).
  - **SQLite**, four lanes of 150 cycles: 1,086 kills (296 at named stages: apply.read 66, apply.before-commit 52, apply.after-commit 45, reindex.start 25, reindex.page 23, reindex.before-commit 19, reindex.after-commit 13, save.outbox-added 53; 790 at random times), 478 index checks, 251 rebuilds after retention, 0 failed opens, 0 lost commits, 0 differences.
  - **PostgreSQL 16.2** (a user-local server from the `pgserver` wheel, unix socket, no container), four lanes of 150 cycles, two application processes per lane with two concurrent writers each (four concurrent writers per database): 1,658 kills (364 at named stages: apply.read 56, apply.before-commit 51, apply.after-commit 54, reindex.start 42, reindex.page 40, reindex.before-commit 38, reindex.after-commit 34, save.outbox-added 49; 1,294 at random times), 510 index checks, 357 rebuilds, 1,596 commits made while gaps were open (at most 93 at once), 577 rows applied because they became visible below the mark, 0 failed opens, 0 lost commits, 0 differences.
  - The campaign found two bugs in this area: a retention pass that read an old pruning mark could lower a newer pass's mark and leave an index silently behind the log (fixed; the interleaving is a test), and a count that included rows applied at positions the rebuild had recorded as gaps (the count now covers rows above the rebuild position only).
- `tests/EfDurability` (`bench`): SaveChanges overhead and applier throughput (indicative, 2,000 rows, three runs on an x64 Linux host with 32 cores under other load). One insert per `SaveChanges`: 22.5 / 29.9 / 25.2 ms without the interceptor, 22.1 / 27.5 / 30.0 ms with the outbox row, 28.9 / 29.8 / 24.4 ms with inline apply: the SQLite commit dominates and the outbox row is within the noise. Applier: a rebuild of 2,000 rows 6,300-6,700 rows/s; replay of 2,000 logged updates 4,760-5,510 rows/s.

