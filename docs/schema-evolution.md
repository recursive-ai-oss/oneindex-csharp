# Schema evolution

An index is opened with the schema the application declares (for the happy path, the attributes of the document type). When the index already holds a schema, the two are compared at open and the index follows the application: no migration step, no separate tool.

## What happens at open

`IndexWriter.Open` (and so `SearchIndex.Open<T>`) compares the declared schema with the committed one, field by field:

| Change | Result |
|---|---|
| A new field | Takes effect at once. Existing documents have no value for it (their stored source never held one), so there is nothing to rebuild. |
| A removed field | The field stops being used. Its data stays in old segments until they are merged or rebuilt away. |
| `Indexed`, `Column` or `Facet` turned on for a field; a different analyzer or index options for a text field; `Multi` turned on; keyword to text or back; integer to floating point | The new schema takes effect at once, the old segments are **rebuilt in the background** from their stored source (below). |
| Boost, `Stored` | Take effect for new documents. No rebuild. |
| An unchanged field | Nothing. |
| A change to a type the stored values cannot be converted to (anything but the three conversions above), `Multi` turned off, a different vector dimension, a different identifier field, an analyzer that is not registered | `SchemaEvolutionException` at open, naming the field (`Field`). The index is left as it was. |
| A rebuild is needed but a field that would have to be rebuilt, or any other searchable field, is not stored | `SchemaEvolutionException` naming that field: the index cannot be recovered from stored source that was never kept. |

The new schema is committed by the open itself, together with a raised **schema epoch** when a rebuild is needed. Every segment records the epoch it was written under (commit-format.md, version 2). A segment older than the commit's epoch awaits its rebuild.

## The rebuild

Each waiting segment is rebuilt by reading its live documents from the stored source and flushing them again under the current schema, exactly as the merge machinery replaces segments: the rebuilt segment takes the place of the original in one commit, deletes that arrived meanwhile are carried over, and the original's files go when the last reader releases them. Properties:

- **Background, with the old version serving.** Readers keep using the last commit; a commit is only ever a set of complete segments. A rebuilt segment appears in the commit that installs it. Until a segment is rebuilt, queries see it as the old schema wrote it: a new analyzer, column or index does not apply to it (a term that is stemmed under the new analyzer will not match unstemmed old terms; a range on a newly indexed field finds nothing in it). `SchemaEvolution` says how many segments still wait.
- **Progress is in the commits.** Each installed rebuild is a commit, so a process that dies loses at most the segment in flight. The next open sees the segments still older than the epoch and continues. Opening again with another schema change before the rebuild finished raises the epoch again; every segment then awaits the newer schema, whatever it had already been through.
- **Pace.** Every commit starts at most four rebuilds; with the default cooperative scheduler they run during the next commit, on the caller's thread, with a threaded scheduler on its thread. `CompleteSchemaEvolution` (on `IndexWriter` and `SearchIndex<T>`) runs the rest to the end, committing as it goes. Merges do not take segments that still wait, so the two never mix schemas.
- **Threads are optional.** Rebuilds run through the same scheduler seam as merges. The cooperative and the threaded run produce byte-identical files (tested), because segment names are taken in a fixed order when the rebuilds are queued, results are installed in name order, and the derived fields of dynamic JSON are ordered by name.
- **Memory.** A rebuild holds the live documents of one segment in memory while it writes the new one. Segments are bounded by the merge policy (5,000,000 live documents by default); lower `MaxMergedSegmentDocs` where documents are large.
- **Storage.** All reads and writes go through the storage interface; the fault injector sees them.

A failed rebuild (for example a stored value that no longer fits the new type) leaves the segment as it was and is reported by `WaitForMerges` / `CompleteSchemaEvolution` as an `IOException` whose inner exception says why.

## Reader processes

A reader process keeps working across the swap. It reads the commit it opened, whose files stay until it lets go (leases, processes.md); `Refresh` moves it to a newer commit, with the schema that commit carries.

## Verified by

Tests: low-level changes of analyzer, columns, indexing, removal and addition; refusals naming the field; reopening halfway; deletes and upserts during the rebuild; cooperative against threaded byte identity; a typed document type that changes between runs; a reader process across the swap. Durability: the kill campaign with schema changes between runs (writer processes killed at random moments of writes, commits, merges and rebuilds, then reopened and checked against the acknowledged commits, the rebuild run to the end every 25 cycles) and the fault sweep with a power cut or failed operation at every storage operation of a run that changes schema halfway (durability method in storage.md).
