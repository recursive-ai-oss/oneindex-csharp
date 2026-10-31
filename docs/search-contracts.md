# Search contracts

These are the shapes the query engine is built against.

- **Queries are data** (`TermQuery`, `BooleanQuery` with Must/Should/MustNot/Filter clauses, `PhraseQuery` with slop, `PrefixQuery`, `FuzzyQuery`, `RangeQuery`, `MatchAllQuery`), with value equality and a `Boost`. A `QueryVisitor<T>` turns them into other things; execution is a visitor that produces a `Weight`, so execution code never edits the query classes.
- **Segment reader.** `ISegmentReader` gives a segment's parts: `ITextFieldReader` (term lookup, prefix enumeration, postings, per-document field length, field statistics), `IColumnReader`, `IPointIndexReader`, `IStoredFieldsReader`, `ILiveDocs`. Document ids are dense and local to a segment; the search context adds each segment's offset to make global ids.
- **Iterators and scorers.** `DocIdSetIterator` (`NextDoc`, `Advance`, `Cost`). `Scorer` adds `Score()` and three hooks for block-max WAND: `AdvanceShallow(target)` and `GetMaxScore(upTo)` bound the score of a run of documents without decoding them, and `SetMinCompetitiveScore` lets a collector tell the scorer which scores no longer matter. `PostingsIterator.ShallowAdvance` returns a `BlockImpact` (last document, maximum frequency, minimum field length): because BM25 grows with frequency and shrinks with length, those two values bound a block's best score.
- **Collectors.** `TopScoreCollector` keeps the best k by score with a total, deterministic order: higher score first, then lower global document id. NaN scores rank last. It allocates nothing per document and raises the scorer's threshold as the heap fills. `CountCollector` needs no scores.
- **Searcher.** `IndexSearcher` asks an `IWeightFactory` for a weight, walks each segment's scorer, skips deleted documents and feeds the collector, checking cancellation every 1,024 documents.
- **Totals.** `TopDocs.TotalHits` is exact when every match is visited (`ScoreMode.Complete`) and a lower bound under `ScoreMode.TopScores`.
- **Doubles in points.** `SortableBits.Encode` maps a double to a long with the same order, so a single integer point index serves both numeric kinds.
