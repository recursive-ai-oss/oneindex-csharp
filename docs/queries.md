# Queries: semantics and limits

How the query engine (`QueryEngine`, an `IWeightFactory`) runs each query record. Scoring is in [scoring.md](scoring.md). A query is data; `QueryEngine.CreateWeight` checks it against the schema, fixes the statistics of the snapshot and returns a `Weight`; a weight makes one `Scorer` per segment. Everything here is deterministic for a given snapshot.

## Fields and typed errors

Every query names fields. A field must exist in the schema and be searchable for the query kind, otherwise `QueryException` (an `InvalidOperationException`) says which field and why:

- Term-like queries (`TermQuery`, `MultiFieldTermQuery`, `PhraseQuery`, `PrefixQuery`, `FuzzyQuery`, `RegexQuery`): a text field with index options other than `None`, or a keyword field that is indexed.
- `PhraseQuery` additionally needs a text field indexed with positions (`Positions` or `Offsets`). A field indexed without positions is an error, not an empty result.
- `RangeQuery`: an indexed integer, floating-point, date, boolean or keyword field.

A field the schema declares but a segment holds no terms for simply matches nothing in that segment. Other typed errors: a negative or non-finite boost, a negative slop, a fuzzy distance outside 0 to 2, an invalid regular expression (with its position), a bound of the wrong type, a boolean query with more than `MaxClauses` clauses (default 4,096) or nested deeper than `MaxDepth` (default 64), and `TooManyTermsException` (below). Nothing else is thrown for a well-formed query.

`Boost` multiplies the score contribution of a query node; 0 is allowed and gives score 0 (the documents still match).

## Term and multi-field term

`TermQuery(field, term)` matches documents whose field contains the term exactly as given: the term is already analyzed (a query string is analyzed by the caller with the field's analyzer). `MultiFieldTermQuery(fields, term)` matches documents containing the term in any listed field and scores it as one BM25F term over the fields. A `TermQuery` is the one-field case; both use the schema boost of each field as its weight.

## Boolean

Clauses are `Must`, `Should`, `MustNot` and `Filter`.

- A document matches when it matches every `Must` and `Filter` clause, no `MustNot` clause, and at least *needed* `Should` clauses, where *needed* is `MinimumShouldMatch` if it is above 0, otherwise 1 when the query has no `Must` or `Filter` clause and 0 when it has one.
- `Should` clauses that are not needed still add to the score of documents that match them.
- `Filter` matches like `Must` but contributes 0 to the score.
- A query with no clauses matches nothing. A query with only `MustNot` clauses matches nothing (a negation alone selects no documents: add a `MatchAllQuery` clause to select "everything but"). A `MinimumShouldMatch` above the number of `Should` clauses matches nothing.
- The score is the sum of the scores of the matching `Must` clauses, plus the sum of the matching `Should` clauses, times the boost, each sum in clause order.

## Phrase

`PhraseQuery(field, terms)` with `Slop`. The terms must occur in the given order at strictly increasing positions. For each position `p` of the first term the engine takes the nearest in-order chain of the other terms (each at the first position after the previous one); the **gap** of the chain is `last position - p - (terms - 1)`, the number of positions the chain is stretched past an exact phrase. The match counts when `gap <= Slop`, with weight `1 / (1 + gap)`. The phrase frequency of a document is the sum of the weights, and it is scored as the frequency of one pseudo-term (scoring.md). Slop 0 is the exact phrase. Terms of a phrase may repeat. One term is a term query; no terms matches nothing. Terms stacked at one position (stemmed twins) are different positions only if the analyzer gave them different positions: a phrase never matches two terms at the same position. Order is required: `"brown quick"` does not match `quick brown` at any slop (there is no transposition match).

## Prefix, fuzzy and regular expression

These **expand** to the terms of the field that match, then score as a disjunction of the terms' BM25F scores (a boolean of `Should` clauses over the expanded terms, in term byte order). Statistics of each expanded term come from the snapshot, like any term query.

- `PrefixQuery`: terms that start with the prefix (compared as UTF-8 bytes).
- `FuzzyQuery`: terms within `MaxEdits` (0 to 2) edits of the term, counting insertion, deletion and substitution of a code point, and with `Transpositions` (default true) the swap of two adjacent code points as one edit (the optimal-string-alignment distance; a character cannot be edited again after being swapped). `PrefixLength` code points at the start must match exactly. `MaxEdits` 0 is a term query. The term itself matches. Matching is by the Levenshtein automaton of the term dictionary: terms are never compared one by one.
- `RegexQuery`: terms that the whole pattern matches; the syntax is the one of the term dictionary (docs/term-dictionary.md): no anchors, no look-around.

**Expansion limit.** A query may expand to at most `QueryEngineOptions.MaxExpandedTerms` distinct terms (default 1,024), counted across all segments of the snapshot. Past it the behaviour is chosen by `Overflow`:

- `Throw` (default): `TooManyTermsException` naming the field and the limit. Chosen as the default because a result computed from a silently truncated term list is hard to explain.
- `KeepMostFrequent`: the `MaxExpandedTerms` terms contained in the most documents (ties by term bytes) are kept and the rest dropped. Never examines more than 1,048,576 distinct matching terms; past that it throws too.

The count includes terms that occur only in deleted documents until a merge removes them, so whether a borderline query throws can change at a merge.

## Range

`RangeQuery(field, lower, upper)` with `IncludeLower` and `IncludeUpper` (default both true); a `null` bound is open. It matches documents with a value of the field in the range and scores every match as its boost (no length or frequency involved).

- Integer, date (milliseconds since the Unix epoch, UTC) and boolean fields take bounds of the same kind (integer and date bounds are interchangeable). A floating-point field takes integer or floating-point bounds. Floating-point values compare numerically; `-0.0` equals `0.0`; NaN is never in a range and is rejected as a bound; an open upper bound includes `+Infinity`.
- A keyword field takes text bounds and compares the UTF-8 bytes of its values in ordinal order.
- A document with several values matches when any value is in range. A document with no value never matches.

**Execution.** A segment that has a point index for the field (`IPointSource`, implemented by the columns and points code) answers through `IPointIndexReader.GetRange(lower, upper)` with inclusive bounds in the ordered 64-bit form of `NumericTerms.Ordered`. Otherwise the engine uses the numeric terms the text codec writes for every indexed number when `TextSegmentCodec.IndexNumericTerms` is on (it is off in the default codec set, because the point index answers ranges and the terms only cost index size; turn it on for an index written without the point codec): 16 hexadecimal digits of the ordered value with the sign bit flipped, so the terms of a range are a contiguous run of the term dictionary, found from the digits the two bounds share; keyword ranges walk the keyword terms in byte order between the bounds. Both collect the matching documents of a segment into a bit set. The fallback cost is the number of distinct values in the walked run.

## Match all

`MatchAllQuery` matches every live document and scores it as its boost.

## Results and counting

`IndexSearcher.Search(query, count, countExactly)` returns the best `count` documents: higher score first, equal scores by lower global document id (segments in commit order). With `countExactly: false` block-max WAND may skip documents that cannot enter the top `count`; the page is identical to the exhaustive one and `TopDocs.TotalHits` is a lower bound (`TotalHitsExact` is true only when nothing could have been skipped). With `true`, every match is visited and the total is exact. Deleted documents never appear. Only queries that are a disjunction at the top (`Should` clauses, expansions, a term, optionally excluded or boosted) are pruned; conjunctions, phrases and ranges are always evaluated exhaustively.

`IndexSearcher.Explain(query, globalDocId)` returns the score breakdown of a document (scoring.md) or `null` when it does not match.

## Statistics scope

Document counts, document frequencies and average field lengths are summed over all segments of the snapshot when the weight is made, so a document scores the same whichever segment it is in. Deleted documents still count until a merge rewrites their segment. Scores of a snapshot therefore change slightly across merges that drop deletes, and are identical for two snapshots with identical segments and deletes.

## Other ways to write a query

The same query objects are written by four text and data forms: forgiving [user text](user-text.md), the opt-in [strict syntax](query-syntax.md) with positioned errors, [filter strings](filter-strings.md), and versioned [query JSON](query-json.md), whose schema for language models is described in [search-tool.md](search-tool.md).
