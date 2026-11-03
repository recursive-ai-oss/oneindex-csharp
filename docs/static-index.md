# Static index

A static index is an ordinary OneIndex index cut into small immutable files, so a browser (or any HTTP client) can search it from a plain file server or CDN with no server process. `oneindex static build` writes one; `StaticIndex<T>` reads one.

```csharp
StaticManifest manifest = StaticIndexBuilder.Build<Doc>("data/index", "site/search");        // build time
StaticIndex<Doc> index = await StaticIndex.OpenAsync<Doc>(new HttpStaticSource(new HttpClient(), new Uri("https://example.org/search/")));
var hits = await index.QueryAsync(i => i.Search("fox").Where(d => d.Year > 2000).Take(10).Execute());
var near = await index.SearchVectorAsync("Embedding", vector, k: 10, nprobe: 8);
```

## Layout

The directory holds `static.json` and the files of **one commit** of the source index (the newest), each cut into chunks. Files read in runs (the IVF file and the vectors) use `ChunkBytes` (default 16 KiB, 1 KiB to 64 MiB); files probed at scattered places (term dictionaries, lengths, identifier tables, columns, points, packs, postings, stored source) use `ScatteredChunkBytes` (default 4 KiB), so a lookup fetches a few small chunks instead of a few large ones. A file of one chunk or less keeps its name; a longer file is stored as `name.c00000`, `name.c00001`, and so on. For every vector field the build adds `ivf-<field>.ivf` ([ivf.md](ivf.md)) and `ivf-<field>.ids` (the table from IVF numbers to document identifiers), also chunked. The source index's locks, leases, temporary files and older commit files are left out; unreferenced segment files from an unmerged index come along, so build from a freshly committed or merged index.

`static.json` (UTF-8 JSON, version 1):

```json
{ "v": 1, "chunkBytes": 16384, "commit": "commit-000000000000000f.oic",
  "files": [ { "name": "s0000000000000014.stored", "size": 7853755, "chunk": 4096, "crc": [3735928559, ...] }, ... ] }
```

`crc` holds the CRC-32C of each chunk in order. `chunk` (optional) is the file's own chunk size; without it the manifest's `chunkBytes` applies, so manifests written before it existed still read. The parser is strict: unknown versions, missing or mistyped fields, sizes or counts past the limits, and crc lists of the wrong length raise `CorruptIndexException`.

## Reading

`ChunkedStaticStorage` is a read-only `IStorage` over an `IStaticSource` (`HttpStaticSource` over `HttpClient`, `DirectoryStaticSource` for tests). Every chunk fetched is checked against the manifest's CRC before it is used; a mismatch is a `CorruptIndexException` naming the file. Chunks are plain whole-object GETs, so any static host works: no range support, no server code, and each chunk caches in the browser and on a CDN. (The storage interface also admits true range backends; chunking was chosen because it needs nothing from the host.)

The engine reads synchronously, but a browser fetch cannot block. So `QueryAsync` runs the ordinary engine query against the chunks that are cached; when the engine touches a chunk that has not been fetched, the read throws `ChunkNotLoadedException`, the query stops, the missing chunk and, for the IVF and vector files only, `ReadAhead` (default 1) chunks after it are fetched asynchronously, and the query runs again. The final run is the normal query, so **results equal the same query on the source index** (ids and scores), by construction. The cache holds up to 128 MiB of chunks by default.

**Paged reads.** On static storage a segment's term dictionary and field lengths are not read whole at open (`PagedBytes`: 4 KiB pages filled on first touch, each bounded by the file size and the limits), and the whole-file checksum read is skipped, because every chunk is already CRC-checked against the manifest; only the header and footer are validated. Local storage keeps its eager open and whole-file checks. The paged reader answers exactly like the resident one (a test compares them over a random corpus, and the `paged` fuzz target feeds it hostile bytes).

**Parts open on first use.** On a static index the column, point and vector parts of a segment are not opened with the segment: they are opened by the first query that asks for a column, a point field or a vector field (`IDeferredOpenStorage`; the same code on local storage opens everything eagerly, so corruption there is still found at open). A text query therefore does not fetch the columns, the points or the HNSW graph; it reads the directory (last chunk) of the vector files at most. A failure while opening a deferred part is raised by the query that needs it and is not remembered. `Retries` counts the re-runs. The query delegate must be repeatable and must materialise its result inside the delegate.

Vector search through `SearchVectorAsync` uses the IVF file: it reads the centroids once, then only the probed lists and the candidates' full-precision rows. **Hybrid through IVF.** A `Hybrid` query run through `QueryAsync` takes its vector arm from the IVF file (`HybridNprobe`, default 16; the approximate code scores, no float rows) and fuses it with the text arm by the usual fusion, so it never walks the HNSW graph. The filter and deletes apply to the IVF candidates; when fewer than K survive, the arm asks again for more (4x, up to the maximum). The build writes `ivf-<field>.docs` (document id per vector number) when that is not the identity mapping. Because the candidate set differs from the server's graph search, static hybrid results are close to, not identical with, the server's hybrid (measured below); the fusion itself is the same code, and a test checks it equals the server's fusion over the same candidates.

## Limits and failures

A chunk that fails its CRC, a manifest that fails to parse, a file shorter than the manifest says and a source that cannot return a chunk all raise typed exceptions; nothing is trusted before its checksum. The reader has a fuzz target (`static`: manifest, IVF file and packed multi-vector bytes, including `MultiVector.ToRows`; 600 s on one core: 31,659,431 executions, 0 findings in the final run; the first run found an `OverflowException` on a list entry above `int.MaxValue`, now `CorruptIndexException`, with its repro in `testdata/fuzz/static-ivf-list-id-overflow.bin`).

## Measured

The browser suite ([browser.md](browser.md)) builds a static index of 30,000 Wikipedia abstracts and runs 40 queries in headless Chromium (12 text, 8 filter, 8 hybrid, 8 vector, 4 highlight). `tools/StaticBytes` runs the same queries through a counting source and prints bytes and requests per kind and compares every text, filter and highlight result with the same query on the ordinary index (24 of 24 equal) and the hybrid results by overlap. Machine: the LAN worker (x64), .NET 10; the numbers are bytes, so load does not matter.

| KB per query after the first (open's manifest read in the last column) | first query | text | filter | hybrid | vector (IVF) | highlight | manifest |
|---|---:|---:|---:|---:|---:|---:|---:|
| 64 KiB chunks, whole parts at open | 5,167 | 524 | 136 | 1,264 | 701 | 112 | |
| 16 KiB, parts on demand | 2,623 | 210 | 108 | 1,310 | 565 | 188 | 40 |
| + paged reads (16 KiB chunks everywhere) | 1,407 | 298 | 110 | 1,310 | 579 | 192 | 40 |
| + 4 KiB chunks for terms, lengths, ids, columns, points, packs | 867 | 307 | 110 | 1,308 | 589 | 192 | 55 |
| **default**: 4 KiB for postings and stored too, hybrid through IVF (nprobe 16) | **631** | **194** | **60** | **338** | 463 | 187 | 106 |

The first query plus the manifest is about 737 KB (it was 2.6 MB), against Pagefind 1.6.0's 280-323 KB; a later text query is 194 KB against Pagefind's 46-70 KB. **The text query stays above the 120 KB aim:** per text query about 94 KB is term-dictionary pages, 60 KB stored source (ten hits, each a 32 KiB raw block that the stored codec compresses in whole blocks, so a hit costs at least its compressed block; only a smaller stored block size, a writer-side option with a cost in compression, would lower it), 21.5 KB postings and 14.5 KB packs. We accept 194 KB for now.

Costs: the manifest grew from 40 to 106 KB because every chunk has a CRC entry (a compact encoding would about halve it); a 4 KiB chunk raises the requests of a text query from 13 to about 48 (Chromium: median 74 ms per text query, see [browser.md](browser.md)).

**Static hybrid against the server's hybrid** (the server's vector arm is the HNSW graph; 8 queries, 30,000 abstracts, overlap of the top-10 ids and the number of identical lists): nprobe 4: 0.500, 0 of 8; 8: 0.713, 0; **16 (default): 0.887, 1**; 32: 0.950, 2. Hybrid bytes fall from 1,310 KB to 338 KB at nprobe 16. Raise `HybridNprobe` for closer agreement at more bytes.
