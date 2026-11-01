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

    acc_t   = sum over adjacent pairs (t, u) at distance d of  idf(u) / d²        (d >= 1; two words at one position count as 1)
    bonus_f = sum over query words t of  idf(t) * acc_t / (k1 + acc_t)            for field f
    score   = bm25f(d) + weight * max over fields f of bonus_f

- `idf(t)` is the word's own idf, as in the BM25F term; `k1` is the BM25F `k1`. A word with typo or prefix expansions uses the positions of
  all its matched terms (up to the 32 best terms per word) and the idf of the closest one.
- **Deviations from the paper**, each deliberate: the paper adds the bonus per word with `min(1, idf)` and BM25's `(k1 + 1)` numerator; here the
  bonus has the shape of this engine's term score (`idf * x / (k1 + x)`, so it is below `idf`), which makes its bound simple. The bonus of a
  document is its best field, because a query word pair split across title and body is not close. Positions are compared inside a field only.
- **The bound.** `acc / (k1 + acc) < 1`, so the bonus is below `weight * sum of the words' idf` (at most 32 words). A scorer reports its
  inner bound plus that, and the inner scorer is told the threshold minus that, so block-max WAND skips only what cannot reach the top k; the
  returned top k and scores equal exhaustive scoring (a test checks 120 random queries, pruned against exhaustive, bit for bit).
- A document in which fewer than two query words occur gets no bonus and its positions are not read.
- At most 32 words are compared (the rarest), and a repeated word counts once.
- `weight` is `TextSettings.ProximityWeight` / `[Proximity(w)]`, default {WEIGHT}, 0 to 4; 0 removes the bonus. In query objects it is
  `ProximityQuery`; query JSON `{"type":"proximity",...}` (a user-text query compiles to it, so it appears in written query JSON).
- Not applied: with ranking rules (they have a `proximity` rule), to one-word queries, to phrases, to typed query objects unless wrapped.

The default weight was chosen on the four BEIR gate sets; see below.

{PROXRESULTS}

## Relevance results

See the results table at the end of [relevance.md](relevance.md) for the measured nDCG@10 per set, the baselines and Lucene.NET.
