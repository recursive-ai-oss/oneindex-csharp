# OneIndex

**An embedded search engine for .NET: SQLite for search.** Full-text (BM25F), vector (HNSW) and hybrid search in one in-process library, written in C# with no native code and no server. It runs from a directory, from memory, or, later, from a static bundle in the browser.

OneIndex is unaffiliated with other products named OneIndex, OneSearch or similar.

> **Status: pre-alpha.** The repository holds the foundation only: the build, the storage layer with a fault-injecting test double, the commit format and protocol, and the contracts the engine will be built on. There is no search yet. Nothing here is released.

## What exists

| Feature | State |
|---|---|
| Storage interface (range reads, durable append, atomic rename), memory and local-file backends | Working, tested |
| Fault-injecting storage (torn, lost and reordered writes) and a block-transform slot | Working, tested |
| Commit file format and crash-safe commit protocol | Working, tested under 1,848 seeded crash schedules |
| Schema model, untyped documents, deterministic schema serialization | Working, tested |
| Analyzer pipeline contract and a reference analyzer | Contract only; Unicode word breaking, folding and stemming not started |
| Query objects, scorer, collector and segment-reader contracts | Contracts only; no query execution |
| Segments, flushing, merging, delete bitmaps | Not started |
| Term dictionary, postings, BM25F, phrase, prefix, fuzzy queries | Not started |
| Columns, filters, facets, aggregations, geo | Not started |
| Vectors (HNSW, quantization, hybrid), reranking, models | Not started |
| `SearchIndex.Open<T>`, LINQ, user-text queries, `SearchTool` | Shapes only; they throw `NotImplementedException` |
| Entity Framework Core, folder indexing, vector-store adapter, command line tool | Empty packages |
| Browser backends and static index | Not started |

## Packages

| Package | Contents |
|---|---|
| `RecursiveAI.OneIndex` | The engine and the happy path (no dependencies beyond .NET) |
| `RecursiveAI.OneIndex.Models` | Embedders, reranker and sparse encoder on OneModel |
| `RecursiveAI.OneIndex.Analysis.Asian` | Japanese, Chinese, Korean and Southeast Asian segmentation |
| `RecursiveAI.OneIndex.EntityFrameworkCore` | Keeps an index in step with `SaveChanges` |
| `RecursiveAI.OneIndex.VectorData` | A vector store over an index for Microsoft.Extensions.VectorData |
| `RecursiveAI.OneIndex.Files` / `.Files.Ocr` | Folder indexing through OnePDF, onehtml, onearchive and oneocr |
| `RecursiveAI.OneIndex.Cli` | The `oneindex` command |

All share one version. Only the core is more than a placeholder today.

## Build

    dotnet build -c Release
    dotnet test --solution OneIndex.slnx -c Release

Build output goes outside the checkout: set `ONEINDEX_ARTIFACTS` to choose where (the default is a folder in your local application data). The .NET SDK in `global.json` is required.

## Layout

- `src/OneIndex`: the core library. `Storage`, `Index`, `Analysis` and `Search` are public layers, each built only on the ones before it; the happy path lives in the root namespace.
- `src/OneIndex.Generators`: the source generator that ships inside the core package.
- `tests/`, `bench/`, `fuzz/`: unit tests, the durability, relevance and differential suites, smoke apps (NativeAOT and browser WebAssembly), benchmarks and fuzz targets.
- `docs/`: normative notes on formats and protocols; start with [the architecture note](docs/architecture.md).

## License

MIT OR Apache-2.0, at your option. Copyright (c) Recursive AI, LLC. See [LICENSE-MIT](LICENSE-MIT) and [LICENSE-APACHE](LICENSE-APACHE). Third-party material is listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
