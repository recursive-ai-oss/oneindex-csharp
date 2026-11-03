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

`bench/OneIndex.Bench` also has `TermDictionaryRows`: term expansion over a dictionary alone (set `ONEINDEX_BENCH_TERMS` to a file of `term<TAB>document frequency` lines in byte order; `ONEINDEX_TERMS_QUICK=1` prints a stopwatch pass with a checksum of what matched, `ONEINDEX_TERMS_VERSION=1` builds the older node layout for comparison). It isolates the dictionary walk from postings and scoring.
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

**Commit on arm64.** A small commit writes the buffered documents as a new segment (text, column, point, stored and identifier files) and
fsyncs them together with the commit file: 9 fsyncs per commit against 8 for Lucene.NET, counted with a system-call trace on x64. On the
x64 disk that is faster than Lucene.NET; on the M2 it is 7 ms slower and the rest of the gap was not found. Fsyncing the segment files one
after another instead of together did not help (23.7 to 31.2 ms against 30.0 to 31.8 ms, interleaved, noisy).

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

### Fuzzy, prefix and as-you-type rows, term dictionary version 2

Same corpus, driver, settings and Lucene.NET build as above, after the version 2 term dictionary, the single-load number reads, the
merge policy size ratio of 2 and the block-max WAND change that keeps clause order between rounds. Each engine ran as its own exclusive
benchmark-class job, Lucene.NET right after OneIndex on the same machine (x64), three repetitions, medians. Match checksums of the Fuzzy,
Typo and Prefix rows differ by design (OneIndex prunes with WAND and prefix queries are constant-score; Lucene.NET expands and counts
all matches); the as-you-type typo row has a matched variant (`ONEINDEX_TYPO_MATCHED=1`, same query shape as the comparator's
prefix-OR-fuzzy) whose checksums agree (2,925).

| Row (ms/query) | OneIndex x64 | Lucene.NET x64 | ratio | OneIndex arm64 | Lucene.NET arm64 | ratio |
|---|---|---|---|---|---|---|
| index (docs/s) | 62,540 | 37,947 | 0.61 | 62,869 | 37,849 | 0.60 |
| commit (median ms, 5 x 200 docs) | 28.3 | 52.4 | 0.54 | 20.7 | 14.5 | 1.43 |
| Terms1 | 0.123 | 0.214 | 0.58 | 0.179 | 0.318 | 0.56 |
| Terms2 | 0.245 | 0.436 | 0.56 | n/r | n/r | |
| Terms5 | 0.487 | 0.635 | 0.77 | 0.620 | 0.752 | 0.82 |
| Phrase | 0.179 | 0.157 | 1.14 | 0.250 | 0.389 | 0.64 |
| Prefix | 0.105 | 0.317 | 0.33 | 0.124 | 0.410 | 0.30 |
| AsYouType | 0.151 | 0.198 | 0.76 | 0.234 | 0.223 | 1.05 |
| Fuzzy1 | 0.232 | 0.530 | 0.44 | 0.277 | 0.945 | 0.29 |
| Fuzzy2 | 1.227 | 2.083 | 0.59 | 1.386 | 4.072 | 0.34 |
| Typo | 0.211 | 0.484 | 0.43 | 0.228 | 0.699 | 0.33 |
| TypoAsYouType (matched variant) | 0.081 | 0.173 | 0.47 | 0.087 | n/r | |
| TypoAsYouType (documented semantics, information) | 0.242 | 0.173 | 1.40 | n/r | | |
| Filtered | 1.540 | 1.888 | 0.82 | n/r | | |
| Facets / Stats / Sort | 0.427 / 0.276 / 0.370 | 0.601 / 0.514 / 0.668 | 0.71 / 0.54 / 0.55 | n/r | | |

n/r: not re-run for this change. The arm64 Lucene.NET column and the arm64 Terms1, Phrase, Prefix, AsYouType and TypoAsYouType
figures come from the earlier benchmark-class run of the same Lucene.NET build and an earlier build of the dictionary code; Terms5,
Fuzzy1, Fuzzy2, Typo and commit on arm64 were re-run on the final code (they did not move with the clause-order change: Fuzzy2 1.395 to
1.386 ms). Commit on arm64 is the one row above 1.25; it is governed by the number of files fsynced per commit, not by the dictionary.
Allocated bytes per query, OneIndex against Lucene.NET (x64): Terms1 4,982 / 59,203; Terms5 21,054 / 970,469; Prefix 62,868 / 153,187;
Fuzzy1 19,456 / 703,984; Fuzzy2 120,550 / 2,281,455; Typo 19,311 / 1,244,174; AsYouType 57,659 / 189,760.

Size and memory (x64): settled index 60,589,958 bytes (62 MB before the merge policy change was 64.3 MB; Lucene.NET 45,426,027); peak
resident set during the index row 1,269 MiB against 1,021 MiB (includes the 120 MB corpus held by the driver), during the query rows
443 to 1,171 MiB against 434 to 603 MiB.

Machine records: x64 as above (Ryzen 9 7950X3D, 32 logical cores, ext4 NVMe, .NET 10.0.13, warm page cache). 1-minute load average
before / during / after: OneIndex run 3.3 / 3.0 / 2.5, Lucene.NET run 7.3 / 5.9 / 5.2 (other users' light jobs on the host), matched
typo run 1.3 / 1.2 / 1.2. arm64: Apple M2, 8 cores, .NET 10.0.14, load 3.0 / 2.9 / 2.9.

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
