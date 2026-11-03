# OneIndex in the browser

Everything in the core package runs in browser WebAssembly (`browser-wasm`, no threads). Storage there is one of three things:

| Backend | Use | Notes |
|---|---|---|
| `MemoryStorage` | an index that lives for the page | read and write; the same code as on the server |
| OPFS, through `OpfsPersistence` | an index kept between page loads | the engine writes synchronously and the main thread cannot block on file writes, so the index lives in a `MemoryStorage` while the page runs and is saved to, or loaded from, OPFS whole, file by file, through the application's `IOpfsHost` (four async methods over `navigator.storage.getDirectory()`). OneIndex has no JavaScript interop itself, so the host is the application's `[JSImport]` code. A save writes the commit file last and skips files no commit lists, so an interrupted save leaves the previous commit or the new one. |
| `ChunkedStaticStorage` over `HttpStaticSource` | a read-only static index from a file server or CDN | [static-index.md](static-index.md) |

The static index is chunked whole-object GETs rather than HTTP range requests: it needs nothing from the host and every chunk is cacheable. Reads through it throw `ChunkNotLoadedException` for a chunk not yet fetched, and the query helper fetches and re-runs.

## Browser suite

`tests/BrowserSuite` publishes `BrowserApp` (browser-wasm, trimmed Release) and drives it in headless Chromium with `playwright-core` (the system Chrome through `executablePath`; Playwright's files are not read). `run.mjs` serves the app and a static index over HTTP, counts the bytes it sends, and compares what the browser prints for each of 40 queries (12 text, 8 filter, 8 hybrid, 8 vector, 4 highlight; ids and scores) with what the same static index returns on the server (`tools/BrowserFixture`). Three modes: **http** (the static index), **memory** (the index copied into `MemoryStorage`) and **opfs** (saved to and reloaded from the real OPFS, then queried). `run-worker.sh` does the whole run for a given abstracts file.

Result on 30,000 Wikipedia abstracts (24 files, 31.0 MB, 64 KiB chunks), Chromium 155.0.8059.84 headless on the worker, Node 22.22.1, playwright-core 1.49.1:

| Mode | Queries compared | Matched | Median ms | p95 ms |
|---|---:|---:|---:|---:|
| http | 40 | 40 | 98 | 697 |
| memory | 32 | 32 | 10 | 42 |
| opfs | 32 | 32 | 10 | 31 |

(Memory and OPFS have no IVF file, so they skip the 8 vector queries.) The 40 http queries made 455 requests and 215 re-runs. Cache-warm session, queries in sequence; the median over all kinds is 98 ms, the p95 is a hybrid query.

Over http, per query kind (the first query excluded; it opens the index):

| Kind | Queries | Median ms | Bytes fetched (avg) | Requests (avg) |
|---|---:|---:|---:|---:|
| text | 11 | 98 | 536 KB | 8.2 |
| filter | 8 | 72 | 139 KB | 2.1 |
| hybrid | 8 | 387 | 1,294 KB | 19.8 |
| vector (IVF) | 8 | 97 | 718 KB | 11.1 |
| highlight | 4 | 55 | 115 KB | 1.8 |
| first query | 1 | | 5,291 KB | |

**Payload** of the .NET WebAssembly app (runtime, the engine, the test app): 5,247,641 bytes raw, 1,933,623 gzip, 1,539,151 brotli (the app bundle, symbols and maps excluded).

**Re-run with paged reads, 4 KiB chunks for scattered files and hybrid through IVF** (the current default; the same 30,000 abstracts, Google Chrome for Testing 156.0.8078.47 headless, Node 22.22.1, playwright-core 1.49.1, the worker, x64): http 40 of 40 equal to the server's static index, memory 24 of 24, OPFS 24 of 24 (memory and OPFS open the files as an ordinary index, whose hybrid uses the HNSW graph, so their 8 hybrid queries are not counted: they legitimately differ from the IVF-based static hybrid, which `static-index.md` compares by overlap). Http: median 155 ms, p95 941 ms over all 40; first query 646 KB (plus the manifest, about 737 KB); per kind (median ms, average KB, average requests): text 74 / 198 / 48.5, filter 107 / 61 / 14.9, hybrid 549 / 371 / 65.5, vector (IVF) 145 / 462 / 72, highlight 208 / 186 / 45.5. Compared with the 16 KiB run before it (first query 2,686 KB; text 60 ms / 214 KB / 13 requests; hybrid 638 ms / 1,341 KB / 82 requests): bytes fall, requests and latency rise for text because a 4 KiB chunk is one more round trip (the engine re-runs a query once per missing chunk, serially). App payload 5,351,162 bytes raw (gzip 1,975,115, brotli 1,573,139). Pagefind 1.6.0, same run: first query 323,199 bytes, median 10.9 ms, p95 102.7 ms, 69,501 bytes per query over all 12 (46,437 over the 11 after the first). Details: [static-index.md](static-index.md).

## Beside Pagefind

Pagefind 1.6.0 (MIT, the npm package, run as a black box) indexes the same 30,000 abstracts as HTML pages (one per abstract; `tests/BrowserSuite/pagefind.mjs`), and the same Chromium build measures the 12 text queries of the suite: `search()` plus loading the first ten results' data.

| | OneIndex static index | Pagefind 1.6.0 |
|---|---:|---:|
| Runtime payload before the first query | 5.25 MB raw, 1.93 MB gzip, 1.54 MB brotli | 575 KB raw, 226 KB gzip, 183 KB brotli (JS and wasm, plus 90 KB of index metadata fetched at init) |
| Index on the server | 31.1 MB (the static directory; vector files are about 13 MB of it) | 15.3 MB in 30,182 files |
| First query, bytes | 646 KB (737 KB with the manifest) | 323 KB |
| Text query after the first, bytes (avg) | 198 KB in 48.5 requests | 46 KB in 4.4 requests (the 11 after the first; all 12 average 70 KB) |
| Text query, median latency | 74 ms | 10.9 ms (p95 102.7 ms) |

Pagefind is smaller and faster on this row, by an order of magnitude in payload and bytes. It is a different thing: a pure keyword search over pre-split index files with no filters, vectors, hybrid or exact engine results. The causes of our numbers are in [static-index.md](static-index.md): a .NET runtime payload; stored-source blocks for each hit (about 60 KB of the 198 KB); term-dictionary pages (about 94 KB). These rows are reported, not gated.

## Other platforms

The WebAssembly smoke app (`tests/WasmSmoke`, trimmed Release) runs the shared scenarios (index, analysis, queries, ranking, vectors, a writer with merges, file-system storage in the runtime's in-memory file system, and the IVF index with MaxSim) in Node and prints OK. The NativeAOT smoke app (`tests/AotSmoke`) adds the static index build and read; it publishes with no warnings (warnings are errors) and runs. A directory index takes a writer lock, and the browser runtime has no file locks, so there is no directory-backed writer in the browser: use `MemoryStorage` and OPFS persistence.
