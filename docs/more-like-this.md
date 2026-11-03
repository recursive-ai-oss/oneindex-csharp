# More like this

Finds documents that resemble a text or an indexed document. `MoreLikeThisQuery` is a query record like the others; the happy path has `index.MoreLikeThis(id, x => x.Body)` and `index.MoreLikeThisText(text)`, query JSON has the `moreLikeThis` node.

## Method

The selection is the relevance-feedback term selection of the IR textbooks (a term is a good probe when it is frequent in the source and rare in the collection):

1. The source is the `Text`, or the stored source of the live document with identifier `DocumentId` (a `QueryException` when there is none).
2. For each full-text field (`Fields`, default all indexed ones) the source text is analyzed with the field's own analyzer and the terms are counted. Stacked forms (stemmed and unstemmed) count as terms like any other.
3. A term is a candidate when its count is at least `MinTermFrequency` (default 1) and it occurs in at least `MinDocumentFrequency` indexed documents (default 1; for a document source the source itself is one of them, so a term only the source holds is dropped: it could not find anything else).
4. Its weight is `count * idf`, with the same idf as scoring (scoring.md), taken from the field's statistics over the snapshot.
5. The `MaxQueryTerms` (default 25) best weights are kept; ties break by field then term in ordinal order, so the choice is deterministic.
6. The query is a boolean of `Should` term clauses, one per kept term. With `BoostTerms` (default true) a clause's boost is its weight divided by the best weight. A document needs `ceil(MinimumShouldMatchFraction * kept)` of them (default 0.3, at least one).
7. A document source is excluded from the results with a `MustNot` identifier clause unless `IncludeSource`.

The terms are chosen when the weight is made, against the snapshot being searched. A query with no candidate term matches nothing. `Highlights` of the hits are empty for this query, since the words are not known until it runs.

Defaults are ours; the parameters follow the names and the idea of the published "more like this" query documentation of Elasticsearch (maximum query terms, minimum term and document frequency, minimum should match) without copying its defaults: this engine's minimum document frequency is 1 because small indexes are common, and a share of terms replaces a percentage string.

## Query JSON

`{"type":"moreLikeThis","id":"c1","fields":["Body"],"maxQueryTerms":25,"minTermFrequency":1,"minDocFrequency":1,"minimumShouldMatch":0.3,"boostTerms":true,"includeSource":false}`. Exactly one of `text` and `id`; defaults are not written back. `fields` must be full-text fields. The strict syntax and user text have no form for it: both are for typed words, and the node composes with them through a `bool` node.
