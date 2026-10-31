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

`null` means the document does not match.

## Relevance results

See the results table at the end of [relevance.md](relevance.md) for the measured nDCG@10 per set, the baselines and Lucene.NET.
