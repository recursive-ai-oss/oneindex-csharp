# Hybrid search: words and meaning in one ranking

This note is normative for `HybridQuery`, `SearchQuery<T>.Hybrid(...)` and the `hybrid` forms of query JSON. Automatic embedding is in `auto-embedding.md`; the vector search itself in `vectors.md`.

## What a hybrid query does

A hybrid query has two arms and a fusion:

* the **text arm** is any query (for plain text, the compiled user text) scored by BM25F as usual;
* the **vector arm** is a `KnnQuery` over a vector field: the documents nearest in meaning to the search text, which the index's embedder turns into a vector with the *query* role;
* each arm contributes its best `Window` documents (default 100): the text arm by relevance score, the vector arm by similarity (exact below the field's flat threshold, otherwise HNSW as `vectors.md`);
* a document in either list matches; its score is the fusion of its position or score in the two lists; hits are ordered by that score, ties towards the earlier segment and smaller document number.

**Filters apply to both arms.** `Where(...)`, filter strings and the `filter` of query JSON are applied inside the text query and as the vector arm's filter, so the vector arm picks its neighbours among the documents that pass (a full window whenever enough pass) instead of being filtered afterwards. `ScoreBy` (function scores) wraps the fused score. Sort, facets, aggregations, distinct and paging see the fused matches: `OrderBy` orders them by the column, facets count them.

**`TotalHits`** is the number of documents in either window (at most twice `Window`) and is exact for that set. A page deeper than that set is empty; raise `Window` for deeper paging. `Skip`/`Take` page through the fused ranking, which does not depend on them.

## Fusion

Notation: for a list `L` (best first) a document at position `i` (1-based) has `rank_L = i`; `r` is `SemanticRatio`.

**Reciprocal rank fusion** (default; Cormack, Clarke and Büttcher, "Reciprocal rank fusion outperforms Condorcet and individual rank learning methods", SIGIR 2009):

```
score(d) = 2(1 - r) / (k + rank_text(d)) + 2r / (k + rank_vector(d))      (a missing list contributes 0)
```

`k` is `RankConstant`. The factor 2 makes `r = 0.5` the plain sum of the two reciprocal ranks of the paper. It uses ranks only, so no calibration between BM25 and similarity scores is needed. Without weights (`r = 0.5`), a document in only one list loses to one in both; the weights let one arm lead.

**Weighted linear fusion** (opt-in, `FusionMethod.Linear`): each list is min-max normalised over its window, `n(d) = (s(d) - min) / (max - min)` (1 when all scores in the list are equal), and

```
score(d) = (1 - r) * n_text(d) + r * n_vector(d)        (a missing list contributes 0)
```

`r = 0` runs the text arm alone and `r = 1` the vector arm alone; the other arm is not even run (no embedding, no search). The query JSON, LINQ and low-level forms validate `r` in `[0, 1]`, `k` in `[1, 1000]` and `Window` in `[1, 10000]`; a violation is a `QueryException` (low level), `ArgumentOutOfRangeException` (LINQ) or a positioned parse error (JSON).

### Defaults and why

The Meilisearch `semanticRatio` is the knob (documented range 0 to 1). The defaults here are `SemanticRatio = 0.75`, `RankConstant = 10`, `Window = 100`, RRF (settled by the experiment below). The paper's constant (60) with equal weights does **not** meet the hybrid gate on the mean: the default embedder is much stronger than BM25 on two of the three gate sets, and an unweighted fusion of a strong and a weak list lands between them. A smaller constant weights the top ranks more, and a ratio above one half lets the stronger arm lead. The settings were chosen from a plateau (below), not a single point, on the same sets the gate reports, so they are in-sample; a different embedder or corpus may prefer 0.5 (set it on the query).

## When `Search(text)` is hybrid

A search is hybrid automatically when all of these hold: it is built by `Search(text)` with non-blank text (not `SearchBy`, `SearchVector`, `SearchStrict`), the index has an embedder (`SearchIndex.Open<T>(path, embedder)` or `SearchEmbedders.Default`), and `T` has exactly one `[Vector(From = ...)]` member. `Hybrid(0)` turns it off for a query. Explicit `Hybrid(...)` calls always apply (with several vector members, name one: `Hybrid(x => x.Embedding)`); `Hybrid(member, vector)` supplies the query vector itself and needs no embedder. A search with no text (`Search()`) matches everything and is never hybrid.

## Query JSON

Additive to version 1 (no new version):

* a request-level `"hybrid": {"semanticRatio": 0.75, "fusion": "rrf" | "linear", "rankConstant": 10, "window": 100, "field": "Embedding"}` (every key optional; `field` defaults to the only automatically embedded member) applies to the request's `"q"`, which must be present and is then held in the request's `Hybrid.Text` rather than compiled into `Query`. `SearchIndex<T>.Parse` embeds it; `QueryRequest.ToQuery()` leaves it out because turning text into a vector is the index's job. The `filter` applies to both arms. An LLM tool schema offers this form (`SearchTool.For<T>()` adds `hybrid` when the schema has an indexed vector field);
* a node `{"type": "hybrid", "text": <node>, "vector": <knn node>, "fusion": ..., "rankConstant": ..., "semanticRatio": ..., "window": ..., "boost": ...}` for callers that bring their own vector. It is not in the tool schema (vectors are not something a model should write).

Defaults are left out when written, so the canonical form round-trips (`QueryJson.Write(QueryJson.Read(x))` is a fixed point). The reserved word `hybrid` no longer produces the "reserved" message.

## Explain

`Explain()` on a hybrid hit names the fusion, the ratio and window, and the hit's rank in each list ("text rank 3, vector rank none").

## Method and results of the gate

The gate (`tools/OneIndex.Models.Tool`, command `hybrid`) builds one index per BEIR set with `title` (BM25F weight 3), `text` and the bge-small-en-v1.5 vector of `title + " " + text` computed by the shipped int8 weights (the same vectors as `docs/models.md`), then runs three kinds of query through the engine's own query objects:

* **BM25**: the OneIndex default for BEIR (a BM25F multi-field term per analysed query token, OR-ed, English analysis, k1 0.9, b 0.4);
* **dense**: `KnnQuery` with `Exact = true`, `K = 100` over the whole corpus (exact, not HNSW, so recall is 1 by construction); the query carries the model's query instruction;
* **hybrid**: `HybridQuery(bm25, dense)` with the defaults above.

nDCG@10 under the BEIR convention (a document with the query's id is dropped), cross-checked with `pytrec_eval` on the run files (`tools/models/crosscheck_run.py`). The gate: hybrid is at least the better single arm *on the mean over the sets*, and on every set at least the weaker arm.

| Set (queries) | BM25 | Dense (exact) | Hybrid, defaults (RRF, k 10, ratio 0.75) | Hybrid, paper setting (RRF, k 60, ratio 0.5) |
|---|---|---|---|---|
| SciFact (300) | 0.6912 | 0.7126 | **0.7257** | 0.7282 |
| ArguAna (1,406) | 0.4333 | 0.6048 | **0.6100** | 0.5616 |
| SCIDOCS (1,000) | 0.1531 | 0.2041 | **0.2064** | 0.1919 |
| Mean | 0.4259 | 0.5072 | **0.5140** | 0.4939 |

* The defaults pass the gate: the mean 0.5140 is above the better arm's mean (dense, 0.5072), and on every set hybrid is above both arms (the weaker arm everywhere is BM25).
* The paper setting passes the weaker-arm test everywhere but misses the mean test by 1.3 points, and is below the dense arm on ArguAna and SCIDOCS.
* `pytrec_eval` on the run files gives the same four-decimal values for BM25, dense and the default hybrid on all three sets.
* NQ was held out of the tuning and is confirmed below.
* Other settings on the same index, for information (nDCG@10 SciFact / ArguAna / SCIDOCS): RRF k 10 ratio 0.5 0.7349 / 0.5677 / 0.1970; RRF k 10 ratio 0.3 0.7175 / 0.5140 / 0.1766; linear ratio 0.3 0.7254 / 0.5649 / 0.1805; linear 0.5 0.7377 / 0.6008 / 0.1994 (mean 0.5126); linear 0.7 0.7291 / 0.6055 / 0.2069 (mean 0.5138).
* The plateau behind the defaults (mean over the three sets, RRF, window 100): ratio 0.75 with k 3, 5, 10, 20 gives 0.5150, 0.5151, 0.5140, 0.5117; ratio 0.7 gives 0.5141, 0.5149, 0.5115, 0.5093; ratio 0.5 gives 0.5013, 0.5005, 0.5000, 0.4965 for all k. Window 100, 300 and 1,000 differ by under 0.0005.

### NQ: the held-out confirmation

The defaults were tuned on the three sets above; NQ (2.68 million passages, 3,452 queries, dense vectors from the reference fp32 model on a GPU, docs/models.md) was run once with them and **not retuned**. Same engine arms, exact kNN:

| Set | BM25 | Dense | Hybrid, defaults | Hybrid, paper setting (k 60, ratio 0.5) |
|---|---|---|---|---|
| SciFact | 0.6912 | 0.7126 | **0.7257** | 0.7282 |
| ArguAna | 0.4333 | 0.6048 | **0.6100** | 0.5616 |
| SCIDOCS | 0.1531 | 0.2041 | **0.2064** | 0.1919 |
| NQ | 0.3420 | 0.5018 | **0.5249** | 0.4862 |
| Mean of four | 0.4049 | 0.5058 | **0.5168** | 0.4920 |

**Gate verdict, as-is: pass.** The four-set mean 0.5168 is above the better arm's mean (dense, 0.5058), and the hybrid is above both arms on every set. Other NQ settings, for information: RRF k 10 ratio 0.5 0.5008; linear fusion ratio 0.3 / 0.5 / 0.7: 0.4461 / 0.5133 / 0.5292 (min-max per arm). The engine's default hybrid and an offline computation of the same formula from the cached windows agree on the top-10 set for 3,448 of 3,452 queries and on its order for 3,395 (ties and window edges). With the sparse arm (opt-in; docs/sparse.md) NQ text + sparse is 0.4911 and text + dense + sparse 0.5671. The stand-in proof for the vectors is in docs/models.md.

## Related

`auto-embedding.md` (how the vectors get there), `vectors.md`, `query-json.md`, `search-tool.md`.
