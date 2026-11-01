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
| Filtered | two terms, numeric range on `len` and three categories | Lucene.NET, tantivy |
| Facets | two terms, top ten `cat` counts over all matches | Lucene.NET |
| Stats | two terms, count/min/max/sum of `len` over all matches | Lucene.NET |
| Sort | two terms ordered by `len` descending | Lucene.NET |

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
previous version either. A OneIndex commit does not wait for the merges it starts; Lucene.NET's does not either. The query rows run after
the merges have settled.

| Row | OneIndex x64 | Lucene.NET x64 | ratio | OneIndex arm64 | Lucene.NET arm64 | ratio |
|---|---|---|---|---|---|---|
| index (docs/s, incl. bulk commit) | 53,249 | 36,542 | 0.69 | 60,162 | 37,163 | 0.62 |
| commit (median ms, 5 x 200 docs) | 69.4 | 35.4 | **1.96** | 32.2 | 12.0 | **2.68** |
| Terms1 (ms/query) | 0.145 | 0.275 | 0.53 | 0.161 | 0.321 | 0.50 |
| Terms2 | 0.228 | 0.463 | 0.49 | 0.255 | 0.466 | 0.55 |
| Terms5 | 0.665 | 0.644 | 1.03 | 0.774 | 0.706 | 1.10 |
| Phrase | 0.214 | 0.165 | **1.30** | 0.247 | 0.243 | 1.02 |
| Filtered | 1.653 | 1.854 | 0.89 | 1.823 | 2.160 | 0.84 |
| Facets | 0.592 | 0.679 | 0.87 | 0.746 | 0.810 | 0.92 |
| Stats | 0.376 | 0.601 | 0.63 | 0.404 | 10.623 | 0.04 |

Geometric mean of the time ratios (index inverted): x64 0.80, arm64 0.38 (0.74 without Stats, where Lucene.NET's arm64 figure is an outlier).
The bar (at most 1.0 geometric mean, no row above 1.25) is met on the geometric mean on both machines. **Rows above 1.25:** commit on
both machines and Phrase on x64.

| Memory and size | OneIndex | Lucene.NET |
|---|---|---|
| index size after the commits (bytes) | 64,321,449 | 45,426,027 |
| peak resident set, index row, x64 (MiB; includes the 120 MB corpus held by the driver) | 1,254 | 1,023 |
| allocated bytes per document, index row | 4,831 | 18,114 |
| allocated bytes per query: Terms1 / Terms2 / Terms5 | 41,476 / 80,687 / 198,619 | 59,203 / 805,364 / 970,469 |
| allocated bytes per query: Phrase / Filtered / Facets / Stats | 71,512 / 262,130 / 104,488 / 82,347 | 210,813 / 649,518 / 856,765 / 801,275 |

The index is larger because OneIndex also stores each document's source and keeps a per-document identifier table and a point index
for the numeric column; Lucene.NET stores only the identifier. The match checksums of the Facets and Stats rows agree between the
engines to within a few documents (Facets 2,595,703 against 2,595,705, Stats 2,520,866 against 2,520,870; the difference is not explained
yet), Phrase and Filtered agree exactly. Terms rows differ by design: OneIndex prunes with block-max WAND and reports a lower bound.

**Why the commit row misses.** A small commit writes the buffered documents as a new segment of several files (text, column, point, stored, identifiers),
and fsyncs each (about 5 ms each on the x64 benchmark disk, measured with a throwaway script); this is the working explanation, not a
profile. Lucene.NET's small flush is understood to write fewer files. Closing the gap needs a segment layout with fewer files (or one file per segment), a format
change, and is not done here. **Why Phrase misses on x64:** the phrase scorer decodes positions of every term in each candidate
document; a skip-aware lead-term strategy was not tried.

Machines: x64 AMD Ryzen 9 7950X3D, 16 cores / 32 threads (SSE4.2, AVX2, FMA, BMI2, AVX-512), NVMe ext4, Linux Mint 22.3, .NET SDK
10.0.402 / runtime 10.0.13, workstation GC; 1-minute load average 3.7 before the OneIndex run and 3.2 after it (3.1 to 3.1 around the Lucene.NET run); the 5- and 15-minute
averages (13 and 22) still carried the earlier jobs; arm64 Apple M2, 8 cores, 8 GiB,
APFS, macOS, .NET SDK 10.0.403 / runtime 10.0.14, load 4.8 before, 4.2 after. Both indexes were warm in the page cache. Peak resident set
was not captured on arm64 (the kernel high-water mark used on Linux does not exist there).

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
