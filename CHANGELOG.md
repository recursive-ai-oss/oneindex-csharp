# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.4.0-preview.1] - 2026-11-03

The first public preview.

- Static index: term dictionaries and lengths are read in 4 KiB pages on first use and scattered files use 4 KiB chunks (`StaticBuildOptions.ScatteredChunkBytes`, `oneindex static build --scattered-kb`), so a first query fetches about 0.63 MB instead of 2.6 MB. Static hybrid search takes its vector arm from IVF (`StaticIndex<T>.HybridNprobe`).
- IVF as a per-field vector index on the server (`[Vector(Index = VectorIndexKind.Ivf, IvfLists = n)]`), stored in a new segment part `ivf`.
- Query JSON: a ranking rule naming a field that cannot be sorted is a positioned parse error instead of an exception.
- Multi-vector auto-encoding: `[MultiVector(From = ...)]` encodes the source text at upsert through an `IMultiVectorEncoder` (`SearchIndex.Open<T>(path, embedder, sparse, reranker, multiVectorEncoder)` or `SearchMultiVectorEncoders.Default`), caches by model identity and text in stored source, re-encodes stale entries in `ReembedStale()`, and `SearchQuery<T>.MaxSim(...)` is the late-interaction query stage beside `Rerank`. `IMultiVectorEncoder` gains batch `Encode`/`EncodeAsync` with default implementations.
- Windows: file handles allow sharing delete, rename replaces a destination that a reader has open, and readers use ordinary handles so cached lookups allocate nothing; the full test suite now runs on Windows in CI. The browser smoke app builds on its own CI job (it needs the wasm-tools workload) instead of with the solution.
- Durability harness: the campaigns start their child processes correctly whether run through `dotnet run`, `dotnet Durability.dll` or the application host.

- `RecursiveAI.OneIndex.Analysis.Asian`: Japanese and Korean lattice segmentation (IPADIC, mecab-ko-dic), Chinese most-probable-path segmentation (jieba word list) and Thai, Lao, Khmer and Myanmar maximal matching (ICU word lists), as the analyzers `ja`, `ko`, `zh`, `th`, `lo`, `km` and `my` (`AsianAnalyzers.Register()`; core gains `AnalyzerRegistry.Register`). Dictionaries are a checksummed container format (`OIDICT` 1) with typed errors.
- Decompounding in core: `CompoundSplitter` (Koehn and Knight splitting, fewest parts first), `DecompoundFilter`, packed word lists for de, nl, sv, da, no, fi and is, and the analyzers `<code>-compound` and `<code>-compound-both`.
- `RecursiveAI.OneIndex.Models`: SentencePiece unigram tokenizer (`SentencePieceModel`, `SentencePieceTokenizer`) with the nmt_nfkc normalizer.
- New package `RecursiveAI.OneIndex.Models.Multilingual`: `MultilingualE5Embedder` (multilingual-e5-small, int8 weights packed).

- More-like-this: `MoreLikeThisQuery` picks the most distinctive terms of a text or a stored document by term frequency times idf and searches them; `index.MoreLikeThis(id)`, `index.MoreLikeThisText(text)` and the query JSON node `moreLikeThis` (docs/more-like-this.md). A missing source document raises `SourceDocumentNotFoundException`.
- Facet value search on the happy path: `search.FacetValues(x => x.Category, "sho")` counts over the documents of that search.
- Release builds fetch the packed model weights from a release asset checked against a pinned SHA-256 and fail without them (`tools/models/fetch-release-weights.sh`).

## [0.3.0-preview.1] - 2026-11-03

- Commit format 3: a flushed segment writes its text, column, point, stored and identifier parts into one pack file, so a small commit adds two files instead of eight (fewer flushes to disk per commit). Indexes written by earlier formats open, search and merge unchanged.
- Term proximity: user-text searches add a proximity bonus to the BM25F score (after Büttcher, Clarke and Lushman, SIGIR 2006), weight 0.1 by default; typed queries are unchanged.
- Ranking rules (opt-in): a bucket-sort order of `words`, `typo`, `proximity`, `attribute`, `sort` and `exactness` rules, declared with `[RankingRules(...)]`, set per search with `.RankingRules(...)`, or given as `rankingRules` in query JSON.
- Query JSON: a string holding an unpaired surrogate escape is a positioned parse error instead of an exception.
- Term dictionary format 2: nodes keep their labels, cumulative term counts and child distances in fixed-width tables, so an arc is found by label and fuzzy, typo and as-you-type expansion can look up only the arcs a Levenshtein state can still use. Fuzzy automata share precomputed shape transitions across queries. Version 1 segments stay readable and are rewritten as version 2 when merged.
- Block-max WAND re-sorts its clauses from the previous round's order instead of from scratch, which makes disjunctions of dozens of terms (fuzzy and typo expansions) about twice as fast.
- Merge policy: `TieredMergePolicy.SizeRatio` (default 2) also merges the smallest segments when the largest of a group is at most that many times the others combined, so a settled index keeps a handful of segments and fuzzy, typo and prefix queries walk fewer term dictionaries; set it to 0 for the plain tiered policy. Bulk loads rewrite documents more often (about 3.8 times at 15 flushes against 1.7).
- Filtered nearest-neighbour search: a filter now steers the HNSW walk (only passing documents are scored, with neighbours-of-neighbours expansion through rejected nodes, after the ACORN paper) and a selective filter switches to an exact scan of the passing vectors; both work on the compressed copies.

## [0.1.0-preview.1] - 2026-11-01

- Automatic embedding: `[Vector(From = ...)]` embeds the source text at upsert through the index's embedder (`SearchIndex.Open<T>(path, embedder)` or `SearchEmbedders.Default`; `BgeSmallEmbedder.UseAsDefault()` in the models package), keeps the text and model identity digest beside the vector in stored source so reindexing, reopening, merges and schema rebuilds never embed unchanged text, and `ReembedStale` after a model change.
- Reranking: `IReranker` (a sibling of `IEmbedder`), `SearchQuery<T>.Rerank(...)` as a stage over the best hits of any search (text, vector or hybrid) with the reranker's score and the first-stage score on each hit, `Explain` for the stage, `[FullText(Rerank = true)]` to rerank plain text searches by itself, the `rerank` property of query JSON, and `MiniLmReranker` in the models package (cross-encoder/ms-marco-MiniLM-L6-v2, Apache-2.0) with `WordPieceTokenizer.EncodePair`.
- Hybrid search: `HybridQuery`, `SearchQuery<T>.Hybrid(...)` and the `hybrid` forms of query JSON fuse a text arm and a vector arm by reciprocal rank fusion (default) or weighted linear fusion; filters apply to both arms. `Search(text)` is hybrid automatically when the type has one auto-embedded vector member and the index has an embedder.

- Repository foundation: solution, shared build properties, packaging metadata, CI and release workflows.
- Storage layer: a range-read interface with synchronous and asynchronous members, memory and local-file backends, a fault-injecting wrapper (torn, lost and reordered writes), and a block-transform slot.
- Commit file format with a checksummed frame, and a crash-safe commit protocol, verified under seeded fault schedules.
- Schema model with deterministic serialization, untyped documents, the analyzer pipeline contract with a reference analyzer, and the query, scorer, collector and segment-reader contracts.
- Attribute types, the schema descriptor source generator, and shapes for `SearchIndex` and `SearchTool` (not implemented).
- Dynamic JSON fields: a `[Dynamic]` property is stored whole and its leaf paths are indexed by kind (keyword, number, bool, date), filterable by path from LINQ (`Path`), filter strings and query JSON, within limits on depth, paths and lengths.
- Schema evolution: opening an index with a changed schema rebuilds the segments that need it in the background from stored source, resumably, while the old schema keeps serving every segment; one commit makes the new schema visible on all of them; changes that cannot be converted fail at open naming the field. Commit format version 2 records the schema epoch and the rebuilt segments written ahead.
