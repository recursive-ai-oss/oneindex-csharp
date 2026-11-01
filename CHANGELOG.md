# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

- Repository foundation: solution, shared build properties, packaging metadata, CI and release workflows.
- Storage layer: a range-read interface with synchronous and asynchronous members, memory and local-file backends, a fault-injecting wrapper (torn, lost and reordered writes), and a block-transform slot.
- Commit file format with a checksummed frame, and a crash-safe commit protocol, verified under seeded fault schedules.
- Schema model with deterministic serialization, untyped documents, the analyzer pipeline contract with a reference analyzer, and the query, scorer, collector and segment-reader contracts.
- Attribute types, the schema descriptor source generator, and shapes for `SearchIndex` and `SearchTool` (not implemented).
- Dynamic JSON fields: a `[Dynamic]` property is stored whole and its leaf paths are indexed by kind (keyword, number, bool, date), filterable by path from LINQ (`Path`), filter strings and query JSON, within limits on depth, paths and lengths.
- Schema evolution: opening an index with a changed schema rebuilds the segments that need it in the background from stored source, resumably, while the old schema keeps serving every segment; one commit makes the new schema visible on all of them; changes that cannot be converted fail at open naming the field. Commit format version 2 records the schema epoch and the rebuilt segments written ahead.
