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

OneIndex is driven through the same `ITextEngine` adapter (`OneIndexEngine`). It reports itself unavailable until index writing,
scoring and query execution exist; no row is skipped silently, the driver exits with code 3 and says why.

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
