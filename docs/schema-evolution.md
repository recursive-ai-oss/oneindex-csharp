# Schema evolution

An index is opened with the schema the application declares (for the happy path, the attributes of the document type). When the index already holds a schema, the two are compared at open and the index follows the application: no migration step, no separate tool.

## What happens at open

`IndexWriter.Open` (and so `SearchIndex.Open<T>`) compares the declared schema with the committed one, field by field:

| Change | Result |
|---|---|
| A new field | Takes effect at once. Existing documents have no value for it (their stored source never held one), so there is nothing to rebuild. |
| A removed field | The field stops being used. Its data stays in old segments until they are merged or rebuilt away. |
| `Indexed`, `Column` or `Facet` turned on for a field; a different analyzer or index options for a text field; `Multi` turned on; keyword to text or back; integer to floating point | The index is **rebuilt in the background** from stored source (below); the new schema becomes visible in one commit when the rebuild is complete. |
| Boost, `Stored` | Take effect at once, for new documents. No rebuild. |
| An unchanged field | Nothing. |
| A change to a type the stored values cannot be converted to (anything but the three conversions above), `Multi` turned off, a different vector dimension, a different identifier field, an analyzer that is not registered | `SchemaEvolutionException` at open, naming the field (`Field`). The index is left as it was. |
| A rebuild is needed but a field that would have to be rebuilt, or any other searchable field, is not stored | `SchemaEvolutionException` naming that field: the index cannot be recovered from stored source that was never kept. |

## The rebuild: one version serves, one swap

The commit file holds two things while a rebuild runs: the **served** schema and segments (the old ones, unchanged) and a **pending** section: the schema to be served next and the segments rebuilt for it so far, each with the name of the segment it was rebuilt from and how many of that source's documents were deleted at the time (commit-format.md).

- **One version at a time.** Queries, snapshots, reader processes and near-real-time views only ever see the served schema and segments. A rebuilt segment is written, listed in the pending section and fsynced with the commit, but never read. Any commit at any kill point therefore serves exactly one schema version with every document present.
- **The swap is one commit.** When every served segment has a current rebuilt twin and nothing is in flight, the next commit lists the rebuilt segments as the served ones, the pending schema as the schema and raises the schema epoch (each segment records the epoch it was written under; the commit records the served one). Readers move from the old version to the new in that atomic rename.
- **Resumable.** Each commit starts at most four rebuilds and lists the finished ones, so a process that dies loses at most the segments in flight. Opening again with the same declared schema keeps the listed rebuilt segments and continues. Opening with a different change discards them and rebuilds for the new one; opening with the schema being served abandons the rebuild.
- **Rebuilt twins are only good for the source they saw.** A delete (including an upsert's) on a source after it was rebuilt makes the twin stale: it is dropped and rebuilt again. This keeps the swap trivial, at the price of rebuilding a segment more than once under a steady stream of deletes.
- **Writes finish the rebuild.** A write of a new document completes the rebuild first (and commits the swap), so documents are always written under one schema. Deletes are allowed while the rebuild runs and are served at once. Merges wait until the swap, and `ForceMerge` completes the rebuild.
- **Pace.** With the default cooperative scheduler the rebuilds run during the next commit on the caller's thread; with a threaded scheduler on its thread. `CompleteSchemaEvolution` (on `IndexWriter` and `SearchIndex<T>`) runs the rest to the end, committing as it goes. `SchemaEvolution` says how many segments still wait.
- **Threads are optional.** Rebuilds run through the same scheduler seam as merges. The cooperative and the threaded run produce byte-identical files (tested), because segment names are taken in a fixed order when the rebuilds are queued, results are listed in name order, and the derived fields of dynamic JSON are ordered by name.
- **Memory.** A rebuild holds the live documents of one segment in memory. Segments are bounded by the merge policy (5,000,000 live documents by default); lower `MaxMergedSegmentDocs` where documents are large.
- **Storage.** All reads and writes go through the storage interface; the fault injector sees them.

A failed rebuild (for example a stored value that no longer fits the new type) leaves the index as it was and is reported by `WaitForMerges` / `CompleteSchemaEvolution` as an `IOException` whose inner exception says why.

## Reader processes

A reader process keeps working across the swap. It reads the commit it opened, whose files stay until it lets go (leases, processes.md); `Refresh` moves it to a newer commit, with the schema that commit serves.

## Verified by

Tests: analyzer, column, indexing, removal and addition; refusals naming the field; reopening halfway (the served schema and segments unchanged, rebuilt twins listed); deletes and upserts during the rebuild; cooperative against threaded byte identity; a typed document type that changes between runs; a reader process across the swap; the pending section round trip. Durability: the kill campaign with schema changes between runs (writer processes killed at random moments of writes, commits, merges and rebuilds, then reopened and checked against the acknowledged commits, every reopen serving segments of a single epoch, the rebuild run to the end every 25 cycles) and the fault sweep with a power cut or failed operation at every storage operation of a run that changes schema halfway (durability method in storage.md).
