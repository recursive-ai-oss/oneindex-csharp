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

OneIndex BM25 nDCG@10 on each set is within 0.01 of the published baseline and not below Lucene.NET on the same run, taking the
matching setting (the OneIndex default is compared with the closest Lucene.NET setting). The OneIndex adapter (`OneIndexEngine`)
is filled in when its scorer and queries exist; the runner needs no change.

## Results

See the tables below, appended per run with the worker job that produced them.
