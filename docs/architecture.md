# Architecture

OneIndex is one engine in layers. Each layer is public, in its own namespace, and is built only on the layers before it.

| Layer | Namespace | Holds |
|---|---|---|
| Storage | `OneIndex.Storage` | The file interface, backends, the fault injector, the block-transform slot, corruption exceptions, limits. See [storage.md](storage.md). |
| Index | `OneIndex.Index` | The index file frame (header, footer, CRC-32C), segment and commit descriptions, the commit file and protocol, the codec registry, the schema and untyped documents. See [commit-format.md](commit-format.md). |
| Analysis | `OneIndex.Analysis` | The token source contract, analyzers and their registry. See [analysis.md](analysis.md). |
| Search | `OneIndex.Search` | Query objects, the segment-reader interfaces, scorers, collectors and the searcher. See [search-contracts.md](search-contracts.md). |
| Happy path | `OneIndex` | `SearchIndex`, `SearchTool` and the schema attributes. |

Later layers (codecs, aggregations, vectors, query languages, LINQ, the static index) slot between and above these.

## Rules that shape the code

- **No assumption of memory mapping or threads.** Readers ask storage for byte ranges into their own buffers. Work that can run in the background (merges, backfills) also runs cooperatively where there are no threads, with identical results.
- **Immutable once written.** A segment never changes after it is flushed. A commit is the only way a reader learns about new segments, and it is published by an atomic rename after everything it names is durable.
- **Every file is framed and checksummed.** Readers validate magic, version and checksum before they trust an offset, and reject anything malformed with a typed exception that names the file. One `IndexLimits` object travels with every reader.
- **Deterministic bytes.** Nothing persisted depends on hash codes, dictionary order or the clock. The same input gives the same file.
- **Explicit registries.** Codecs and analyzers are looked up in plain lists in the code, so trimming and ahead-of-time compilation keep exactly what is named. Nothing is discovered by reflection.
- **Sync and async twins** on every operation that does I/O, each taking a cancellation token.

## Ownership of the contracts

The segment reader (`ISegmentReader`) is deliberately split by part so that independent pieces of the engine do not meet in the same file: the term dictionary and postings implement `ITextFieldReader`, columns implement `IColumnReader`, the point index `IPointIndexReader`, stored source `IStoredFieldsReader`, and delete bitmaps `ILiveDocs`. Queries are plain data; a visitor turns them into scorers, so adding execution for a query kind never edits the query classes.
