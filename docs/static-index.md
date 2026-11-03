# Static index

A static index is an ordinary OneIndex index cut into small immutable files, so a browser (or any HTTP client) can search it from a plain file server or CDN with no server process. `oneindex static build` writes one; `StaticIndex<T>` reads one.

```csharp
StaticManifest manifest = StaticIndexBuilder.Build<Doc>("data/index", "site/search");        // build time
StaticIndex<Doc> index = await StaticIndex.OpenAsync<Doc>(new HttpStaticSource(new HttpClient(), new Uri("https://example.org/search/")));
var hits = await index.QueryAsync(i => i.Search("fox").Where(d => d.Year > 2000).Take(10).Execute());
var near = await index.SearchVectorAsync("Embedding", vector, k: 10, nprobe: 8);
```

## Layout

The directory holds `static.json` and the files of **one commit** of the source index (the newest), each cut into chunks of `ChunkBytes` (default 16 KiB, 1 KiB to 64 MiB). A file of one chunk or less keeps its name; a longer file is stored as `name.c00000`, `name.c00001`, and so on. For every vector field the build adds `ivf-<field>.ivf` ([ivf.md](ivf.md)) and `ivf-<field>.ids` (the table from IVF numbers to document identifiers), also chunked. The source index's locks, leases, temporary files and older commit files are left out; unreferenced segment files from an unmerged index come along, so build from a freshly committed or merged index.

`static.json` (UTF-8 JSON, version 1):

```json
{ "v": 1, "chunkBytes": 65536, "commit": "commit-000000000000000f.oic",
  "files": [ { "name": "s0000000000000014.stored", "size": 7853755, "crc": [3735928559, ...] }, ... ] }
```

`crc` holds the CRC-32C of each chunk in order. The parser is strict: unknown versions, missing or mistyped fields, sizes or counts past the limits, and crc lists of the wrong length raise `CorruptIndexException`.

## Reading

`ChunkedStaticStorage` is a read-only `IStorage` over an `IStaticSource` (`HttpStaticSource` over `HttpClient`, `DirectoryStaticSource` for tests). Every chunk fetched is checked against the manifest's CRC before it is used; a mismatch is a `CorruptIndexException` naming the file. Chunks are plain whole-object GETs, so any static host works: no range support, no server code, and each chunk caches in the browser and on a CDN. (The storage interface also admits true range backends; chunking was chosen because it needs nothing from the host.)

The engine reads synchronously, but a browser fetch cannot block. So `QueryAsync` runs the ordinary engine query against the chunks that are cached; when the engine touches a chunk that has not been fetched, the read throws `ChunkNotLoadedException`, the query stops, the missing chunk and `ReadAhead` (default 1) chunks after it in the same file are fetched asynchronously, and the query runs again. The final run is the normal query, so **results equal the same query on the source index** (ids and scores), by construction. The cache holds up to 128 MiB of chunks by default.

**Parts open on first use.** On a static index the column, point and vector parts of a segment are not opened with the segment: they are opened by the first query that asks for a column, a point field or a vector field (`IDeferredOpenStorage`; the same code on local storage opens everything eagerly, so corruption there is still found at open). A text query therefore does not fetch the columns, the points or the HNSW graph; it reads the directory (last chunk) of the vector files at most. A failure while opening a deferred part is raised by the query that needs it and is not remembered. `Retries` counts the re-runs. The query delegate must be repeatable and must materialise its result inside the delegate.

Vector search through `SearchVectorAsync` uses the IVF file: it reads the centroids once, then only the probed lists and the candidates' full-precision rows. The HNSW graph is also in the directory (it is part of the commit) and serves `Hybrid` queries run through `QueryAsync`; a graph walk is one chunk fetch per hop in the worst case, which is why hybrid is the slowest query in the measurements below.

## Limits and failures

A chunk that fails its CRC, a manifest that fails to parse, a file shorter than the manifest says and a source that cannot return a chunk all raise typed exceptions; nothing is trusted before its checksum. The reader has a fuzz target (`static`: manifest, IVF file and packed multi-vector bytes; 600 s on one core: 31,659,431 executions, 0 findings in the final run; the first run found an `OverflowException` on a list entry above `int.MaxValue`, now `CorruptIndexException`, with its repro in `testdata/fuzz/static-ivf-list-id-overflow.bin`).

## Measured

The browser suite ([browser.md](browser.md)) builds a static index of 30,000 Wikipedia abstracts and runs 40 queries in headless Chromium (12 text, 8 filter, 8 hybrid, 8 vector, 4 highlight). `tools/StaticBytes` runs the same 40 queries through a counting source and prints bytes and requests per query kind and per file kind, and compares every text, filter, hybrid and highlight result with the same query on the ordinary index (32 of 32 equal in every row below). Machine: the LAN worker (x64, 32 threads, load shared), .NET 10; the numbers are bytes, so load does not matter.

| | Before (64 KiB chunks) | Parts on demand, 64 KiB | 16 KiB (the default) | 256 KiB |
|---|---:|---:|---:|---:|
| First query: bytes (requests) | 5,167 KB (92) | 4,015 KB (74) | 2,623 KB (175) | 8,367 KB (45) |
| Text query after the first: KB (requests) | 524 (8.2) | 524 (8.2) | 210 (13.1) | 675 (2.6) |
| Filter | 136 (2.1) | 136 (2.1) | 108 (6.8) | 32 (0.1) |
| Hybrid | 1,264 (19.8) | 1,368 (21.4) | 1,310 (81.9) | 1,088 (4.2) |
| Vector (IVF) | 701 (11.1) | 701 (11.1) | 565 (35.5) | 661 (2.8) |
| Highlight | 112 (1.8) | 112 (1.8) | 188 (11.8) | 0 (0) |

(The "before" column is the previous release's code at 64 KiB: 5.29 MB with the open request, the figure the browser measured.) Pagefind 1.6.0 on the same abstracts: 280 KB for the first query, 46 KB per later text query ([browser.md](browser.md)). **Neither target is met:** the first query is 2.6 MB (target about 1 MB) and later text queries are 210 KB (4.6 times Pagefind, target 3 times).

Where the first query's bytes go at 64 KiB chunks before the change: term dictionaries 1,717 KB (27 chunks), stored source 626 KB (10 hits), postings 592 KB, vectors 564 KB and graph 481 KB, small-part packs 473 KB, columns 420 KB, identifier tables 206 KB, lengths 140 KB, points 70 KB. Parts on demand removes vectors, graph, most of the columns and points and part of the packs (1.15 MB). What is left of the first query is dominated by the term dictionaries, which a segment reads whole at open, with the lengths and the identifier tables.

### Proposed next change (not made)

Reading the term dictionary, the lengths and the identifier table by page instead of whole would bring the first query to roughly the stored, postings and a few dictionary pages: about 0.7 MB at 16 KiB chunks (estimate: 2 query words x 3 segments x about 6 dictionary pages, plus 10 stored chunks, about 6 postings chunks and the length and identifier pages). It needs no new file format: a static index checks every chunk against the manifest's CRC, so the whole-file checksum the readers verify at open is redundant there. It needs the term dictionary (and the lengths and identifier readers) to demand-fill a buffer by page with `ChunkNotLoadedException` raised from the page fill, and it needs a fuzz target for the paged reader. Hybrid queries would then still walk the HNSW graph (1.3 MB each); routing the vector arm through the IVF lists needs the engine's hybrid stage to accept an externally computed vector arm, which does not exist yet.

