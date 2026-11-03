# Benchmarks: method

How the text rows are measured against Lucene.NET and the native engines. The relevance method is in [relevance.md](relevance.md).
Nothing here is a published result: publishable rows come from exclusive benchmark runs on the machines listed in the machine
record, and every number carries that record.

## What is measured

All rows run over the **Wikipedia abstracts**: the first paragraph of each article of the English Wikipedia 20231101 dump,
shards 00000 and 00001 of the Hugging Face `wikimedia/wikipedia` dataset (CC BY-SA 4.0 / GFDL), built by
`tools/wikipedia_abstracts.py` into `abstracts-20231101.en-00000-00001.jsonl`. Each line also carries two derived columns: `len`
(abstract length in characters, numeric) and `cat` (first letter of the title, keyword, naturally skewed). A run indexes the first
N abstracts (50,000 for an indicative pass).

**Matched analysis.** Standard word tokenization, lower-casing, no stop words, no stemming, on every engine. Lucene.NET uses
`StandardAnalyzer` with an empty stop set; tantivy its `default` tokenizer; FTS5 `unicode61`. Only the body text is indexed
(title is not searched).

**Matched commits.** One bulk commit after the first N-1000 documents, then five commits each after 200 further documents. The
write buffer is 256 MiB; merge policy and scheduler are the engine's defaults. Documents are added by one thread unless a run says
otherwise. Query rows run after the last commit, single-threaded, with the index opened once.

**Rows.**

| Row | Query | Engines |
|---|---|---|
| index | documents per second including the bulk commit, allocated bytes per document | all |
| commit | median and maximum latency of the five small commits | all |
| Terms1 / Terms2 / Terms5 | BM25 top 10 over a disjunction of 1, 2 or 5 terms | all |
| Phrase | two adjacent terms as an exact phrase | all |
| Prefix | a three-letter prefix | all |
| AsYouType | one full term plus a partial last word | all |
| Fuzzy1 / Fuzzy2 | one term with one / two edits, edit distance 1 / 2 | Lucene.NET, tantivy |
| Typo | one word of 6+ characters with one substituted letter (not the first), default typo budgets | Lucene.NET (FuzzyQuery, first character fixed) |
| TypoAsYouType | one full term plus a typed 6-letter beginning with a substituted letter, matched as a beginning with typos | Lucene.NET (prefix OR fuzzy: Lucene has no Levenshtein over word beginnings, so its side matches less) |
| Filtered | two terms, numeric range on `len` and three categories | Lucene.NET, tantivy |
| Facets | two terms, top ten `cat` counts over all matches | Lucene.NET |
| Stats | two terms, count/min/max/sum of `len` over all matches | Lucene.NET |
| Sort | two terms ordered by `len` descending | Lucene.NET |
| FunctionScore | two terms, BM25 times a Gaussian decay of `len` (origin the median, scale the median, 0.5 at the scale), top 10 by the product | Lucene.NET, through a collector over the `len` doc values (its public API's way to a function score; every match is visited in both engines) |

**Queries** come from `QueryGenerator`, seeded (seed 1 by default) and drawn from the corpus itself: terms occur in at least 5 and
at most a fifth of the documents, phrases are adjacent words of one abstract, fuzzy terms are corpus terms with substitutions or a
deletion. The same seed gives byte-identical query sets for every engine, and `oneindex-harness queries` writes them as JSON for
the native runners. Each row has 200 queries. `matchChecksum` (the sum of match counts, hit counts and facet counts) lets two
engines with matched analysis be compared for agreement.

## Procedure

1. **Corpus driver** (`oneindex-harness driver`, `tools/run_bench_sample.sh`): builds the index, then times each row: one warm-up
   pass, then three timed passes of the whole query set; the **median** pass gives milliseconds per query. It records, per row, the
   allocated bytes per query (managed allocations of the process), the **peak resident set** (Linux: the kernel's high-water mark,
   reset before each row through `/proc/self/clear_refs`; elsewhere the process peak since start) and the **index size** (sum of
   file lengths). Process-wide peak memory includes the loaded corpus and query sets, which is the same for every engine.
2. **BenchmarkDotNet suite** (`bench/OneIndex.Bench`, `TextRows`, `[MemoryDiagnoser]`): one benchmark per query row, one invocation
   answers the full query set (`OperationsPerInvoke`). Parameterised by the engines that can run. Environment:
   `ONEINDEX_BENCH_CORPUS`, `ONEINDEX_BENCH_DOCS`, `ONEINDEX_BENCH_WORK`. Use `--filter '*TextRows*' --inProcess` to avoid the
   auto-generated build project; drop `--job short` for publishable runs.
3. **Native, information only:** `tools/native/tantivy_rows.py` (tantivy-py in the oracle venv) and `tools/native/fts5_rows.sh`
   (the pinned `sqlite3` command-line tool) run the same queries. The FTS5 per-query time includes a small process start-up share
   because each row is one `sqlite3` invocation. Neither is a gate; the target is within 1.5x of them.

OneIndex is driven through the same `ITextEngine` adapter (`OneIndexEngine`). On the benchmark profile (body text with standard
analysis, the `len` integral column and the `cat` keyword facet column) it answers the term, Facets and Stats rows; the others are
reported as skipped ("unsupported by engine") until their query types exist. Facets: the top ten `cat` counts by frequency over all
matches together with the top page, in one pass; Stats: count, minimum, maximum and sum of `len` over all matches. The Facets and Stats
`matchChecksum` values agree with Lucene.NET's, which is how the two are checked to compute the same thing. A Terms row's checksum
differs because OneIndex prunes with block-max WAND and reports a lower-bound total.

## The machine record

Every result file carries (in `machine`, `loadBefore`, `loadDuring`, `loadAfter`, `pageCache`):

- CPU model, logical core count, instruction sets the runtime reports (SSE4.2, AVX, AVX2, FMA, BMI2, AVX-512, NEON, CRC32, dot product),
  operating system, total memory;
- storage: file system type, mount, size and free space of the index directory's volume (and whether it is a local disk);
- .NET SDK and runtime versions, GC mode;
- comparator versions (engine name and version in the result, the Python and tool versions of the native runs in their output);
- dataset: the abstracts file's SHA-256 (from `tools/fetch.sh`) and the number of documents;
- load averages (1, 5, 15 minutes) before the run, after each row and after the run;
- page cache: the index is written by the same process, so reads are served from the OS cache ("warm"). A cold-cache run states how
  the cache was dropped.

Publishable runs are exclusive: nothing else runs on the machine, which the record's load figures must show. Timings taken on a
shared or loaded host, or on a development machine, are indicative only and are labelled so.

## Gates (for reference)

At most 1.0x Lucene.NET's time as a geometric mean over the rows, no row worse than 1.25x; memory and index size are reported next
to every row. Rows with no managed comparator pass, with the reason recorded.

## Results: text rows, 302,567 abstracts

All Wikipedia abstracts of the corpus above, one run per machine of the corpus driver (`tools/run_bench_sample.sh`, rows
`Terms1,Terms2,Terms5,Phrase,Filtered,Facets,Stats`, 200 queries per row, warm-up plus three timed passes, median), run alone on the
machine as exclusive benchmark jobs; OneIndex first, then Lucene.NET 4.8.0-beta00018, same corpus file (sha256 1759e651...4893) and seed 1.
The multi-term rows (Prefix, AsYouType, Fuzzy1, Fuzzy2) are measured with the typo-tolerance work and are not in this table.
The write buffer is 256 MiB for Lucene.NET and 20,000 documents for OneIndex; both add documents from one thread with the default merge
policy. OneIndex adds with the bulk `AddUnique` path (identifiers are unique), as Lucene.NET's `AddDocument` does not look for a
previous version either. The harness runs OneIndex's merges on a background thread (`ThreadedMergeScheduler`), as Lucene.NET's default
scheduler does, so a commit does not wait for the merges it starts. A finished merge is installed by the next commit, so before the query
rows the harness commits until no merge installs (the settled index has the 200,000-document segment, five of 20,000, and the small
segments of the commit row). The library's own default scheduler is cooperative (it needs no threads): merges then run inside the commit
that polls them, which keeps results identical in the browser; applications that want Lucene.NET's behaviour pass a `ThreadedMergeScheduler`.

| Row | OneIndex x64 | Lucene.NET x64 | ratio | OneIndex arm64 | Lucene.NET arm64 | ratio |
|---|---|---|---|---|---|---|
| index (docs/s, incl. bulk commit) | 61,135 | 35,923 | 0.59 | 63,859 | 37,849 | 0.59 |
| commit (median ms, 5 x 200 docs) | 28.3 | 48.1 | 0.59 | 21.7 | 14.4 | **1.50** |
| commit (max ms) | 35.1 | 54.6 | 0.64 | 25.2 | 14.6 | 1.73 |
| Terms1 (ms/query) | 0.142 | 0.323 | 0.44 | 0.182 | 0.351 | 0.52 |
| Terms2 | 0.227 | 0.463 | 0.49 | 0.262 | 0.405 | 0.65 |
| Terms5 | 0.658 | 0.642 | 1.03 | 0.771 | 0.696 | 1.11 |
| Phrase | 0.208 | 0.168 | 1.24 | 0.248 | 0.414 | 0.60 |
| Filtered | 1.636 | 1.854 | 0.88 | 1.782 | 2.185 | 0.82 |
| Facets | 0.611 | 0.677 | 0.90 | 0.723 | 0.755 | 0.96 |
| Stats | 0.369 | 0.612 | 0.60 | 0.405 | 10.622 | 0.04 |

Geometric mean of the time ratios (index inverted): x64 0.71; arm64 0.57, or 0.79 without Stats, where Lucene.NET's arm64 figure is an
outlier. The geometric-mean bar (at most 1.0) is met on both machines. **Rows above 1.25:** the commit row on arm64 (1.50). Phrase on x64
is 1.24, just inside; earlier runs of the same code on a busier host read 1.2 to 1.4, so it sits on the line.

| Memory and size | OneIndex | Lucene.NET |
|---|---|---|
| index size after the settled merges (bytes) | 64,321,449 | 45,426,027 |
| peak resident set, index row, x64 (MiB; includes the 120 MB corpus held by the driver) | 1,253 | 1,023 |
| allocated bytes per document, index row | 4,831 | 18,117 |
| allocated bytes per query: Terms1 / Terms2 / Terms5 | 41,554 / 81,259 / 200,141 | 59,203 / 805,364 / 970,469 |
| allocated bytes per query: Phrase / Filtered / Facets / Stats | 72,174 / 265,172 / 104,644 / 82,503 | 210,813 / 649,518 / 856,765 / 801,275 |

The index is larger because OneIndex also stores each document's source and keeps a per-document identifier table and a point index
for the numeric column; Lucene.NET stores only the identifier. Phrase and Filtered match checksums agree exactly. Facets and Stats
agree to within 2 and 4 documents for the tokenization reason in the next section. Terms rows differ by design: OneIndex prunes with
block-max WAND and reports a lower bound.

**Commit on arm64.** A small commit used to write the buffered documents as a new segment of seven files (text, column, point, stored,
identifier and length parts) and fsync them together with the commit file: 8 new files and 8 fsyncs per commit against 4 files for
Lucene.NET (a compound `.cfs`, `.cfe`, `.si` and the segments file). On an M2 under APFS one flush-to-disk of a 30 KB file measured
about 2.9 ms, eight in sequence 22 ms (7 ms in parallel), so the cost of a commit there is the number of files times the cost of making
each durable, and the x64 NVMe disk hides it. A flush now writes its text, column, point, stored and identifier parts into one pack
file (commit format 3, docs/commit-format.md), so a small commit adds two files: the pack and the commit file. Durability is
unchanged: the same files are fsynced before the commit file is renamed, and the pack is checksummed like any segment file. Fsyncing
the files one after another instead of together did not help (23.7 to 31.2 ms against 30.0 to 31.8 ms, interleaved, noisy).

Final rows for that change, from the corpus driver on 50,000 abstracts (one bulk commit of 49,000, five commits of 200), three runs
per engine interleaved on each machine, the median of the three medians, run alone under the benchmark queue. "Before" is the same
harness on the code before the pack file.

| Row | machine | OneIndex now | OneIndex before | Lucene.NET | ratio now | ratio before |
|---|---|---|---|---|---|---|
| commit (median ms) | x64 | 23.5 | 30.3 | 40.7 | 0.58 | 0.74 |
| commit (median ms) | arm64 | 19.5 | 23.7 | 16.5 | **1.18** | 1.43 |
| index (docs/s, incl. bulk commit) | x64 | 38,360 | 39,490 | 17,960 | 0.47 (inverted) | 0.45 |
| index (docs/s, incl. bulk commit) | arm64 | 39,680 | 38,670 | 16,490 | 0.42 (inverted) | 0.43 |
| Terms1 (ms/query) | x64 | 0.155 | 0.157 | 0.193 | 0.80 | 0.81 |
| Terms1 (ms/query) | arm64 | 0.159 | 0.160 | 0.157 | 1.01 | 1.02 |
| files added per small commit | both | 2 | 8 | 4 | | |
| allocated bytes per document, index row | both | 5,694 | 5,091 | 21,040 | | |
| peak resident set, x64 (MiB) | x64 | 238 | 237 | 218 | | |
| index size, index row (bytes) | both | 11.64 M | 11.64 M | 8.16 M | | |

Where a small commit spent its time on the M2 before the change (milliseconds, indicative, a stopwatch around each step on a loaded
host): the flush 13 to 30 (text parts 6 to 11, stored 3 to 4, columns 0.3 to 11, and 7 to 13 of syncing the parts), the commit
protocol about 4 (temporary file sync 1.3 to 3, final sync 1.1 to 3). The syncs of seven part files were the largest single share. With
one pack file the whole five-commit sequence measured 21.7 ms against Lucene.NET's 22.0 on that host, and 19.5 against 16.5 in the
final rows above; the steps were not timed again after the change.

The arm64 commit row is inside the 1.25 bar and not yet at 1.0: what remains is one pack plus one commit file (two fsyncs and a
rename) against Lucene.NET's one compound file plus its small files, and the segment's own write. Index throughput on x64 is within
run-to-run spread of before (38.4k against 39.5k; the three runs span 36.6k to 38.5k and 38.7k to 39.6k), and the 12% rise in
allocation per document is the pack buffer. Peak resident set was not captured on arm64. Load average before / after: x64 3.05 /
1.96 (one-minute), arm64 1.81 / 1.68, flat during the runs.

**Phrase on x64 and the segment count.** The same index merged to one segment answers Phrase in 0.169 ms (Lucene.NET 0.165 to 0.168); with
the 12 segments of the settled index it is 0.20, so about 3 microseconds per segment per query is the per-segment cost of looking up two
terms and positioning two iterators. A skip over position varints with 16-byte vector steps was built and timed (interleaved with the
scalar version, same host): 0.249 and 0.240 ms against 0.241 and 0.261 ms, no difference, so it was removed. The phrase scorer already
leads with the rarest term and advances the others only to its documents.

Machines: x64 AMD Ryzen 9 7950X3D, 16 cores / 32 threads (SSE4.2, AVX2, FMA, BMI2, AVX-512), NVMe ext4, Linux Mint 22.3, .NET SDK
10.0.402 / runtime 10.0.13, workstation GC; 1-minute load average 2.6 before the OneIndex run, 2.6 to 2.8 during and 2.7 after the
Lucene.NET run (the 15-minute figure, 8.4, still carried an earlier job); arm64 Apple M2, 8 cores, 8 GiB, APFS, macOS, .NET SDK 10.0.403 /
runtime 10.0.14, load 4.8 before, 3.9 after the OneIndex run and 3.3 after the Lucene.NET run (the Mac was not idle: a load of 3 to 4 is
its baseline). Both indexes were warm in the page cache. Peak resident set was not captured on arm64.

Native engines, information only (x64, same queries, tantivy-py 0.27.0 with its Python binding overhead, SQLite 3.54.1 command-line with
the process start-up share in each row), one run of the corpus driver's native runners on the same machine:

| Row | tantivy-py (ms/query) | SQLite FTS5 (ms/query) | OneIndex (ms/query) |
|---|---|---|---|
| index (docs/s) | 129,446 | 91,774 (3.30 s for 302,567) | 61,135 |
| Terms1 / Terms2 / Terms5 | 0.046 / 0.104 / 0.224 | 3.75 / 7.53 / 18.1 | 0.142 / 0.227 / 0.658 |
| Phrase | 0.056 | 0.66 | 0.208 |
| Filtered | 0.415 | n/a | 1.636 |

OneIndex is 2 to 4 times tantivy-py on query rows here, outside the 1.5x target, which is not a gate. Tantivy's index is 49.0 MB,
OneIndex's 64.3 MB, Lucene.NET's 45.4 MB.

### Counting differences in the Facets and Stats rows

At 302,567 documents the match checksums of the Facets and Stats rows differ by 2 and 4 between the engines. The cause is tokenization, not
a collection bug: six abstracts contain U+202F NARROW NO-BREAK SPACE, which the Unicode 17 word-break property file lists as ExtendNumLet
(UAX #29 rule WB13b), so OneIndex keeps "they\u202fare" as one token, as the standard prescribes, while the comparator's older tables break there. Four
of the six documents hold a query term next to such a space; the single-term counts (`oneindex-harness count-diff`) differ for exactly those
terms, each by one document that only the comparator counts.

## Earlier indicative pass (not publishable)

A first run of the driver on 50,000 abstracts, to prove the rows run end to end and to capture memory and size. It ran as a shared-queue
job on the worker beside other jobs (load average 11 to 12 on 32 logical cores before and after; the machine was **not** quiet), so the
absolute numbers are indicative only and no ratio may be drawn from them. Code at
commit `1956a0d`. Machine: AMD Ryzen 9 7950X3D (32 logical cores; SSE4.2, AVX, AVX2, FMA, BMI2, AVX-512 F/BW), 124 GiB, NVMe ext4, Linux Mint
22.3, .NET SDK 10.0.402, runtime 10.0.13, workstation GC; Lucene.NET 4.8.0-beta00018; tantivy-py 0.27.0; SQLite 3.54.1. Dataset: abstracts
SHA-256 `1759e651…4893` (the generator was run twice on the worker with identical output), first 50,000 lines,
seed 1, 200 queries per row, median of 3 passes, single indexing thread, index freshly written (warm page cache).

Lucene.NET (index 49,000 documents in one bulk commit: 13,188 documents/s including the 1.25 s commit; 20.7 KB allocated per document;
peak resident 288 MiB; five 200-document commits: median 66 ms, maximum 75 ms; index 8.5 MB):

| Row | ms/query | allocated KB/query | peak resident MiB |
|---|---|---|---|
| Terms1 | 0.254 | 57 | 233 |
| Terms2 | 0.554 | 787 | 174 |
| Terms5 | 0.462 | 944 | 172 |
| Phrase | 0.145 | 211 | 174 |
| Prefix | 0.218 | 106 | 174 |
| AsYouType | 0.131 | 143 | 170 |
| Fuzzy1 | 0.388 | 586 | 175 |
| Fuzzy2 | 1.620 | 2,116 | 177 |
| Filtered | 0.517 | 605 | 179 |
| Facets | 0.408 | 804 | 179 |
| Stats | 0.285 | 780 | 183 |
| Sort | 0.302 | 786 | 183 |

Native, information only (same queries): tantivy-py indexed 50,000 documents at about 70,600 documents/s (peak resident 74 MiB; a
single-threaded writer with a 256 MB heap) and answered Terms1 to Filtered in 0.012 (Terms1), 0.037, 0.070, 0.022 (Phrase), 0.086
(Prefix), 0.026 (AsYouType), 0.219 (Fuzzy1), 1.125 (Fuzzy2) and 0.081 (Filtered) ms/query; SQLite FTS5 loaded the same documents in 0.53 s
of wall time per 50,000 insert statements and answered Terms1, Terms2, Terms5, Phrase, Prefix and AsYouType in 0.58, 1.38, 3.13, 0.23,
2.79 and 0.38 ms/query including its process start-up share. tantivy-py figures include the Python binding overhead; they are for orientation, not comparison. These native numbers come from the same
noisy run.

The full JSON (all rows, load figures, machine record) is produced by the scripts above; stage-close runs replace this section.
