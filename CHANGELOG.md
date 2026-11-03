# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
