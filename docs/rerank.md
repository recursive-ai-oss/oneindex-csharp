# Reranking: `IReranker`, the rerank stage and the default cross-encoder

A cross-encoder reads the query and a passage together, which ranks better than comparing a stored vector or counting words, and costs
one model pass per pair. So it runs as a **stage** over the best hits of a first-stage search, never over the whole corpus.

## `IReranker` (core, namespace `OneIndex.Vectors`)

A sibling of `IEmbedder`: same shape, same rules.

- `ModelId`: a stable identity of the model and every setting that changes its scores (weights, quantization, truncation).
- `Score(query, passages, scores, ct)` and `ScoreAsync(...)`: one score per passage, written to a caller buffer of exactly
  `passages.Count` floats. Higher is more relevant. Scores have no fixed range and are comparable only within one query. Pairs longer
  than the model's limit are cut, never rejected; the empty string is valid; `null` throws; an empty list does nothing.
- Thread-safe. The async twin runs on a pooled thread where threads exist and on the caller's turn in the browser; results are identical.
- `SearchRerankers.Default` (set once at startup, or by `MiniLmReranker.UseAsDefault()`) is the reranker new indexes open with;
  `SearchIndex.Open<T>(path, embedder, reranker)` and `InMemory<T>(embedder, reranker)` pass one explicitly. There is no discovery.
- A reranker that throws, or returns a NaN or infinity, makes the search fail with `RerankException` naming the model.

## The stage

```csharp
var hits = index.Search("overdue acme")
                .Where(x => x.Tags.Contains("invoice"))
                .Rerank(window: 100)          // the best 100 after filters, rescored, best first
                .Take(10);
```

1. The first stage runs as the same search with the window as its page: `max(window, Skip + Take)` hits, best first, by whatever
   the search does (text, vector, hybrid, filters, function scores).
2. The **passage** of each hit is read from its stored source: the `[FullText(Rerank = true)]` fields, in declaration order, or every full-text
   field when none is marked, joined by one space (for BEIR documents: title, a space, text). `Rerank(x => x.Body)` names one property.
3. The **query** is the search text as typed (`Rerank(query: "...")` overrides it, and is required when the search was made from query objects,
   which have no text).
4. One `Score(query, passages)` call rescores the window. Hits are ordered by the score, best first; equal scores keep their first-stage order,
   so the result is deterministic.
5. `Skip` and `Take` page the reranked list. A page deeper than the window reranks as many hits as the page needs (`Skip 45 + Take 10` with a window of 50 reranks 55).

What the hit carries: `Score` is the reranker's score and `FirstStageScore` the score before the stage (`null` when the search was not
reranked). `Explain()` wraps the first-stage breakdown in a root node `rerank by <model id>` whose value is the reranker's score (so the root
still equals `Score`) and whose single child is the first stage's explanation.

What it does not change: `TotalHits` is the first stage's. **Facets and aggregations count all matches, not the window**, because the first stage runs unchanged.
`Distinct` ranks its groups by the first stage and the window then holds groups. Highlights are computed from the hit as usual.

What it does not combine with: `OrderBy`, `OrderByDistance` and `SearchAfter` (the reranker decides the order): `NotSupportedException`
at execution. `Take(0)` is a count and calls no model. A search with no first-stage hits calls no model.

## When rerank turns on by itself

Only when all of these hold: the type has a `[FullText(Rerank = true)]` member (the type asks for it), the index has a reranker, the search is
plain text (`Search("...")`, not query objects), non-blank, and not ordered. Then `Search(text)` is `Search(text).Rerank()` with the default window
of 100. `WithoutRerank()` turns it off for one search. A type without a marked member is never reranked unless the search says `Rerank()`. With hybrid
(automatic when the type has a `[Vector(From = ...)]` member and an embedder) the first stage is the fused list, so a type with both is
"hybrid, then rerank" with no further code.

## Query JSON

`"rerank"` is an additive property of version 1: `{"v":1,"q":"overdue acme","rerank":{"window":50}}`. It reads the text of `q` as the query, so `q` is
required (a path-named error otherwise), as with `hybrid`; like hybrid, `QueryRequest.Rerank` holds the text and `ToQuery()` leaves it out while
`SearchIndex.Parse` applies it. `window` is 1 to 10,000, default 100 (not written when default); `{}` is valid; unknown properties are errors that
name the nearest known one. `SearchTool.For<T>()` lists it. It needs an index with a reranker; without one executing the search throws
`InvalidOperationException`.

## Default cross-encoder: `MiniLmReranker` in `RecursiveAI.OneIndex.Models`

cross-encoder/ms-marco-MiniLM-L6-v2 (Apache-2.0), revision `233902d25c440f23af6f7d6e94d2946bac0bee0a`: 6 layers, hidden size 384, one output logit.
Trained on the MS MARCO passage ranking set (provenance only; the weights are the published ones).

- **Pair encoding** (`WordPieceTokenizer.EncodePair`): `[CLS] query [SEP] passage [SEP]`, segment ids 0 for the class token, the query and the
  first separator, 1 for the rest; at most 512 ids. When the pair is too long, tokens are cut from the end of the longer text, never below half of the
  room: with `room = 512 - 3`, the shorter text keeps `min(its length, room / 2)` and the longer one the rest; when the two are equally long the second counts
  as the longer (settled by comparing with the Hugging Face `tokenizers` package as a black box; "longest first" truncation with `max_length = 512`, which
  is what the sentence-transformers cross-encoder asks for). Texts of more than 8,192 tokens are measured only that far to decide which is longer.
- **Score**: the raw logit (the model's own default activation is the identity, `sbert_ce_default_activation_function` in its config).
- **Batching**: pairs are sorted by length, padded to a multiple of 32 and run under a token budget (4,096 padded tokens); a pair's score does not depend on its
  batch (checked: batched against one-at-a-time scores differ by 0.00000).
- **Weights**: `tools/models/prepare_cross_encoder.py` downloads the published ONNX at the pinned revision (SHA-256 checked) and writes the weight-only int8
  variant (the same scheme as the embedder: 37 matrix-multiply weights and the embedding table as int8 with a scale per channel, activations float32).
  `gate.sh setup-reranker` converts both to OneModel files and `pack-weights.sh reranker` places the int8 one where `OneIndex.Models.csproj` embeds it with the
  Apache-2.0 notice. Never committed.

### Conformance and quality

Pair token ids (4,528 of 4,528), scores against ONNX Runtime and the BEIR reproduction (all three sets within 1 point of the published BM25+CE figures) are in `models.md` ("Cross-encoder"). NQ: BM25F top-100 reranked 0.5404 against the BEIR paper's BM25+CE 0.533 (+0.7 point), with the GPU-reference scores and the int8 stand-in proof, also in `models.md`.
