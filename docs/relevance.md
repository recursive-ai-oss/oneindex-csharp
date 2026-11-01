# Relevance: method

How retrieval quality is measured on the BEIR sets, with the comparator side first. Speed is in [benchmarks.md](benchmarks.md).

## Datasets

BEIR test splits of **SciFact**, **ArguAna**, **NQ** and **SCIDOCS**, fetched and hash-checked by `tools/fetch.sh` (BEIR zips; the MD5s
are BEIR's own, the SHA-256s are ours; both are in `tools/pins.tsv`). Datasets are never committed. Queries are the test queries that
have judgements; judgements are the `qrels/test.tsv` grades. Other BEIR sets are not used.

## Runner

`oneindex-harness beir` (`tools/run_relevance.sh`) drives any `ITextEngine`: it indexes the corpus (title and text), retrieves the top
1,000 documents for every test query under each BM25 setting, writes a TREC run file per setting, and scores it.

- **Settings.** Three field layouts times two parameter sets, so six runs per dataset:
  - layouts: `Flat` (title and text concatenated into one field, Kamalloo et al.'s "flat"); `MultiFieldSum` (title and text as two
    fields, scores summed with equal weights, Kamalloo et al.'s "multifield" and the Anserini configuration the BEIR paper describes);
    `MultiFieldMax` (the better of the two fields' scores, Elasticsearch's `best_fields`, the BEIR leaderboard's original
    Elasticsearch run);
  - parameters: k1=0.9, b=0.4 (Anserini's defaults, the ones the BEIR paper states) and k1=1.2, b=0.75 (Lucene's defaults).
- **Analysis.** The engine's English chain: lower-casing, possessive removal, English stop words and Porter-family stemming. Queries
  are analysed with the same chain and the terms OR-ed within each field.
- **nDCG@10.** Our implementation (`TrecEval.NdcgAt`) follows trec_eval: gain is the judged grade, discount 1/log2(rank+1), ranking by
  score descending with ties broken by document id descending, the mean taken over queries present in both run and judgements, ideal
  DCG from all positively graded judgements. Two numbers are reported per run: the raw one, and the **BEIR-convention** one, which
  drops a hit whose document id equals the query id before scoring (BEIR's default; it matters for ArguAna, whose queries are
  documents of the corpus).
- **Cross-check.** `tools/crosscheck_ndcg.py` evaluates the same run files with `pytrec_eval` (`ndcg_cut_10`), the evaluator BEIR
  uses, and fails if any run differs from ours by more than 5e-5 (agreement to four decimals).

## Published baselines

Quoted with their source; "ours" are compared with both.

- **BEIR paper:** Thakur, Reimers, Rücklé, Srivastava, Gurevych, *BEIR: A Heterogeneous Benchmark for Zero-shot Evaluation of
  Information Retrieval Models*, NeurIPS 2021 Datasets and Benchmarks, arXiv:2104.08663 (v4), Table 2, BM25 column. The text describes the
  lexical baseline as Anserini with k1=0.9 and b=0.4, title and passage indexed as separate fields.
- **Kamalloo et al.:** *Resources for Brewing BEIR: Reproducible Reference Models and an Official Leaderboard*, arXiv:2306.07471
  (SIGIR 2024), Table 3, BM25 columns "multifield" (the BEIR paper's configuration, equal weights over title and contents), "flat" (title
  and text in one field), both using Lucene's default English analyzer.

| Dataset | BEIR paper Table 2 | Kamalloo Table 3 multifield | Kamalloo Table 3 flat |
|---|---|---|---|
| SciFact | 0.665 | 0.665 | 0.679 |
| ArguAna | 0.315 | 0.414 | 0.397 |
| NQ | 0.329 | 0.329 | 0.306 |
| SCIDOCS | 0.158 | 0.158 | 0.149 |

The ArguAna figures differ between the two papers as printed (0.315 and 0.414); we report against both and do not reconcile them.

## Gate

OneIndex BM25 nDCG@10 on each set is at most one point below the published baseline (above it is fine) and not below Lucene.NET
at matched settings: OneIndex's default k1 and b against Lucene.NET at the same layout, k1 and b.

## Results: Lucene.NET 4.8.0-beta00018 (the comparator side)

Engine: Lucene.NET `4.8.0-beta00018` (assembly build `commit:[df99b8abd9]`; packages Lucene.Net, Lucene.Net.Analysis.Common, Lucene.Net.Facet) on .NET 10.0.13 (SDK 10.0.402), Linux x64.
Analysis: `EnglishAnalyzer` (lower-case, possessive removal, English stop words, Porter stemming) for documents and queries. Top 1,000 per
query, test split. Worker: 32 logical cores (AMD Ryzen 9 7950X3D), NVMe ext4, 8 indexing and search threads. One run covered all four sets and the pytrec_eval cross-check; an earlier attempt produced identical nDCG values. Code at commit `1956a0d`.

Datasets (SHA-256 of the zip, MD5 matches BEIR): SciFact `536e1444…0165` (5,183 documents, 300 queries), ArguAna `cfdf79ad…557b` (8,674; 1,406),
SCIDOCS `96640201…17ef1` (25,657; 1,000), NQ `2553bf7b…e752` (2,681,468; 3,452). Full hashes: `tools/pins.tsv`.

nDCG@10 (BEIR convention in parentheses where it differs; it drops a hit whose id equals the query id):

| Run (layout, k1/b) | SciFact | ArguAna | SCIDOCS | NQ |
|---|---|---|---|---|
| Flat, 0.9/0.4 | 0.6789 | 0.3111 (0.4118) | 0.1509 | 0.3058 |
| Flat, 1.2/0.75 | 0.6848 | 0.3642 (0.4882) | 0.1566 | 0.2775 |
| MultiFieldSum, 0.9/0.4 | 0.6647 | 0.3217 (0.4240) | 0.1587 | 0.3252 |
| MultiFieldSum, 1.2/0.75 | 0.6628 | 0.3632 (0.4880) | 0.1619 | 0.3051 |
| MultiFieldMax, 0.9/0.4 | 0.6685 | 0.2877 (0.3784) | 0.1461 | 0.2437 |
| MultiFieldMax, 1.2/0.75 | 0.6684 | 0.3414 (0.4584) | 0.1480 | 0.2309 |
| **Published: BEIR paper Table 2** | 0.665 | 0.315 | 0.158 | 0.329 |
| **Published: Kamalloo Table 3 multifield** | 0.665 | 0.414 | 0.158 | 0.329 |
| **Published: Kamalloo Table 3 flat** | 0.679 | 0.397 | 0.149 | 0.306 |

Reading: with k1=0.9, b=0.4, Lucene.NET reproduces the Kamalloo flat column on SciFact (0.6789 vs 0.679) and NQ (0.3058 vs 0.306) to three
decimals and its multifield column on SciFact (0.6647 vs 0.665), SCIDOCS (0.1587 vs 0.158) and, with the BEIR convention, ArguAna
(0.4240 vs 0.414, 1.0 point high). NQ MultiFieldSum is 0.3252 against the published 0.329 (0.4 point low). The BEIR paper's ArguAna
value (0.315) is close to the raw, convention-free figure (0.3217), the Kamalloo values to the BEIR-convention figures; the two papers'
ArguAna numbers do not agree with each other, so the gate on ArguAna should be read against Kamalloo with the convention applied.
`MultiFieldMax` (Elasticsearch best_fields over the two fields) is clearly worst on NQ, where the title field alone matches many
irrelevant documents; it is reported for completeness, not as a target. Lucene defaults (1.2/0.75) beat 0.9/0.4 on ArguAna and SCIDOCS
and SciFact-flat, 0.9/0.4 on NQ.

pytrec_eval 0.5.11 (`ndcg_cut_10`) on the same run files agrees with our nDCG to better than 2e-15 on all 48 run files (raw and
BEIR-convention variants of 24 runs): four decimals and far beyond.

Indexing time (8 threads, one commit): SciFact 1.8 s, ArguAna 1.0 s, SCIDOCS 1.6 s, NQ 26.8 s (index 1.1 GiB). All six settings for NQ
answered 3,452 queries in 15 to 22 s each.

## Results: OneIndex against Lucene.NET and the baselines

Run on the worker (32 logical cores, AMD Ryzen 9 7950X3D, NVMe ext4), .NET 10 (SDK 10.0.4xx), 8 threads, same datasets and hashes as above.
OneIndex defaults: `Bm25F` layout (one score per term over `title` and `text`, title weight 3), k1 = 0.9, b = 0.4, English analysis
(lower-case, possessive removal, Snowball English stop words, Snowball English stemmer). NQ was indexed and searched in separate
runs (index phase, then a search phase on the reopened index). Every run file was cross-checked with
pytrec_eval (`ndcg_cut_10`); the largest difference to our nDCG@10 is 1.8e-15.

nDCG@10, BEIR convention (a hit whose id equals the query id is dropped):

| Set | OneIndex `Bm25F` 0.9/0.4 (default) | OneIndex `Bm25F` 1.2/0.75 | Lucene.NET `MultiFieldSum` 0.9/0.4 | Lucene.NET `MultiFieldSum` 1.2/0.75 | Lucene.NET `Flat` 0.9/0.4 | Published baseline (multifield) |
|---|---|---|---|---|---|---|
| SciFact | **0.6912** | 0.6998 | 0.6647 | 0.6628 | 0.6789 | 0.665 |
| ArguAna | **0.4333** | 0.5072 | 0.4240 | 0.4880 | 0.4118 | 0.414 |
| SCIDOCS | **0.1531** | 0.1601 | 0.1587 | 0.1619 | 0.1509 | 0.158 |
| NQ | **0.3420** | 0.3377 | 0.3252 | 0.3051 | 0.3058 | 0.329 |

SciFact (2.6 points) and ArguAna (1.9) are more than one point *above* the baseline, NQ is 1.3 above; SCIDOCS is 0.2 below it. The
Lucene.NET figures are those of the section above (re-run once more in the equal-weight job; identical to four decimals).

OneIndex with the same layout as Lucene.NET's `MultiFieldSum` (two separate field scores added, title weight 1, so no BM25F):

| Set | OneIndex `MultiFieldSum`, title weight 1, 0.9/0.4 | Lucene.NET `MultiFieldSum` 0.9/0.4 | OneIndex, 1.2/0.75 | Lucene.NET, 1.2/0.75 |
|---|---|---|---|---|
| SciFact | 0.6598 | 0.6647 | 0.6653 | 0.6628 |
| ArguAna | 0.4270 | 0.4240 | 0.4861 | 0.4880 |
| SCIDOCS | 0.1587 | 0.1587 | 0.1641 | 0.1619 |
| NQ | 0.3323 | 0.3252 | 0.3114 | 0.3051 |

At matched layout and parameters OneIndex's BM25 scores equal Lucene.NET's within noise (identical on SCIDOCS at 0.9/0.4, above on ArguAna and NQ, 0.5 point below on SciFact).

### SCIDOCS against Lucene.NET `MultiFieldSum`

`Bm25F` (0.1531) is 0.56 points below Lucene.NET's `MultiFieldSum` (0.1587) on SCIDOCS, and above its `Flat` layout (0.1509). Measured causes:

- **It is the layout, not the scoring or the analysis.** The equal-weight sum in OneIndex reproduces Lucene.NET to four decimals on SCIDOCS
  (0.1587 / 0.1587). Lucene.NET's `Explain` output (public API) shows the same idf and tf formula as OneIndex's: idf `ln(1 + (N - n + 0.5)/(n + 0.5))`,
  `tf (k1+1) / (tf + k1 (1 - b + b len/avgLen))`.
- **Analysis is not it.** Experimental builds (not kept) with Lucene's 33-word stop list and the original Porter stemmer moved the sum layout
  by at most 0.1 point either way (0.1552 default, 0.1548 Lucene stop list, 0.1551 Porter, 0.1543 both), while the title weight moved it 0.35 (the
  harness had given the sum layout OneIndex's title weight 3 until this run; at weight 1 it is 0.1587). With the same stop list the two
  analyzers' token streams differ in 20 of 6,000 sampled texts (stems of short words); with each side's own list they differ in 5,286, almost
  all by stop words.
- **Collection size and average length are not it.** Counting empty-field documents in N and average length (two variants, run on all three
  small sets) changed SCIDOCS `Bm25F` by at most 0.04 points.
- **What decides it:** SCIDOCS (queries are paper titles, relevant documents are cited papers) rewards a document whose title *and* abstract both match,
  which adding two saturating field scores does and BM25F (one saturation over the weighted sum) does not. Title weight sweep, `Bm25F` 0.9/0.4:

| Title weight | SciFact | ArguAna | SCIDOCS |
|---|---|---|---|
| 1 | 0.6838 | 0.4188 | 0.1502 |
| 1.5 | 0.6878 | 0.4248 | 0.1517 |
| 2 | 0.6872 | n/a | 0.1520 |
| 3 (default) | 0.6912 | 0.4333 | 0.1531 |

  No weight reaches 0.1587 on SCIDOCS while all four sets keep their other results; the sum layout wins on SCIDOCS and loses 3 points on
  SciFact and 1 on NQ against `Bm25F`. BM25F is the engine's documented base score, so it stays the default; the sum layout is a query composition
  (`BooleanQuery` of per-field `TermQuery`) any caller can write.

### Operational notes

- Loading NQ takes 173 s at 8 threads (index 746 MiB; Lucene.NET: 26.8 s, 1.1 GiB), commit and merges included; `IndexWriter.AddUnique` skips the
  per-document identifier lookup that made the first attempt time out at 30 minutes. Reopening the finished index and reading 2.68 M identifiers takes 36 s;
  the 3,452 queries take 12 to 25 s per setting.
- The runner takes `ONEINDEX_PHASE=index|search` (build and commit in one job, search in another), `ONEINDEX_LAYOUTS`, `ONEINDEX_WORK` and the
  OneIndex adapter `ONEINDEX_TITLE_WEIGHT`.

## Typo tolerance on (information)

`ONEINDEX_TYPO=on` makes the OneIndex adapter match the Bm25F layout with `TypoTermQuery` (default typo budgets on the analyzed word, the
typo and exactness factors, no prefix matching). It is information only; the gated runs leave it off, where queries are exactly the plain
BM25F term queries.
