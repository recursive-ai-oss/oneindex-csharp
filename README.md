# OneIndex

**An embedded search engine for .NET: SQLite for search.** Full-text (BM25F), vector (HNSW) and hybrid search in one in-process library, written in C# with no native code and no server. It runs from a directory, from memory, or in the browser from a static bundle fetched over plain HTTP.

OneIndex is unaffiliated with other products named OneIndex, OneSearch or similar.

> **Status: preview, not yet on nuget.org.** Everything in the table below works and is tested; APIs and file formats may still change between previews. Known gaps are listed in the table and in "Known gaps", not hidden.

## What exists

| Feature | State |
|---|---|
| Storage interface (range reads, sync and async); memory, local-file, read-only HTTP and browser backends; fault-injecting test double | Working, tested ([docs/storage.md](docs/storage.md)) |
| Segments, commits (fsync, then atomic rename), background merges, reader processes on committed snapshots, near-real-time readers | Working. |
| Analysis: UAX #29 words and sentences (Unicode 17.0.0), NFC/NFKC, case and accent folding, Snowball stemmers (34 languages) and stop words, CJK bigrams | Working, conformance-tested ([docs/analysis.md](docs/analysis.md)) |
| Term dictionary, postings, block-max WAND, BM25F; term, boolean, phrase, prefix, fuzzy, regex, range queries | Working, measured ([docs/benchmarks.md](docs/benchmarks.md)) |
| Typo tolerance, as-you-type, "did you mean", synonyms, stop words | Working ([docs/typo-tolerance.md](docs/typo-tolerance.md)) |
| Columns, filters, facets with value search, aggregations, distinct, geo, sort and search-after, function scores, ranking rules, explain | Working |
| `[Dynamic]` fields and schema evolution from stored source | Working ([docs/schema-evolution.md](docs/schema-evolution.md)) |
| Strict query syntax, filter strings, versioned query JSON, `SearchTool.For<T>()`, the LINQ builder (comparisons, `null`, `Contains`, `Any`) | Working, fuzzed ([docs/linq.md](docs/linq.md)) |
| Vectors: HNSW and exact flat search, int8, int4 and sign-bit codecs with float rerank, filtered traversal, graph merge, learned sparse vectors, rank fusion, reranking, auto-embedding with an embedding cache | Working ([docs/vectors.md](docs/vectors.md), [docs/hybrid.md](docs/hybrid.md)) |
| Multi-vector fields (`[MultiVector]`) with MaxSim as a rerank and as an exact scan | Working ([docs/multi-vector.md](docs/multi-vector.md)) |
| `RecursiveAI.OneIndex.Models`: WordPiece tokenizer, bge-small-en-v1.5 embedder (int8), cross-encoder reranker, learned sparse encoder | Working ([docs/models.md](docs/models.md)) |
| `RecursiveAI.OneIndex.Analysis.Asian` (ja, zh, ko, th, lo, km, my), decompounding (de, nl, sv, da, no, fi, is), `RecursiveAI.OneIndex.Models.Multilingual` (multilingual-e5-small, SentencePiece) | Working |
| `RecursiveAI.OneIndex.EntityFrameworkCore`: transactional outbox, applier, pushdown with over-fetch, `ReindexAsync` | Working on SQLite and PostgreSQL ([docs/entity-framework.md](docs/entity-framework.md)). |
| `RecursiveAI.OneIndex.Files` / `.Files.Ocr`: folder indexing with citations | Working ([docs/files.md](docs/files.md)) |
| `RecursiveAI.OneIndex.VectorData`: a vector store over an index | Working; the abstraction's conformance suite passes except the listed unsupported cases ([docs/vectordata.md](docs/vectordata.md)) |
| `oneindex` command line tool | Working ([docs/cli.md](docs/cli.md)) |
| Browser: memory and OPFS storage, static index over plain HTTP, IVF | Working; the static index's first query is heavy next to Pagefind's ([docs/static-index.md](docs/static-index.md), [docs/browser.md](docs/browser.md)) |

### Known gaps

- **Entity Framework:** `ExecuteUpdate`, `ExecuteDelete` and raw SQL bypass `SaveChanges`, so they are not indexed; repair with `ReindexAsync(query)`. A log row that becomes visible after the applier gave up on its position is applied at the entity's next change or by `ReindexAsync`. SQL Server and MySQL have not been run ([docs/entity-framework.md](docs/entity-framework.md)).
- **VectorData:** dynamic (dictionary) collections, store-side embedding generation, Manhattan and Hamming distance and whole-array equality filters are not supported, so 13 of the abstraction's conformance tests and the data-type suite (which needs dynamic collections) are excluded ([docs/vectordata.md](docs/vectordata.md)).
- **Static index against Pagefind:** a first query costs about 0.74 MB (Pagefind: 0.32 MB) and a later text query about 194 KB in 48 small requests (Pagefind: 46 KB in 4); the stored-source blocks and term-dictionary pages set that floor. Static hybrid uses IVF candidates, so its results are close to, not identical with, the server's graph-based hybrid (top-10 overlap 0.89 at the default). The .NET WebAssembly payload is 5.35 MB raw (1.57 MB brotli) against Pagefind's 575 KB. IVF is also a per-field server option (`[Vector(Index = VectorIndexKind.Ivf)]`), 3 to 8 times slower than HNSW at equal recall.

### Relevance (BM25, nDCG@10, BEIR test sets; measured 2026-10)

OneIndex at its defaults (BM25F, title weight 3, k1=0.9, b=0.4) against the published BM25 baseline (Kamalloo et al., SIGIR 2024, Table 3, multifield) and Lucene.NET 4.8.0-beta00018 on the same runs. Method and all layouts: [docs/relevance.md](docs/relevance.md).

| Set | OneIndex | Published | Lucene.NET, flat | Lucene.NET, per-field sum |
|---|---|---|---|---|
| SciFact | 0.6912 | 0.665 | see docs | 0.6647 |
| ArguAna | 0.4333 | 0.414 | see docs | 0.4240 |
| NQ | 0.3420 | 0.329 | see docs | 0.3252 |
| SCIDOCS | 0.1531 | 0.158 | 0.1509 | 0.1587 |

BM25F scores a document as one weighted stream, so its like-for-like comparison is Lucene.NET's flat layout. On SCIDOCS, summing per-field scores does better (0.1587), but the same layout costs 3.1 points on SciFact and 1.0 on NQ when OneIndex runs it.

### Speed

Text rows against Lucene.NET 4.8.0-beta00018 with matched analysis and commits, and vector rows against Hnsw.Net at matched recall, on x64 and arm64, are in [docs/benchmarks.md](docs/benchmarks.md) and [docs/vectors.md](docs/vectors.md), with peak memory and index size beside each row (measured 2026-10 and 2026-11).

### Durability and the EF Core integration

**Durable by design.** A commit that returns survives a crash or `kill -9` of your process. A reader sees one whole commit. Every file is checksummed, and damage fails the open with an error that names the file instead of returning wrong results. We test this with fault injection at every storage operation (about 500,000 schedules on x64 and arm64) and with thousands of process kills. On a power cut the index may roll back to an earlier commit, but it is never corrupt. See [docs/durability.md](docs/durability.md).

**EF Core integration.** Changes made through `SaveChanges` reach the index through a transactional outbox log and are applied once. After catch-up the index equals the database, including across kills of the app and of the applier. Verified on SQLite and PostgreSQL 16; SQL Server and MySQL are not tested. A transaction left open longer than the late-row window or age is applied with the entity's next change or `ReindexAsync`. See [docs/entity-framework.md](docs/entity-framework.md).

## Getting started (preview)

Mark the properties of an ordinary class; a source generator inside the package writes the mapping at build time (no reflection, so it is NativeAOT- and trim-safe):

```csharp
using OneIndex;

public class Invoice
{
    [SearchId] public string Id { get; set; } = "";
    [FullText(Boost = 2)] public string Title { get; set; } = "";
    [FullText(Language = "en")] public string Body { get; set; } = "";
    [Filterable, Sortable] public DateTime Modified { get; set; }
    [Keyword, Facet] public string[] Tags { get; set; } = [];
}

using var index = SearchIndex.Open<Invoice>("data/search");   // or SearchIndex.InMemory<Invoice>()
index.Upsert(new Invoice { Id = "inv-1", Title = "Overdue notice", Body = "Payment from Acme Corp is overdue.",
                           Modified = DateTime.UtcNow, Tags = ["invoice"] });
index.Commit();                                               // atomic and durable; searches see the last commit

var cutoff = DateTime.UtcNow.AddYears(-1);
var results = index.Search("overdue \"acme corp\" -draft")      // what a user types: words, "phrases", -exclusions, OR
                   .Where(x => x.Tags.Contains("invoice") && x.Modified > cutoff)
                   .Take(10);
foreach (var hit in results)
    Console.WriteLine($"{hit.Id} {hit.Score:F2} {hit.Document.Title}");
```

- User text never throws, whatever is typed: [docs/user-text.md](docs/user-text.md).
- `Where` is translated to filters and never run; anything it cannot translate throws and names the part: [docs/linq.md](docs/linq.md).
- Attributes, supported types, compile-time diagnostics, highlights: [docs/mapping.md](docs/mapping.md).
- `TotalHits` is a lower bound unless you add `CountExactly()`.
- Typo tolerance, facets, sorting, geo, vectors and hybrid search: see [docs/](docs/) (start with [docs/architecture.md](docs/architecture.md)).

## Packages

| Package | Contents |
|---|---|
| `RecursiveAI.OneIndex` | The engine and the happy path (no dependencies beyond .NET) |
| `RecursiveAI.OneIndex.Models` | Embedders, reranker and sparse encoder on OneModel |
| `RecursiveAI.OneIndex.Analysis.Asian` | Japanese, Chinese, Korean and Southeast Asian segmentation |
| `RecursiveAI.OneIndex.Models.Multilingual` | multilingual-e5-small embedder with SentencePiece tokenizer and int8 weights |
| `RecursiveAI.OneIndex.EntityFrameworkCore` | Keeps an index in step with `SaveChanges` |
| `RecursiveAI.OneIndex.VectorData` | A vector store over an index for Microsoft.Extensions.VectorData |
| `RecursiveAI.OneIndex.Files` / `.Files.Ocr` | Folder indexing through OnePDF, onehtml, onearchive and oneocr |
| `RecursiveAI.OneIndex.Cli` | The `oneindex` command (`index`, `query`, `static build`) |

All share one version.

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
