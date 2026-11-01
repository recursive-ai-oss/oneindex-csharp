# Query JSON

A search as data: a JSON document with `"v": 1`, checked against a schema as it is read. It is meant for programs and language models that build a query without a C# compiler, and it compiles to the same query objects as every other surface. It is not compatible with Elasticsearch's query DSL.

```json
{
  "v": 1,
  "q": "overdue \"acme corp\" -draft",
  "filter": "Tags = invoice AND Modified > 2024-01-01",
  "limit": 10,
  "highlight": true
}
```

```csharp
SearchQuery<Invoice> search = index.Parse(json);            // throws QueryParseException with the JSON path
QueryRequest request = QueryJson.Read(json, index.Schema);   // the same, as data
string canonical = QueryJson.Write(request);
```

## The document

| Property | Meaning |
|---|---|
| `v` | **Required.** The format version, the whole number 1. Any other value is an error. |
| `q` | Free text, as typed by a user ([user-text.md](user-text.md)). Never an error. |
| `query` | A structured query node (below). When both `q` and `query` are given, both must match. |
| `filter` | A [filter string](filter-strings.md), or a query node. Selects without scoring. |
| `limit` | Hits to return, 0 to 10,000. Default 20. |
| `offset` | Hits to skip, 0 to 10,000. Default 0. `limit + offset` is at most 10,000. |
| `countExactly` | `true` for an exact `TotalHits`. Default false (a lower bound). |
| `highlight` | `true` for the default snippets, or `{"fragmentLength": 1..10000, "maxFragments": 1..100}` (defaults 160 and 3). |
| `sort` | 1 to 16 strings that order the hits instead of relevance: `Field:asc`, `Field:desc`, `_score`, `_geoPoint(lat, lng):asc`, each optionally ending in `:first` or `:last` for documents without a value. See [sorting.md](sorting.md). |
| `searchAfter` | A cursor token (`SortCursor.ToToken()`, also `NextCursor` of the previous page) to continue after; needs `sort` made the same way, and excludes `offset`. |

With no `q` and no `query`, every document is a candidate. Unknown properties are errors that name the path and the nearest known name. These names are reserved for later versions and rejected with a message of their own: `facets`, `aggregations`, `distinct`, `vector`. `hybrid` is a property: an object that fuses the text search of `q` with a vector search over its embedding, and a node type `hybrid` joins `knn` for callers that bring a vector (both in `hybrid.md`; additive to version 1, so every document that was accepted reads the same). `rerank` is a property too: an object `{"window":50}` that rescores the best hits of the search with the index's reranker; it reads the text of `q` and is described in `rerank.md` (additive to version 1 as well). (`sort` and `searchAfter` were reserved in the first drafts of version 1 and are now properties: documents that used to be rejected are accepted, and every document that was accepted reads the same.)

Limits: the document is at most 1 MiB, strings 8,192 characters, query nodes nested 32 deep, 2,048 nodes in all. Invalid UTF-8 and lone surrogates are errors.

## Query nodes

A node is an object with a `type`. Every node accepts `"boost"` (a non-negative number; default 1). Fields are checked against the schema: the name must exist and be indexed, and the kind of field decides what the node accepts.

| `type` | Properties | Meaning |
|---|---|---|
| `matchAll` | | every document |
| `text` | `text` | user text across the full-text fields (what `q` is) |
| `strict` | `text` | a [strict query](query-syntax.md); its errors keep their offset, line and column inside the message and `$.….text` as the path |
| `filter` | `filter` | a [filter string](filter-strings.md) used as a query |
| `term` | `field`, `value` | the exact value. A keyword takes a string; a full-text field takes one *analyzed* term (not analyzed again); numbers, dates and booleans take a JSON number, an ISO date string or `true`/`false` |
| `phrase` | `field`, `terms` (1 to 256 strings), `slop` (0 to 1000) | analyzed terms that must occur in order |
| `prefix` | `field`, `prefix` | terms that start with the analyzed prefix |
| `fuzzy` | `field`, `term`, `maxEdits` (0 to 2, default 2), `prefixLength` (default 0), `transpositions` (default true) | terms within the edit distance |
| `regex` | `field`, `pattern` | terms that the expression matches in full (checked when read) |
| `range` | `field`, at most one of `gt`/`gte`, at most one of `lt`/`lte`, at least one bound | numbers, dates (ISO strings) or keywords (strings); not booleans |
| `exists` | `field` | documents that have a value |
| `multiFieldTerm` | `fields`, `term` | one analyzed term in any of the full-text or keyword fields, scored as one BM25F term |
| `geoRadius` | `field` (a geo point field), `lat`, `lng`, `meters` | documents with a point within the distance ([geo.md](geo.md)) |
| `geoBoundingBox` | `field`, `north`, `south`, `west`, `east` | documents with a point in the rectangle; `west` greater than `east` crosses the antimeridian |
| `bool` | `must`, `filter`, `should`, `mustNot` (arrays of nodes), `minimumShouldMatch` | all `must` and `filter` clauses, no `mustNot` clause, and enough `should` clauses; `filter` clauses do not score |
| `functionScore` | `query` (a node; default `matchAll`), `functions` (1 to 32 function objects), `scoreMode` (`multiply`, `sum`, `avg`, `first`, `max`, `min`; default `multiply`), `boostMode` (`multiply`, `replace`, `sum`, `avg`, `max`, `min`; default `multiply`), `maxBoost` (above 0), `minScore` (not negative) | the score of `query` changed by functions of column values; see [function-scores.md](function-scores.md) |

A **function object** has a `type`, an optional `weight` (not negative; default 1) and an optional `filter` (a filter string or a node):

| `type` | Properties |
|---|---|
| `weight` | |
| `fieldValueFactor` | `field` (numeric, date or bool column), `factor` (default 1), `modifier` (`none`, `log`, `log1p`, `log2p`, `ln`, `ln1p`, `ln2p`, `square`, `sqrt`, `reciprocal`), `missing` |
| `gauss`, `exp`, `linear` | `field` (a numeric, date or geo point column), `origin` (a number; for a date field also an ISO date string; for a geo point `{"lat":…,"lng":…}`), `scale` (above 0; milliseconds for dates, metres for geo points), `offset` (default 0), `decay` (above 0 and below 1; default 0.5) |

Function fields need a column, not an index. The custom function of the low-level API has no JSON form. A written `origin` is always a number (a date as milliseconds since 1970).

`phrase`, `prefix`, `fuzzy`, `regex` and `multiFieldTerm` carry **already analyzed** terms, as the query objects do: write them lower case for the default analyzer. To search words the way a user would, use `text` or `strict` (`{"type":"strict","text":"Title:\"acme corp\""}`).

Numbers on integer fields may be fractional in a `range` (rounded inward, as in filter strings). Dates are ISO strings: `2024-01-31`, `2024-01-31T10:00:00Z`, with an optional fraction and offset.

## Paths in dynamic JSON

Wherever a node names a `field`, the name of a `[Dynamic]` property, a dot and a path name a path inside it (`"field":"Meta.customer.age"`). `term`, `range` and `exists` choose the kind of value from the JSON type of the value given (string, number, boolean; a string bound that reads as a date makes a date range); `prefix`, `fuzzy`, `regex` and `phrase` apply to strings. See dynamic.md.

## Errors

A `QueryParseException` has `Path` (`$.query.must[1].field`), `Detail`, and `Suggestion` for a misspelt field, property or type. Text that is not JSON has no path; its `Line` and `Column` locate the syntax error. A `filter` or `strict` string with a mistake reports the position inside that string in the message, and the path of the string.

## Canonical form and round trips

`QueryJson.Write` is canonical: properties in a fixed order (`v`, `query`, `filter`, `limit`, `offset`, `countExactly`, `highlight`, `sort`, `searchAfter`), nodes with `type` first, no white space, defaults left out, boosts as shortest round-trip numbers.

- **Object to JSON to object is identity:** `Read(Write(request))` equals `request` for every request whose boolean clauses are grouped as `must`, `filter`, `should`, `mustNot` (the shapes of all our parsers). Interleaved clauses are written grouped, which keeps the order inside each group; their meaning is the same.
- **JSON to object to JSON is canonical:** any accepted document, whatever its property order or spacing, and with `q`, `text`, `strict` and `filter` nodes already compiled into query nodes, writes as one canonical document. Writing is deterministic.
- `Range(v, v)` with both bounds included and a value that is not text is written as a `term`; `Range` with no bounds as `exists`; a `MatchAll` as `matchAll`.
- `Boost` of 1 is not written. Include flags on an open bound are normalized to true.
