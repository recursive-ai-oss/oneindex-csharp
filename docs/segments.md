# Segments, deletes, snapshots and merges

This note is normative for the index lifecycle. File frames, the commit file and the commit protocol are in `commit-format.md`; the storage interface is in `storage.md`.

## The segment

A segment is an immutable set of files that holds a range of documents numbered `0 … docCount-1`. Its name is `s` plus 16 lowercase hex digits (`s0000000000000007`); names are allocated from the commit's *next segment id* counter and only ever grow, so name order is creation order. A file is named `<segment>.<part>` (`s0000000000000007.ids`); a delete file is `<segment>.del<generation>`.

Parts:

| Part | Owner | Contents |
|---|---|---|
| `ids` | the index | document identifier to document id (below). Present when the schema has an identifier field |
| `deletes` | the index | the deleted documents of one generation (below) |
| `terms`, `postings`, `lengths`, `columns`, `points`, `stored` | codecs | defined by the codec |

Every file uses the frame of `commit-format.md` (magic, version, checksummed footer). The commit records each file's length, footer checksum, part and codec version, so a file replaced after the commit is noticed when the index opens.

Document ids are dense and local to a segment. Deleting a document does not renumber anything; a merge renumbers.

## The codec interface

A codec is an `ISegmentCodec` registered explicitly in `IndexWriterOptions.Codecs`. It owns one or more *parts*, each listed in `CodecRegistry` (the registry number is the format version a file of that part is written with, and a codec owner raises it when the format changes). A part is owned by exactly one codec; `ids` and `deletes` belong to the index.

```csharp
public interface ISegmentCodec {
    string Name { get; }
    IReadOnlyList<string> Parts { get; }
    void Flush(SegmentFlushContext context, SegmentFileSink sink, CancellationToken ct);
    void Merge(SegmentMergeContext context, SegmentFileSink sink, CancellationToken ct);
    ISegmentPartReader? OpenReader(SegmentReaderContext context, CancellationToken ct);
}
```

**Flush.** `SegmentFlushContext` carries the schema, the segment name and `Documents`: the live buffered documents in order. Document `i` of the list becomes document id `i`. Documents that were replaced or deleted while still buffered are already removed, so a new segment has no deletes. The codec writes the files for the fields it handles (it reads the field definitions from `Schema`) through the `SegmentFileSink`: `sink.Create(part, magicTag, version)` returns a `SegmentFileBuilder` with `Write(span)` and `Complete()`. `Complete` writes the footer, fsyncs the file and records it for the commit. A codec with nothing to write for the schema writes nothing. If `Flush` throws, the writer deletes every file the sink created.

**Merge.** `SegmentMergeContext` carries the schema, the new segment name, the total live document count and `Sources`, one `MergeSource` per source segment in merge order. A source has `Info`, `MaxDoc`, this codec's own `Reader` for that segment (an `ISegmentPartReader`, or null if the segment had none of the codec's parts), `Live` (the source's deleted set as of the merge start) and `DocMap`: `DocMap[old]` is the new document id or `-1` for a deleted document. Live documents are numbered consecutively in source order, `DocMap.Base` being the first new id of a source and `DocMap.LiveCount` the number it contributes. The codec writes the same parts it would write on flush, covering exactly the live documents. Documents deleted after the merge began are still copied; the writer deletes them again in the result.

**Read.** `OpenReader` receives a `SegmentReaderContext` (storage, schema, `SegmentInfo`, limits; `OpenPart(part, out file)` opens a part's file after checking its length against the commit). It returns the codec's view of the segment or null. A part reader may implement any of the reader contracts of the layers above (`ITextFieldReader`, `IColumnReader`, `IStoredFieldsReader` …) and `SegmentReader.GetPart<T>()` finds it by type. Readers are used from several threads and must be thread-safe. A codec holds no per-segment state; flush and merge may run on a background thread.

**Keyword lookup.** A part reader may implement `ITermDocLookup` so that `IndexWriter.DeleteByTerm(field, value)` reaches committed segments. Deleting by the identifier field never needs it (the `ids` part serves it).

**Rules for codecs.** All bytes written must be deterministic for the same input (no hash-order iteration, no timestamps). Readers validate every offset and length before trusting it and raise `CorruptIndexException` naming the file; they honour the `IndexLimits` in the context. The sink calls are synchronous.

## The `ids` part, `OIXI` version 1

A sorted map from identifier to document id, so the index can replace, delete and find a document by identifier with no other codec: `u32 count`, a table of `count` entries `(u32 dataOffset, u32 docId)` sorted by the identifier's UTF-8 bytes (ordinal, strictly ascending), then the identifiers as varint length plus bytes, back to back in table order. A lookup is a binary search with range reads; nothing is held in memory and a lookup of an identifier up to 256 UTF-8 bytes allocates nothing. Identifiers are unique among the live documents of the index (`Upsert` replaces); a segment never holds two entries for one identifier, because a document replaced while still buffered is dropped before the flush.

## The delete file, `OIXD` version 1

Body: `u32 maxDoc`, `u32 deletedCount`, `u8 encoding`, then encoding 0, a bitmap of `ceil(maxDoc/64)` little-endian `u64` words, or encoding 1, the sorted deleted ids as varints (the first id, then each gap minus one). The writer picks the smaller, the bitmap on a tie. A reader rejects: a count that disagrees with the ids or with the commit, bits or ids at or past `maxDoc`, ids out of order, trailing bytes, an unknown encoding. A sparse delete file is held as a sorted array, so a tiny file can never force a large allocation.

A segment's deletes are versioned by the *delete generation* in the commit: generation 0 means no delete file; a commit that adds deletes writes `<segment>.del<g+1>` and lists it in place of generation `g`, whose file stays until the previous commit is unreferenced. A delete file is never modified.

## Writer lifecycle

`IndexWriter` is the single writer of a storage. Calls take turns (a lock), so it is safe from several threads.

1. **Buffer.** `Upsert(document)` validates against the schema, deletes the live document with the same identifier (in the buffer or in a segment) and buffers the new one. `Add` is the same for schemas without an identifier. At `MaxBufferedDocuments` the buffer flushes.
2. **Flush.** The buffer becomes a new segment: every codec's `Flush`, then `ids`. Files are fsynced as they are completed. Nothing refers to them yet.
3. **Delete.** `DeleteById` and `DeleteByTerm` mark documents in the buffer or set bits in the segment's in-memory deleted set. Delete files are written at commit.
4. **Merge.** `MaybeMerge` (run after every commit by default) asks the `TieredMergePolicy` for groups and queues them on the `IMergeScheduler`. A merge reads its sources and writes a new segment; it changes nothing visible. A finished merge is **installed by the next commit**: the sources are replaced by the result, and deletes made to a source after the merge began are re-applied to the result through the document map.
5. **Commit.** Install finished merges, flush, drop segments whose documents are all deleted, write delete files for changed segments, build the commit (generation + 1) and run the commit protocol of `commit-format.md`. Then the commit's files are pinned, the previous commit's pin and the writer's own pending pins are released; files whose last reference went are deleted. A commit with no change writes nothing.

If the commit protocol throws, the writer cannot know whether the commit became durable and refuses further work; opening the index again tells.

### Tiered merge policy

Segments are grouped into tiers by live document count: tier 0 holds segments below `FloorSegmentDocs × SegmentsPerTier` (1,000 × 10), and each next tier's limit is `SegmentsPerTier` times larger. A tier with at least `SegmentsPerTier` (10) segments merges its oldest `MaxMergeAtOnce` (10) segments, never producing more than `MaxMergedSegmentDocs` (5,000,000) live documents. A segment more than `DeletesPctAllowed` (33) percent deleted is rewritten alone. The plan is a pure function of the segment list.

### Scheduler seam and throttle

`IMergeScheduler` runs queued work. `CooperativeMergeScheduler` (the default; no threads) runs work on the caller during `Poll` (the start of every commit) and `WaitForIdle`; `ThreadedMergeScheduler` runs it on one background thread. Merge output is a function of the sources, the codecs and the new segment name only, and the name is allocated when the merge is queued, so for the same sequence of operations the segment bytes are identical under both schedulers when the caller waits for merges at the same points (`WaitForMerges`). Without waiting, a threaded merge may finish before or after a given commit, so the layout then depends on timing, and the contents never do. `MaxMergeBytesPerSecond` makes a threaded merge pause (through the scheduler) so that its output does not exceed the rate; a cooperative scheduler cannot pause and ignores it.

## Snapshots and file lifetime

Every file has a reference count. References are held by: the newest commit (its commit file and every file it lists), each open `IndexSnapshot`, each running merge (its sources) and the writer for files written since the last commit. A file is deleted when its count reaches zero, never before. So a snapshot of commit *N* keeps reading correctly while the writer commits and merges past it, and the files are released when the snapshot is disposed. `IndexSnapshot.Open(storage)` opens the newest commit without a writer and deletes nothing; processes that only read a storage a writer in another process is changing are not supported yet.

## Recovery

`IndexWriter.Open` reads the newest commit with `CommitProtocol.OpenLatest` (raising `CorruptIndexException` for a damaged commit; there is no fallback to an older one) and deletes every file that matches the engine's names (`commit-*.oic`, `commit-*.tmp`, `s<16 hex>.*`) and is not listed by the newest commit. Files with other names are left alone. After a crash at any instant the last acknowledged commit, or a commit the caller had not yet been told about, opens and is complete.

## What is tested

- `tests/Durability` runs the writer under the fault injector with a power cut at every storage operation of a workload (flushes, commits, delete-generation writes, merges), for each crash policy and several seeds, and checks that the reopened index equals an acknowledged commit exactly. It also fails every operation in turn.
- The kill harness runs a writer in a child process that is killed at random points; the parent reopens the directory and compares it with the commits the child acknowledged on a side channel.
