# Learned sparse vectors

A sparse vector field holds a weight per token (a SPLADE-style expansion, Formal et al., SIGIR 2021 and arXiv:2109.10086, or the output of an
inference-free encoder). Documents are indexed on the existing postings; a query scores by dot product.

## Field and value

- `FieldType.Sparse` (schema type byte 11), `FieldValueKind.Sparse` (10), `SchemaBuilder.Sparse(name)`, `Document.Add(name, SparseVector)`.
- `SparseVector`: unique tokens in ordinal order, finite non-negative weights, zero weights dropped, at most 65,536 entries.
  A repeated token keeps its larger weight.
- A sparse field is single-valued, stored (the vector is returned with the document and survives merges and schema rebuilds from the
  stored source), and indexed unless `Indexed = false`.

## Postings layout (codec `postings` version 1, unchanged)

Each token is a term of the field; a document's posting carries the **quantized weight as its frequency**:
`q = round(weight * 128)` (`SparseVector.Scale`), tokens with `q = 0` are not indexed. No positions, no offsets. The field length
(unused by scoring) is the number of indexed tokens. Because nothing changes in the files, merges, deletes, checksums, the fault
injector's guarantees and the term-dictionary and postings readers apply as they are, and block impacts (largest frequency in a run of
documents) are largest weights, which is what block-max WAND needs (Ding and Suel, SIGIR 2011).

Error bound: a stored weight differs from the original by at most `0.5 / 128`, so a document's score differs from the exact dot product
by at most `0.5 / 128 * sum(query weights)`. Top-k is exhaustive-equal over the quantized weights (test below).

## Query

`SparseQuery(field, vector)` (boost multiplies): for each query token present in a segment, a `PostingsScorer` with score
`frequency * queryWeight * boost / 128` (non-decreasing in the frequency, as the bound requires), combined by `BlockMaxWandScorer`.
A document matches when it shares a token with the query; its score is the sum over shared tokens. `Explain` lists the shared tokens.
Query JSON: `{"type":"sparse","field":"Expansion","weights":{"token":1.5}}`, additive to version 1.

## Encoders

`ISparseEncoder` (core, `OneIndex.Vectors`): `ModelId`, `Encode`/`EncodeAsync(texts, role, destination)`; same rules as `IEmbedder`.

`NeuralSparseEncoder` (`.Models`) is the inference-free encoder for models of the OpenSearch neural sparse "doc" family; files are
supplied by the caller (`OpenDirectory`, `Open(paths)`, `Open(streams)`): a OneModel/ONNX graph with inputs `input_ids`,
`attention_mask` and output `sparse` (float, batch x vocabulary), `vocab.txt`, and the query weight table `idf.json`.
- Document side: WordPiece with `[CLS]`/`[SEP]`, the masked language model's logits, `log(1 + relu(x))` max-pooled over the tokens
  (padding masked) - built into the exported graph by `tools/models/prepare_neural_sparse.py`.
- Query side: no model run. Tokenize without special tokens; each distinct token takes its weight from the table; tokens with weight 0 are
  dropped. A special token written in the text keeps its table weight (observed in the reference).
- **Observed deviation resolved by experiment:** the reference sentence-transformers pipeline (5.x, run only) gives document weights equal
  to `log(1 + w)` of the single-activation pooled weights (checked on 200 texts: with one extra `log1p`, fp32 cosine 1.000000, max abs
  difference 0.0000 at identical support on 199 of 200 texts). Without it SciFact is expected to fall outside the card's number; see the
  results. `OpenSearchSparseEncoder` sets `ExtraLog1p = 1`.

`OpenSearchSparseEncoder` packs opensearch-project/opensearch-neural-sparse-encoding-doc-v3-distill (Apache-2.0; provenance in
THIRD-PARTY-NOTICES.md) and opens it with `Load()`, or from a directory with `Open(directory, variant)`.

## Happy path

```csharp
public class Note {
    [SearchId] public string Id { get; set; }
    [FullText] public string Body { get; set; }
    [Sparse(From = nameof(Body))] public SparseVector? Expansion { get; set; }   // encoded when an ISparseEncoder is configured
}
OpenSearchSparseEncoder.UseAsDefault();                  // or SearchIndex.Open<Note>(path, embedder, encoder)
var hits = index.Search("red apple").Sparse().Take(10);  // text + sparse, fused
```

- `[Sparse]` goes on an `OneIndex.Index.SparseVector` property. With `From`, the index encodes the named text member (document role) before the
  document reaches the writer. The cache key `<member>#sparse` = SHA-256(model id, 0, text) is stored in the stored source beside the vector,
  so reupserting unchanged text, reopening, committing, merging and rebuilding for schema evolution never call the encoder; a new model id is
  stale, and `ReembedStale()` encodes (and embeds) exactly the stale documents. A vector the application sets is kept as it is. The encoder is
  supplied like the embedder: `SearchSparseEncoders.Default`, or the three-argument `Open<T>`/`InMemory<T>`.
- `Search(text).Sparse(weight?)` adds the sparse list (the text encoded with the query role). Combinations: text + sparse, and text + vector +
  sparse when the type also has an automatically embedded vector (the default hybrid applies; `Hybrid(0)` drops the vector list).
  Sparse search is opt-in: plain text search does not use it, because the default hybrid was tuned without it.
- `HybridQuery` gains `Sparse` (a `SparseQuery` or a boolean around it that filters it) and `SparseWeight`; `Vector` may be null (text + sparse).
  Fusion: each list contributes `weight / (RankConstant + rank)` (rank fusion) or `weight * normalized score` (linear); the text and vector
  weights are as before (`2(1-r)`, `2r` for rank fusion, `1-r`, `r` for linear) and 1 for the text list when there is no vector list; the sparse
  weight defaults to 1 (rank fusion) or 0.5 (linear). Explain names the rank in each list.
- Query JSON: a `hybrid` node takes `sparse` (a node) and `sparseWeight`, and `vector` becomes optional (one of the two is required); a request
  `{"q":..., "hybrid":{"sparse":true,"sparseWeight":..}}` adds the sparse list. `SearchTool` describes a `sparse` node per sparse field and the
  `sparse`/`sparseWeight` request properties.

## Results (information)

int8 weights, `ExtraLog1p = 1`, our engine's `SparseQuery` over a force-merged index, nDCG@10 under the BEIR convention (a hit with the query's own id is dropped):

| Set | Docs | Ours | Model card | Entries per doc | Sparse-only index | Sparse query (on disk, warm) |
|---|---|---|---|---|---|---|
| SciFact | 5,183 | 0.7056 | 0.708 | - | 13.1 MB | 1.4 ms |
| ArguAna | 8,674 | 0.5194 | 0.520 | 248.2 | 23.7 MB | 67 ms (queries are whole passages) |
| SCIDOCS | 25,657 | 0.1628 | 0.163 | 224.5 | 70.2 MB | 3.4-4.0 ms |

All three are within 1 point of the card. SciFact is one of the model's training sets (its card lists scifact), so it is not an out-of-domain figure. One thread, one machine, indicative only.

Hybrid, same sets, on one on-disk index holding BM25 (title and text), the bge-small dense vectors and the sparse vectors (default rank constant 10, semantic ratio 0.75, window 100; sparse weight 1 under rank fusion):

| Configuration | SciFact | ArguAna | SCIDOCS | Mean |
|---|---|---|---|---|
| BM25 | 0.6912 | 0.4333 | 0.1531 | 0.4259 |
| dense | 0.7126 | 0.6048 | 0.2041 | 0.5072 |
| sparse | 0.7056 | 0.5194 | 0.1628 | 0.4626 |
| text + dense (the default hybrid) | 0.7257 | 0.6100 | 0.2064 | 0.5140 |
| text + sparse | 0.7209 | 0.5020 | 0.1664 | 0.4631 |
| text + dense + sparse, rank fusion | 0.7374 | 0.6049 | 0.2063 | 0.5162 |
| text + dense + sparse, linear fusion | 0.7439 | 0.6149 | 0.2066 | 0.5218 |

The sparse arm is opt-in (`Sparse()`); it helps SciFact and is neutral on the other two sets with rank fusion, and it is not part of the default hybrid.

- Reference parity (200 texts, fp32): identical to the reference runtime. int8 weights: document cosine median 0.9991, min 0.9818.
