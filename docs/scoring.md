# Scoring

How a document's score is made, what the statistics cover, the defaults and why, and the explain format. Query semantics are in [queries.md](queries.md).

## Formula: BM25F

Robertson and Zaragoza, *The Probabilistic Relevance Framework: BM25 and Beyond* (2009), section 3.6, equations 3.19 to 3.21, with the length normalization of section 3.4.5.

For a term *t* searched in fields *F* (one field for a `TermQuery`, several for a `MultiFieldTermQuery`) and a document *d*:

    B_f   = (1 - b) + b * len_f(d) / avgLen_f            length normalization of field f
    x     = sum over f in F of  w_f * tf_f(d) / B_f      weighted, normalized frequency (the paper's tf~)
    score = boost * idf(t) * x / (k1 + x)                saturation of the total evidence
    idf   = ln(1 + (N - n + 0.5) / (n + 0.5))

- `w_f` is the field's schema boost (`FieldDefinition.Boost`, default 1). `tf_f` is the number of occurrences of the term in the field of the document, `len_f` the number of tokens of the field (stacked tokens such as an added stem count), `avgLen_f` the average length over the documents that have the field.
- **Saturation is applied to the sum**, not to each field: a term repeated across title and body does not outscore a term whose combined weighted frequency is the same (paper section 3.6.1). The per-field scores of two `TermQuery`s in a boolean add up after saturation instead; use `MultiFieldTermQuery` for BM25F.
- `k1` (default 0.9) and `b` (default 0.4) are common to all fields. `b = 0` switches length normalization off, `k1 = 0` makes the score binary.
- `idf` is the Robertson-Sparck Jones weight without relevance information, in the non-negative form (paper equations 3.2 and 3.3). For several fields `N` is the largest document count of the fields and `n` the largest document frequency of the term in any of them. The paper computes the idf "on the entire collection disregarding streams" and says a better definition needs research; the exact union frequency would need a pass over the postings, so the largest per-field frequency stands in for it. **Documented deviation**: for a term that appears in disjoint sets of documents in two fields, `n` is smaller than the union, so its idf is a little larger than the paper's. A try with the rarest field's relative frequency instead scored 2 to 4 nDCG points lower on SciFact and SCIDOCS.
- A phrase is scored as one pseudo-term in its field: `tf` is the phrase frequency (queries.md), the `idf` is the sum of the terms' idf values. Expansions (prefix, fuzzy, regex) are the sum of the matching terms' scores. A boolean query adds the scores of its matching `Must` and `Should` clauses in clause order, then multiplies by its boost. Range, match-all and filter clauses score a constant (the boost) or 0.
- The query's `Boost` multiplies a node; a term's boost is folded into its `idf` factor.

Scores are single-precision floats computed in a fixed order, so the same snapshot gives the same bits whether block-max WAND skipped documents or not.

## Block bounds for WAND

For a block of a postings list the postings carry the largest frequency and the shortest field length of its documents (docs/postings.md). `x` grows with the frequency and shrinks with the length, and the score grows with `x`, so the bound of a block is the score of (largest frequency, shortest length), summed over the term's fields. A disjunction keeps these bounds per clause and skips a run of documents when the sum of its clauses' bounds cannot reach the lowest score still in the top k. The bound of a phrase, a conjunction or a boolean is the sum of its parts' bounds over the whole list (no block skipping inside them). Pruning only changes `TotalHits`, never the documents returned or their scores.

## Statistics scope

`N`, `n` and `avgLen` are summed over every segment of the snapshot when the weight is made, so every document scores on the same numbers. Documents deleted but not yet merged away still count. Different snapshots of the same data (a merge happened, a document was updated) can therefore score a document slightly differently.

## Parameters: why k1 = 0.9, b = 0.4

The paper gives no rule for the two constants (it reports that 0.5 < b < 0.8 and 1.2 < k1 < 2 are often good). The defaults are the values of the BEIR lexical baseline (Thakur et al. 2021, setup of Anserini BM25 with k1 = 0.9, b = 0.4), chosen so that the gate sets can be compared with the published baselines. Results on the four gate sets for both parameter pairs are in the table below. Lucene's pair (1.2, 0.75) is better on ArguAna and about even elsewhere; it remains a one-line option (`QueryEngineOptions.Bm25`). The title weight of the benchmark schema, 3, was picked from {0.5, 1, 2, 3, 5, 10} on SciFact and SCIDOCS (flat above 3; ArguAna measured at 1, 2 and 3); it is a schema property, not an engine constant. A sweep of 1, 1.5, 2 and 3 on SciFact, ArguAna and SCIDOCS improved every set up to 3 (relevance.md).

## Explain

`IndexSearcher.Explain(query, globalDocId)` returns an `Explanation` tree whose root value equals the score returned by the search, bit for bit (a test checks it on every random case). Nodes:

- term: `term '<text>': boost * idf * x / (k1 + x), k1 = ...`, with children
  - `idf = ln(1 + (N - n + 0.5) / (n + 0.5)), n = ..., N = ...`
  - `x = sum over fields of weight * tf / ((1 - b) + b * length / avgLength)`, with one child per field that holds the term: `field <name>: weight <w> * tf <tf> / ((1 - b) + b * length <len> / avgLength <avg>), b = <b>`
- phrase: `phrase in <field>: boost * idf * x / (k1 + x)` with the idf sum and the phrase frequency (`sum of 1 / (1 + gap) over matches, slop <s>`)
- boolean: `sum of matching clauses, boost <b>, <m> of <n> should clauses matched (need <k>)`, children are the matching clauses (a filter clause is shown with value 0)
- constant matches (range, match all): `<description>, constant score = boost <b>`

- function score: `function score: query score <q> and function score <f> by <mode>, boost <b>`, with the inner explanation and the functions' values as children ([function-scores.md](function-scores.md))

`null` means the document does not match.

On the happy path, `SearchQuery.Explain()` returns this tree on each hit (`SearchHit.Explanation`); its root value equals `SearchHit.Score` exactly, on the plain, sorted, faceted and function-scored paths (a test asserts it on every hit).

## Typo and exactness factors

User text with typo tolerance or as-you-type expands a query word to the indexed words it can match (`docs/typo-tolerance.md`). Each
expanded word is an ordinary BM25F term (its own idf) with a boost:

    boost = typo factor * exactness factor
    typo factor      = 1 / (1 + typos)        typos 0, 1, 2 -> 1, 0.5, 0.333
    exactness factor = 1 whole word, 0.8 a longer word that only begins like the query word

A document's score is the sum over the expansions it contains, as for any disjunction. `Explain` lists every matched expansion:

    query word 'planet' matched 'plnaet' (whole word with typos, 1 typos): typo factor 0.5 = 1 / (1 + typos), exactness factor 1, boost 0.5

with the term's own explanation below it. With typos and prefix matching off the query is a plain term query: no factor, same scores
as before. The other Meilisearch-style rules are optional ranking rules ([ranking-rules.md](ranking-rules.md)).

## Term proximity

User text with two or more words (not quoted phrases) adds a proximity bonus to the BM25F score: documents whose query words occur close
together, in any order, score higher. It uses the positions already in the postings; no index format changes.

Büttcher, Clarke and Lushman, "Term proximity scoring for ad-hoc retrieval on very large text collections", SIGIR 2006, define it. In a field of
the document, take the occurrences of the query words in position order. Every two neighbours in that order that are occurrences of
*different* words (no other query word occurrence between them) at distance *d* add `idf(other) / d²` to the accumulator of each word:

    acc_t   = sum over adjacent pairs (t, u) at distance d of  idf(u) / d^2        (d >= 1; two words at one position count as 1)
    K       = k1 * (1 - b + b * len_f / avglen_f)                                   the BM25 length normalisation of field f
    bonus_f = sum over query words t of  min(1, idf(t)) * acc_t * (k1 + 1) / (acc_t + K)
    score   = bm25f(d) + weight * max over fields f of bonus_f

- `idf(t)` is the word's own idf, as in the BM25F term; `k1` and `b` are the BM25F parameters; `len_f` and `avglen_f` are the field's length in
  the document and its average. A word with typo or prefix expansions uses the positions of all its matched terms (up to the 32 best terms per
  word) and the idf of the closest one.
- **Deviations from the paper**, each deliberate: the bonus of a document is its best field, because a query word pair split across title and
  body is not close, and positions are compared inside a field only. The paper has one text field; `weight` is ours (the paper's weight is 1).
- **The bound.** `acc * (k1 + 1) / (acc + K) < k1 + 1`, so the bonus is below `weight * (k1 + 1) * sum of min(1, idf)` (at most 32 words). A
  scorer reports its inner bound plus that, and the inner scorer is told the threshold minus that, so block-max WAND skips only what cannot
  reach the top k; the returned top k and scores equal exhaustive scoring (a test checks 120 random queries, pruned against exhaustive, bit for bit).
- A document in which fewer than two query words occur gets no bonus and its positions are not read.
- At most 32 words are compared (the rarest), and a repeated word counts once.
- `weight` is `TextSettings.ProximityWeight` / `[Proximity(w)]`, default 0.1, 0 to 4; 0 removes the bonus. In query objects it is
  `ProximityQuery`; query JSON `{"type":"proximity",...}` (a user-text query compiles to it, so it appears in written query JSON).
- Not applied: with ranking rules (they have a `proximity` rule), to one-word queries, to phrases, to typed query objects unless wrapped.

The default weight was chosen on the four BEIR gate sets; see below.

Measured effect of the weight (BEIR gate sets):

| Proximity weight | SciFact | ArguAna | SCIDOCS | NQ |
|---|---|---|---|---|
| 0 (no bonus) | 0.6912 \| 0.6998 | 0.4333 \| 0.5072 | 0.1531 \| 0.1601 | 0.3420 \| 0.3377 |
| **0.1 (default)** | 0.6918 \| 0.7033 | 0.4347 \| 0.5067 | 0.1544 \| 0.1615 | 0.3452 \| 0.3412 |
| 0.25 | 0.6920 \| 0.7032 | 0.4347 \| 0.5045 | 0.1555 \| 0.1623 | 0.3476 \| 0.3428 |
| 0.5 | 0.6903 \| 0.6960 | 0.4343 \| 0.5007 | 0.1556 \| 0.1608 | 0.3451 \| 0.3384 |
| 1 | 0.6726 \| 0.6831 | 0.4359 \| 0.4940 | 0.1548 \| 0.1593 | not run |
| 2 | 0.6594 \| 0.6540 | 0.4327 \| 0.4810 | 0.1493 \| 0.1501 | not run |
| Lucene.NET, summed fields (flat for SCIDOCS 0.1509 at 0.9/0.4) | 0.6647 \| 0.6628 | 0.4240 \| 0.4880 | 0.1587 \| 0.1619 | 0.3252 \| 0.3051 |

Each cell is nDCG@10 under the BEIR convention at k1/b = 0.9/0.4 \| 1.2/0.75, BM25F with title weight 3 over the BEIR queries as OR-ed words, the bonus
wrapped around them. Weight 0 is the earlier no-bonus score and reproduces the numbers above. Runs of the sweep on the other CPU architecture
(x64 and arm64) gave identical values.

- **Default 0.1.** It is at or above the no-bonus score on all four sets at both settings except ArguAna at 1.2/0.75 (0.5067 against 0.5072, within
  noise). Gains at the default setting: SciFact +0.06 points, ArguAna +0.14, SCIDOCS +0.13, NQ +0.32. A weight of 0.25 gains more on NQ (+0.56) and
  SCIDOCS (+0.24) but gives up 0.27 points on ArguAna at 1.2/0.75; from 0.5 up the bonus costs SciFact and ArguAna, and at 1 and 2 it costs clearly.
  The sets with long queries (ArguAna) and many near-duplicate documents lose first because an additive bonus reorders near ties.
- The first formulation of the bonus (unbounded idf-weighted, no length normalisation, weight 0.5) lost 0.6 points on SciFact and 2.0 on ArguAna at
  1.2/0.75; the paper's form (word weight capped at 1, BM25 length normalisation) with a small weight does not.
- **Hybrid.** With the bonus in the text arm at the default weight, hybrid (RRF, k 10, ratio 0.75) gives SciFact 0.7258, ArguAna 0.6105,
  SCIDOCS 0.2061, mean 0.5141 (without it 0.7257, 0.6100, 0.2064, mean 0.5140): still above both arms on every set.

## Relevance results

See the results table at the end of [relevance.md](relevance.md) for the measured nDCG@10 per set, the baselines and Lucene.NET.
