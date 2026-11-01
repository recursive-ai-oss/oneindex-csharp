# Ranking rules

An optional way to rank results: a bucket sort over a list of rules, in the manner of Meilisearch's documented ranking rules. Without
rules a search is ranked by the BM25F score ([scoring.md](scoring.md)), which already includes a term-proximity bonus. Rules are for
applications that want results ordered by explicit, explainable criteria: "documents with more of the query words first, then fewer
typos, then closer words, ...".

## Choosing rules

- A type declares them with `[RankingRules("words", "typo", "proximity", "attribute", "sort", "exactness")]`; they are recorded in the
  index (schema section 5) and apply to the next query. At the low level, `TextSettings.RankingRules`.
- A search replaces them: `Search(text).RankingRules("words", "Price:asc")`, or `RankByRelevance()` to ignore the declared rules.
- Query JSON: `"rankingRules": ["words", "typo"]` (an empty list ranks by relevance). `SearchTool` lists the property.
- Meilisearch's default order is `words, typo, proximity, attribute, sort, exactness`
  (`RankingRule.MeilisearchDefault`). There is no default: with no rules the score ranks.

A rule is one of `words`, `typo`, `proximity`, `attribute`, `sort`, `exactness`, or `field:asc` / `field:desc` for a property with a
column (`[Sortable]`, `[Filterable]` or `[Facet]`). A built-in rule or a field may be listed once; at most 12 rules.

## How the sort runs

The rules, in order, are the keys of one sort ([sorting.md](sorting.md)); the relevance score is the last key and the document's
identifier breaks what remains, so the order is total and the same on every run. Every rule's value is computed per matching document
and a smaller value is better. Because it is a sort over all matches:

- `TotalHits` is exact and the search visits every match.
- Paging works with `Skip`/`Take` and with search-after cursors (`hit.Cursor`, `SearchAfter(...)`, `"searchAfter"` in JSON); a cursor is
  valid for the rules it came from.
- Facets, aggregations, filters, highlighting, `Explain()` and `ScoreBy` (it shapes the relevance score, the last key) work as usual.
  `Distinct` cannot be combined with rules. Hybrid searches ignore rules (they rank by fused position).
- With the `words` rule the search words are OR-ed; without it every word must match, as without rules.
- The rules apply to user text. For a typed query object (`SearchBy`) there are no query words, so `words`, `typo`, `proximity`,
  `attribute` and `exactness` are all 0 and only `sort`, field rules and relevance order the results.

## The rules

"Query words" are the words and quoted phrases of the user text, in text order, after analysis and stop words. A phrase counts as one
query word for `words` and takes no part in the other rules (it matches exactly and in order). Synonym readings count towards the word
they replace for `words`; the other rules look at the word as typed.

| Rule | Value of a document (smaller is better) | Choices where the documentation is silent |
|---|---|---|
| `words` | Number of query words (or phrases) the document does not match. | Meilisearch drops words from the end of the query only when needed to fill the page; here a document that lacks the last words simply sorts after documents that have them, which gives the same order. |
| `typo` | Sum, over matched words, of the typos of the closest matching term (0 for a word found as typed). | Typos are counted as in [typo-tolerance.md](typo-tolerance.md) (a first-letter typo is two). |
| `proximity` | Sum over consecutive matched words (in query order) of the smallest distance between an occurrence of one and an occurrence of the next, in one field. Adjacent in order is 1; the second before the first costs one more than its distance (so query order wins ties); the distance is capped at 8, which is also the cost of two words that never occur in the same field. | The cap of 8 and the order penalty are ours. A document with fewer than two matched words has 0. |
| `attribute` | Per matched word, the rank of the first field that holds it (fields in the type's declaration order, so earlier properties are more important) times 100,000,000 plus the word's first position in that field (capped at 99,999); summed over the words. | The documentation ranks by the order of the searchable attributes and then position; combining both in one number is ours. |
| `sort` | The search's `OrderBy`/`ThenBy` keys, in place. With no ordering it changes nothing. | Meilisearch applies `sort` only when the request has a sort; so does this. Without a `sort` rule an `OrderBy` has no effect. |
| `exactness` | Number of matched words that were found only with typos or as a longer word (a prefix match). | The documentation also prefers documents whose field equals the query; that part is not implemented. |
| `field:asc` / `field:desc` | The column's value; missing values last. | Multi-valued fields sort by their smallest (asc) or largest (desc) value, as in [sorting.md](sorting.md). |

Matched means the document contains the word (as typed, with typos, or as a beginning of a longer word, per the
typo-tolerance settings). When a word has several matching terms the closest one is used.

## Explain

With `Explain()` each hit's explanation has the bucket of every key, in order, as a child, plus the text score's explanation as the last
child. The root value is the text score, as for any hit (a test asserts it equals `SearchHit.Score`).

```
0.5731 = ranking rules in order (smaller buckets first), then the text score; the root value is the text score
  0 = words: 0 of 2 query words missing
  0 = typo: 0 typos in the matched words
  1 = proximity: 1 summed distance between matched words (adjacent = 1, at most 8 each)
  ...
```

## Checks

`tests/OneIndex.Tests/RankingTests.cs` compares the order of generated corpora against a brute-force sort by the rule tuple computed
from the raw tokens (48 cases over four rule lists, 80 to 400 documents, several segments), and the order of equal tuples by score.
Relevance numbers for the Meilisearch default order, as information, are in [relevance.md](relevance.md).
