# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

- Term dictionary format 2: nodes keep their labels, cumulative term counts and child distances in fixed-width tables, so an arc is found by label and fuzzy, typo and as-you-type expansion can look up only the arcs a Levenshtein state can still use. Fuzzy automata share precomputed shape transitions across queries. Version 1 segments stay readable and are rewritten as version 2 when merged.
- Automatic embedding: `[Vector(From = ...)]` embeds the source text at upsert through the index's embedder (`SearchIndex.Open<T>(path, embedder)` or `SearchEmbedders.Default`; `BgeSmallEmbedder.UseAsDefault()` in the models package), keeps the text and model identity digest beside the vector in stored source so reindexing, reopening, merges and schema rebuilds never embed unchanged text, and `ReembedStale` after a model change.
- Hybrid search: `HybridQuery`, `SearchQuery<T>.Hybrid(...)` and the `hybrid` forms of query JSON fuse a text arm and a vector arm by reciprocal rank fusion (default) or weighted linear fusion; filters apply to both arms. `Search(text)` is hybrid automatically when the type has one auto-embedded vector member and the index has an embedder.

- Repository foundation: solution, shared build properties, packaging metadata, CI and release workflows.
- Storage layer: a range-read interface with synchronous and asynchronous members, memory and local-file backends, a fault-injecting wrapper (torn, lost and reordered writes), and a block-transform slot.
- Commit file format with a checksummed frame, and a crash-safe commit protocol, verified under seeded fault schedules.
- Schema model with deterministic serialization, untyped documents, the analyzer pipeline contract with a reference analyzer, and the query, scorer, collector and segment-reader contracts.
- Attribute types, the schema descriptor source generator, and shapes for `SearchIndex` and `SearchTool` (not implemented).
- Dynamic JSON fields: a `[Dynamic]` property is stored whole and its leaf paths are indexed by kind (keyword, number, bool, date), filterable by path from LINQ (`Path`), filter strings and query JSON, within limits on depth, paths and lengths.
- Schema evolution: opening an index with a changed schema rebuilds the segments that need it in the background from stored source, resumably, while the old schema keeps serving every segment; one commit makes the new schema visible on all of them; changes that cannot be converted fail at open naming the field. Commit format version 2 records the schema epoch and the rebuilt segments written ahead.
