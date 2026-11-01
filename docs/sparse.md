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

## Results (information; see the report of the unit for machines and job ids)

- Reference parity (200 texts, fp32): identical to the reference runtime. int8 weights: document cosine median 0.9991, min 0.9818.
- SciFact, int8 weights, our engine's `SparseQuery`: nDCG@10 0.7056 (model card 0.708; the card's training list includes SciFact).

## Not yet in this layer

Attribute `[Sparse(From = ...)]` with source generation, auto-encoding with the cache in the stored source, a sparse arm in `.Hybrid()`,
and the `SearchTool` schema entry are not built yet.
