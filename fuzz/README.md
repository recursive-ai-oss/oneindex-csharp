# Fuzzing

`fuzz/OneIndex.Fuzz` holds the fuzz targets; SharpFuzz instruments `OneIndex.dll` and libFuzzer drives it.

| Target | Input | Surface |
|---|---|---|
| `commit` | arbitrary bytes | the commit-file reader and the schema reader; a commit or schema that parses must serialize back to exactly the same bytes |
| `analysis` | byte 0 picks an analyzer, byte 1 a repeat factor, the rest is UTF-16 text (lone surrogates included) | every registered analyzer, the word and sentence segmenters and the normalizer; nothing may be thrown and token offsets, positions and terms must be consistent |
| `terms` | the terms file of a segment (first byte: odd repairs the header, footer and checksum) | the term dictionary reader, reached through opening the segment and walking every term, prefix, fuzzy and regular-expression enumeration |
| `postings` | the postings file of a segment (same first byte) | the postings reader: every term's documents, frequencies, positions and offsets, advances and look-aheads |
| `segment` | arbitrary bytes | the delete-file reader and the identifier-map reader (validation and binary-search lookup); what parses must agree with itself |
| `query` | the bytes choose a small random corpus (segments, deletes, optional merge), a random query tree of every kind, and the checks | query execution against a brute-force BM25F reference (docs/queries.md): same hit set, scores within tolerance, pruned top-k pages bit-identical to the exhaustive list, explanations equal to scores; only `QueryException` for the queries meant to be invalid |
| `columns` | the columns file of a segment (first byte: odd repairs the header, footer and checksum) | the column directory and sections: every document's values, dictionaries, bulk decodes |
| `points` | the points file of a segment (same first byte) | the point-index directory and leaves: range iteration, advances, leaf reads |
| `stored` | the stored-source file of a segment (same first byte) | the block table and the compressed blocks: every document fetched as a document and as source bytes |
| `usertext` | UTF-16 text (lone surrogates and controls included) | the user-text parser and a search over a small index, with and without a filter; nothing may be thrown, pages are bounded and scores finite |
| `strict` | UTF-16 text | the strict query parser, its printer and binder: only `QueryParseException` may come out; what parses prints and reads back to an equal tree; the compiled query runs on a small index |
| `filter` | UTF-16 text | the filter-string parser and printer, same checks |
| `queryjson` | UTF-8 bytes | the query JSON reader and writer: only `QueryParseException`; write then read gives an equal request; the compiled query runs |
| `dynamic` | first byte = route, then JSON bytes | hostile JSON indexed by path (directly, or after a schema change that rebuilds it from stored source), committed, reopened and queried: only typed exceptions; derived fields stay within the path limit; the stored value comes back whole |

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
