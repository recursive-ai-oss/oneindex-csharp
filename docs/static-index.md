# Static index

A static index is an ordinary OneIndex index cut into small immutable files, so a browser (or any HTTP client) can search it from a plain file server or CDN with no server process. `oneindex static build` writes one; `StaticIndex<T>` reads one.

```csharp
StaticManifest manifest = StaticIndexBuilder.Build<Doc>("data/index", "site/search");        // build time
StaticIndex<Doc> index = await StaticIndex.OpenAsync<Doc>(new HttpStaticSource(new HttpClient(), new Uri("https://example.org/search/")));
var hits = await index.QueryAsync(i => i.Search("fox").Where(d => d.Year > 2000).Take(10).Execute());
var near = await index.SearchVectorAsync("Embedding", vector, k: 10, nprobe: 8);
```

## Layout

The directory holds `static.json` and the files of **one commit** of the source index (the newest), each cut into chunks of `ChunkBytes` (default 256 KiB, 1 KiB to 64 MiB). A file of one chunk or less keeps its name; a longer file is stored as `name.c00000`, `name.c00001`, and so on. For every vector field the build adds `ivf-<field>.ivf` ([ivf.md](ivf.md)) and `ivf-<field>.ids` (the table from IVF numbers to document identifiers), also chunked. The source index's locks, leases, temporary files and older commit files are left out; unreferenced segment files from an unmerged index come along, so build from a freshly committed or merged index.

`static.json` (UTF-8 JSON, version 1):

```json
{ "v": 1, "chunkBytes": 65536, "commit": "commit-000000000000000f.oic",
  "files": [ { "name": "s0000000000000014.stored", "size": 7853755, "crc": [3735928559, ...] }, ... ] }
```

`crc` holds the CRC-32C of each chunk in order. The parser is strict: unknown versions, missing or mistyped fields, sizes or counts past the limits, and crc lists of the wrong length raise `CorruptIndexException`.

## Reading

`ChunkedStaticStorage` is a read-only `IStorage` over an `IStaticSource` (`HttpStaticSource` over `HttpClient`, `DirectoryStaticSource` for tests). Every chunk fetched is checked against the manifest's CRC before it is used; a mismatch is a `CorruptIndexException` naming the file. Chunks are plain whole-object GETs, so any static host works: no range support, no server code, and each chunk caches in the browser and on a CDN. (The storage interface also admits true range backends; chunking was chosen because it needs nothing from the host.)

The engine reads synchronously, but a browser fetch cannot block. So `QueryAsync` runs the ordinary engine query against the chunks that are cached; when the engine touches a chunk that has not been fetched, the read throws `ChunkNotLoadedException`, the query stops, the missing chunk and `ReadAhead` (default 1) chunks after it in the same file are fetched asynchronously, and the query runs again. The final run is the normal query, so **results equal the same query on the source index** (ids and scores), by construction. The cache holds up to 128 MiB of chunks by default. `Retries` counts the re-runs. The query delegate must be repeatable and must materialise its result inside the delegate.

Vector search through `SearchVectorAsync` uses the IVF file: it reads the centroids once, then only the probed lists and the candidates' full-precision rows. The HNSW graph is also in the directory (it is part of the commit) and serves `Hybrid` queries run through `QueryAsync`; a graph walk is one chunk fetch per hop in the worst case, which is why hybrid is the slowest query in the measurements below.

## Limits and failures

A chunk that fails its CRC, a manifest that fails to parse, a file shorter than the manifest says and a source that cannot return a chunk all raise typed exceptions; nothing is trusted before its checksum. The reader has a fuzz target (`static`: manifest, IVF file and packed multi-vector bytes; 600 s on one core: 31,659,431 executions, 0 findings in the final run; the first run found an `OverflowException` on a list entry above `int.MaxValue`, now `CorruptIndexException`, with its repro in `testdata/fuzz/static-ivf-list-id-overflow.bin`).

## Measured

The browser suite ([browser.md](browser.md)) builds a static index of 30,000 Wikipedia abstracts (24 files, 31.0 MB, 64 KiB chunks) and runs 40 queries in headless Chromium. All 40 results equal the server's. The first query costs 5.3 MB because opening a segment reads whole files (term dictionaries, document identifiers, the packed small parts); after that a text query fetches 0.5 MB on average in 8 requests (11 queries), a filtered query 0.14 MB in 2, a highlight 0.11 MB in 2, a vector query 0.7 MB in 11, a hybrid query 1.3 MB in 20. Per query, the cost is dominated by the chunk size: each hit is a stored-source read, and a 64 KiB chunk holds many abstracts; a smaller chunk fetches less per hit and more requests.

What would lower the bytes: reading the term dictionary lazily instead of whole at open, and routing hybrid's vector arm through the IVF file. Neither is done.
