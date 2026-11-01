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

## Indicative pass (not publishable)

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
