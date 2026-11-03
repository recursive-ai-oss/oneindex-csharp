# OneIndex

**An embedded search engine for .NET: SQLite for search.** Full-text (BM25F), vector (HNSW) and hybrid search in one in-process library, written in C# with no native code and no server. It runs from a directory, from memory, or, later, from a static bundle in the browser.

OneIndex is unaffiliated with other products named OneIndex, OneSearch or similar.

> **Status: preview (0.1.0-preview.1, not yet on nuget.org).** The text engine, the app-search features and the first vector, model and hybrid pieces work and are measured below. APIs and file formats may still change between previews. Gaps are listed, not hidden.

## What exists

| Feature | State |
|---|---|
| Storage interface (range reads, sync and async), memory and local-file backends, fault-injecting test double | Working, tested |
| Segments, commits (fsync, then atomic rename), background merges, reader processes on committed snapshots, near-real-time readers | Working; see Durability below (independent review not yet done) |
| Analysis: UAX #29 words and sentences (Unicode 17.0.0), NFC/NFKC, case and accent folding, Snowball stemmers (34 languages) and stop words, CJK bigrams | Working, conformance-tested |
| Term dictionary, postings, block-max WAND, BM25F; term, boolean, phrase, prefix, fuzzy, regex, range queries | Working, measured |
| Typo tolerance, as-you-type, "did you mean", synonyms, stop words | Working |
| Columns, filters, facets with value search, aggregations, distinct, geo, sort and search-after, function scores, explain | Working |
| `[Dynamic]` fields and schema evolution from stored source | Working |
| Strict query syntax, filter strings, versioned query JSON, `SearchTool.For<T>()` | Working, fuzzed |
| Vectors: HNSW and exact flat search; auto-embedding with an embedding cache; hybrid search (rank fusion, linear fusion) | Working (preview); quantized codecs, filtered traversal, sparse vectors and reranking in progress |
| `RecursiveAI.OneIndex.Models`: WordPiece tokenizer, default English embedder (bge-small-en-v1.5, int8) | Working (preview) |
| Entity Framework Core, folder indexing, vector-store adapter, command line tool | Not built yet (empty packages) |
| `RecursiveAI.OneIndex.Analysis.Asian` (ja, zh, ko, th, lo, km, my), decompounding (de, nl, sv, da, no, fi, is), `RecursiveAI.OneIndex.Models.Multilingual` (multilingual-e5-small, SentencePiece) | Working (preview) |
| Browser backends and static index, multi-vector, IVF | Not built yet |

### Relevance (BM25, nDCG@10, BEIR test sets)

OneIndex at its defaults (BM25F, title weight 3, k1=0.9, b=0.4) against the published BM25 baseline (Kamalloo et al., SIGIR 2024, Table 3, multifield) and Lucene.NET 4.8.0-beta00018 on the same runs. Method and all layouts: [docs/relevance.md](docs/relevance.md).

| Set | OneIndex | Published | Lucene.NET, flat | Lucene.NET, per-field sum |
|---|---|---|---|---|
| SciFact | 0.6912 | 0.665 | see docs | 0.6647 |
| ArguAna | 0.4333 | 0.414 | see docs | 0.4240 |
| NQ | 0.3420 | 0.329 | see docs | 0.3252 |
| SCIDOCS | 0.1531 | 0.158 | 0.1509 | 0.1587 |

BM25F scores a document as one weighted stream, so its like-for-like comparison is Lucene.NET's flat layout. On SCIDOCS, summing per-field scores does better (0.1587), but the same layout costs 3.1 points on SciFact and 1.0 on NQ when OneIndex runs it.

### Speed

Text rows against Lucene.NET 4.8.0-beta00018 with matched analysis and commits, on x64 and arm64, are in [docs/benchmarks.md](docs/benchmarks.md), with peak memory and index size beside each row. Most rows are faster than Lucene.NET. Rows still slower than 1.25x: fuzzy queries with 1 and 2 edits and typo-tolerant queries on x64, and commit latency on arm64. Work on these is in progress.

### Durability

Tested by random process kills during writes, merges and schema backfills (over 10,000 kills) and by torn, lost and reordered writes injected through the storage interface (over 100,000 crash schedules), with zero failed opens and zero lost commits. On Linux, .NET cannot fsync a directory, so after a power cut an index may come back at its previous commit; it is never corrupt ([docs/storage.md](docs/storage.md)). An independent review of the commit protocol is still to come.

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
| `RecursiveAI.OneIndex.Cli` | The `oneindex` command |

All share one version. The core and `.Models` work today; the others are placeholders.

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
