# Fuzzing

`fuzz/OneIndex.Fuzz` holds the fuzz targets; SharpFuzz instruments `OneIndex.dll` and libFuzzer drives it.

| Target | Input | Surface |
|---|---|---|
| `commit` | arbitrary bytes | the commit-file reader and the schema reader; a commit or schema that parses must serialize back to exactly the same bytes |
| `analysis` | byte 0 picks an analyzer, byte 1 a repeat factor, the rest is UTF-16 text (lone surrogates included) | every registered analyzer, the word and sentence segmenters and the normalizer; nothing may be thrown and token offsets, positions and terms must be consistent |
| `terms` | the terms file of a segment (first byte: bit 0 repairs the header, footer and checksum; bit 1 starts from the version 1 layout instead of version 2) | the term dictionary reader, reached through opening the segment and walking every term, prefix, fuzzy and regular-expression enumeration |
| `postings` | the postings file of a segment (same first byte) | the postings reader: every term's documents, frequencies, positions and offsets, advances and look-aheads |
| `paged` | the terms file (first byte: bit 0 repairs header and footer, bit 1 picks the lengths file instead) opened by page with no whole-file checksum, the way a static index opens it | the same reads as `terms`, lookups and walks through the demand-filled buffer; only `CorruptIndexException` |
| `segment` | arbitrary bytes | the delete-file reader and the identifier-map reader (validation and binary-search lookup); what parses must agree with itself |
| `query` | the bytes choose a small random corpus (segments, deletes, optional merge), a random query tree of every kind, and the checks | query execution against a brute-force BM25F reference (docs/queries.md): same hit set, scores within tolerance, pruned top-k pages bit-identical to the exhaustive list, explanations equal to scores; only `QueryException` for the queries meant to be invalid |
| `columns` | the columns file of a segment (first byte: odd repairs the header, footer and checksum) | the column directory and sections: every document's values, dictionaries, bulk decodes |
| `points` | the points file of a segment (same first byte) | the point-index directory and leaves, numeric and geo: range iteration, advances, leaf reads, geo rectangle lookups (across the antimeridian, at the poles) |
| `stored` | the stored-source file of a segment (same first byte) | the block table and the compressed blocks: every document fetched as a document and as source bytes |
| `usertext` | UTF-16 text (lone surrogates and controls included) | the user-text parser and a search over a small index, with and without a filter; nothing may be thrown, pages are bounded and scores finite |
| `strict` | UTF-16 text | the strict query parser, its printer and binder: only `QueryParseException` may come out; what parses prints and reads back to an equal tree; the compiled query runs on a small index |
| `filter` | UTF-16 text | the filter-string parser and printer (geo conditions included), same checks |
| `queryjson` | UTF-8 bytes | the query JSON reader and writer (geo nodes, function-score nodes, `sort` and `searchAfter` included): only `QueryParseException`; write then read gives an equal request; the compiled query runs, and a sorted request runs its first page and a page after its cursor |
| `vectors` | the vectors file, the graph file, the compressed-vectors file or the IVF file of a segment (byte 0: bit 0 repairs header and footer, bit 1 picks the graph file, bit 3 the compressed file, bit 4 the IVF file, bit 2 skips block checksums) | the directory, the ordinal map, the blocks, the upper layers, the compressed records (int8, int4 and sign-bit fields) and the IVF index (header, centres, list directory, lists); every stored vector, exact, graph and IVF searches with deletes and a filter |
| `dynamic` | first byte = route, then JSON bytes | hostile JSON indexed by path (directly, or after a schema change that rebuilds it from stored source), committed, reopened and queried: only typed exceptions; derived fields stay within the path limit; the stored value comes back whole |
| `dictionary` | byte 0 picks the reader (lattice dictionary, word list, SentencePiece model; bit 7 repairs the checksum footer), then the file | the dictionary and model readers: only the typed format exceptions escape; a loaded dictionary is searched, and segments cover the text |
| `asian` | UTF-16 text | the Japanese, Chinese, Korean and Thai analyzers, the three segmenters, the decompounder and the SentencePiece tokenizer over small synthetic dictionaries: nothing may be thrown, tokens keep offsets inside the text |
| `static` | bytes in place of a static index manifest, an IVF index file or a packed multi-vector (read flat and as rows) | only `CorruptIndexException` (and `FormatException` for the multi-vector) may escape; a file that opens is searched, with and without a filter |
| `wordpiece` | UTF-16 text | the WordPiece tokenizer over a small synthetic vocabulary: nothing may be thrown, ids stay in the vocabulary, a shorter limit gives a prefix of a longer one, a batch row equals the single encoding |

Only `CorruptIndexException` (and its subclass `UnsupportedFormatException`) may come out of a reader. Any other exception, a failed invariant, a hang (`-timeout`) or a memory blow-up (`-rss_limit_mb`) is a finding.

## Running

    export ONEINDEX_FUZZ_TOOLS=/path/to/tools     # holds fuzz/libfuzzer-dotnet and sharpfuzz/sharpfuzz
    export ONEINDEX_FUZZ_WORK=/path/to/workdir    # builds, corpora, findings; must be outside the checkout
    fuzz/run.sh commit 600                        # fuzz for 10 minutes
    fuzz/run.sh replay commit some-input...       # run saved inputs once, without libFuzzer

`run.sh` builds the harness in release mode into `$ONEINDEX_FUZZ_WORK/bin`, writes seeds (a full commit, an empty commit, a schema) into `$ONEINDEX_FUZZ_WORK/corpus/commit/`, instruments `OneIndex.dll`, and runs libFuzzer. Findings land in `$ONEINDEX_FUZZ_WORK/findings/commit/`.

The tools:

* `libfuzzer-dotnet`: the C++ host from the SharpFuzz project, compiled with `clang++ -fsanitize=fuzzer`.
* `sharpfuzz`: `dotnet tool install SharpFuzz.CommandLine --tool-path <dir>/sharpfuzz`. If its runtime is older than the installed one, set `DOTNET_ROLL_FORWARD=Major`.

Reading the output: `ft` (features) is the number to watch. The host's own `cov` stays small because only the .NET side is instrumented. Run one fuzz process at a time.

## Findings

Minimize the input, save it under `testdata/fuzz/` (under 1 MB), and add a test that feeds it and asserts a typed result.

## Milestone run

Every target for 600 s, one process at a time. `execs/s` is the average; `ft` is libFuzzer's feature count at the end. 0 findings unless noted. The slow targets (`analysis`, `query`) run few executions because each input does a lot of work.

| Target | Executions | execs/s | ft |
|---|---|---|---|
| commit | 25,335,596 | 42,155 | 2,931 |
| analysis | 51,643 | 85 | 28,490 |
| terms | 17,199,932 | 28,618 | 5,973 |
| postings | 10,883,769 | 18,109 | 4,132 |
| paged | 16,924,002 | 28,159 | 5,779 |
| segment | 18,201,661 | 30,285 | 1,235 |
| columns | 27,157,079 | 45,186 | 4,932 |
| points | 22,209,569 | 36,954 | 5,238 |
| stored | 687,811 | 1,144 | 6,828 |
| vectors (including IVF and 1-bit) | 3,810,761 | 6,340 | 9,507 |
| dynamic | 4,569,108 | 7,602 | 17,239 |
| wordpiece | 3,613,873 | 6,013 | 2,913 |
| dictionary | 19,614,974 | 32,637 | 2,535 |
| asian | 1,259,853 | 2,096 | 9,537 |
| static | 23,318,661 | 38,799 | 2,099 |
| query | 198,981 | 331 | 50,568 |
| usertext | 3,134,198 | 5,214 | 22,623 |
| strict | 5,643,393 | 9,390 | 39,713 |
| filter | 21,087,077 | 35,086 | 20,039 |
| queryjson | 6,337,134 (stopped at the finding) | | |

The first 15 rows ran on the code before more-like-this was added; `query`, `usertext`, `strict`, `filter` and `queryjson` ran after it.

**Finding (`queryjson`).** A query JSON document with a `rankingRules` property rule naming a field that cannot be sorted on, combined with `sort`, escaped as `ArgumentException` instead of a parse error. Fixed: the reader now rejects such a rule as a `QueryParseException` at `$.rankingRules[i]`. Repro: `testdata/fuzz/queryjson-ranking-rule-field.json`, test `QueryDataFormTests.ARankingRuleNamingAnUnsortableFieldIsAParseErrorWithItsPosition`.

**Re-runs after the fix and the later merges.** `queryjson` 600 s: 5,731,105 executions, 9,535/s, ft 52,778, 0 findings. `dynamic` (957,336 executions, ft 16,215) and `stored` (181,471 executions, ft 6,802) 180 s each, 0 findings. `query`, `usertext`, `strict` and `filter` were not re-run after multi-vector encoding at upsert and the MaxSim stage were merged; neither touches those parsers.
