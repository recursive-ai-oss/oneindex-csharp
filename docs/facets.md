# Facets, facet value search and distinct

This note is normative for `OneIndex.Aggregations.FacetCollector`, `FacetValueSearch`, `OneIndex.Search.DistinctCollector` and the happy-path calls `Facets` and `Distinct` of `SearchQuery<T>`. Aggregations (buckets and metrics beyond facets) are in [aggregations.md](aggregations.md). The column layouts they read are in [columns.md](columns.md).

The vocabulary follows Meilisearch's documented one: *facet distribution* (counts per value), `maxValuesPerFacet`, `sortFacetValuesBy` (`alpha` or `count`), *facet stats* (minimum and maximum of numeric facets), *facet search* (search within a facet's values) and `distinctAttribute`.

## Facets

A facet is the count of each value of a field over **all documents the query matches**, not over the page. Fields: keyword, bool, integral, fractional and date fields that keep a column (`[Facet]`, `[Filterable]` or `[Sortable]` on the property; the property may be a collection when it is keyword or numeric). Text, geo, vector, bytes and JSON fields cannot be faceted (`ArgumentException` naming the field).

- **Counting.** A document counts once for each distinct value it has. A document listing `["a", "a", "b"]` adds 1 to `a` and 1 to `b`. A document without a value adds nothing. Deleted documents never count. Counts are exact.
- **Values as text.** `FacetValue.Value` is the keyword; `true`/`false`; the invariant round-trip text of a number (`12`, `0.5`; `-0` is `0`); an ISO 8601 UTC time with milliseconds (`2024-02-05T00:00:00.000Z`) for dates. `FacetValue.Key` also carries the number.
- **`MaxValues`** (default 100, at least 1) keeps the first values in the requested order. `DistinctValues` reports how many distinct values existed before the cut.
- **`SortBy`.** `Alpha` (the default, as in Meilisearch): keywords by ordinal comparison of UTF-16 code units, numbers and dates by value, `false` before `true`. `Count`: most documents first, equal counts in `Alpha` order. Both orders are total, so the same snapshot always gives the same list.
- **Stats.** For integral and fractional fields `FacetResult.Stats` holds the minimum and maximum over the matching documents' values (null when no matching document has a value). Dates, bools and keywords have none.

### Cost and exactness

Counting needs every match, so a facet search cannot use block-max WAND skipping. In the happy path, asking for any facet, aggregation or distinct makes the top-hits collector count exactly (`TotalHits` is exact, as if `CountExactly()` had been called) and the engine visits every match once, feeding all collectors in the same pass (`MultiCollector`). Cost is about one column read per matching document per facet, plus the page's scoring; the top-k collector sees no skip hints. A search that only needs the page and a lower bound stays pruned: leave facets off it.

Per segment a keyword facet keeps one `int` counter per dictionary term (and one `int` stamp per term when the column is multi-valued, to count a document once per value). Collecting a document allocates nothing; the keywords are read from the dictionary only once per distinct term per segment at its end. Memory is proportional to the segment's distinct values.

### Low level

```csharp
var facets = new FacetCollector(schema, [new FacetRequest("tags") { SortBy = FacetSort.Count, MaxValues = 20 }, new FacetRequest("price")]);
searcher.Search(query, facets);
IReadOnlyList<FacetResult> r = facets.GetResults();   // request order
```

Happy path: `index.Search("shoes").Facets(x => x.Tags).Facets(x => x.Price, sortBy: FacetSort.Count)`; read `results.Facets["Tags"]` (by property name). The property must keep a column; a property that is already faceted is refused.

## Facet value search

`FacetValueSearch.Search(searcher, query, field, text, limit)` answers "which values of this facet match what the user typed", counted over the documents `query` matches (so the counts reflect the current filters). `Match(values, text, limit)` applies the same rules to counts already in hand. The result is ordered by value (ordinal; the default, as in Meilisearch) or, with `sortBy: FacetSort.Count`, by count (most documents first, ties by value), then cut at `limit` (default 100).

On the happy path the same search is `index.Search(...).Where(...).FacetValues(x => x.Category, "sho")` (and `FacetValuesAsync`): it counts over the documents of that search, ignoring its paging.

Normalization of both the value and the input: NFKC, full case folding, accent removal (the same steps as the standard analyzers; `ß` becomes `ss`, `ﬁ` becomes `fi`, final sigma folds to sigma). Then both are split into words (runs of letters, digits and combining marks).

A value matches when
1. the input is empty, or
2. the normalized value starts with the normalized input (so punctuation inside the input still works), or
3. every input word matches some word of the value, where an input word `q` matches a value word `w` if `w` starts with `q`, or, within a **typo budget**, if `q` is within that many edits of a prefix of `w` whose length differs from `q`'s by at most the budget.

Typo budget by the input word's length in code points, as Meilisearch documents for search: none up to 4, 1 from 5, 2 from 9. An edit is an insertion, deletion, substitution or the swap of two adjacent characters (optimal string alignment, the distance of `Suggest.EditDistance`).

Meilisearch's reference says facet search considers only the first word of the query and does not search numeric values. Documented differences from it: every input word is used and may match any word of the value (so `york` finds `New York`, which Meilisearch would not return), numeric and date values are searched as their text, and matching is on whole values by the rules above (words of the value, not the engine's tokenizer, so no language-specific segmentation: a CJK value is one word, so it matches by prefix); there is no separate setting for typo thresholds.

## Distinct

`DistinctCollector(field, count)` and `SearchQuery<T>.Distinct(x => x.Property)` keep the best document of each value of a column.

- **Groups.** Documents with the same value of the column form a group. A document with several values belongs to the group of its first value (values keep the order the document gave them). A document with no value is a group by itself, as in Meilisearch, where documents without the distinct attribute are not collapsed.
- **Ranking.** A group is ranked by its best document: higher score first; equal scores by lower global document id. The best document of a group is chosen the same way. Both are total orders, so a page is a pure function of the snapshot and the query, and `Skip`/`Take` pages tile without overlap or gap.
- **Totals.** In the happy path `TotalHits` is the exact number of groups. `DistinctDocs` (low level) reports both the groups and the matching documents.
- **Cost.** All matches are visited (a group's best score is known only at the end) and a score is computed for each. Memory is proportional to the number of groups (about 8 bytes plus the dictionary entry for each); it is not bounded by the page. `Skip + Take` stays within the window limit of the happy path (10,000).
- Geo points cannot be collapsed by (`ArgumentException`).
