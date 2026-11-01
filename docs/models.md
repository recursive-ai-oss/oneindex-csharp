# Models: tokenizer, embedder contract and the default embedder

Package `RecursiveAI.OneIndex.Models`. It depends on core and on `RecursiveAI.OneModel` only.

## `IEmbedder` (core, namespace `OneIndex.Vectors`)

- `ModelId`: a stable identity of the model and every setting that changes its output (weights, quantization, prefix, truncation). Two
  embedders with the same id give the same vectors for the same text and role, so the id is safe in an embedding cache key.
- `Dimension`, `IsNormalized` (unit-length vectors: the dot product is cosine similarity).
- `Embed(texts, role, destination, ct)` and `EmbedAsync(...)`: row-major `texts.Count * Dimension` floats written to a caller buffer.
  `EmbeddingRole.Query` or `Document` selects the query instruction some models need. Texts longer than the model's limit are cut,
  never rejected; the empty string is valid; `null` throws; the destination length must match exactly.
- Implementations are thread-safe. The sparse, multi-vector and reranking contracts are siblings of the same shape.

## WordPiece tokenizer (`OneIndex.Models.Tokenization`)

Self-contained: no reference to the engine or to OneModel, only the BCL (and an `ICanonicalDecomposer` for accent stripping, which
`OneIndex.Models.UnicodeDecomposer` implements from OneIndex's own Unicode tables; the runtime's own `Normalize` does nothing for
non-ASCII text when globalization is invariant, so the default fallback is only for hosts that have ICU).

Sources: Devlin et al. 2019 (BERT), Wu et al. 2016 (WordPiece), the BERT README (cleaning, CJK and punctuation rules), the Hugging Face
`tokenizers` documentation. The reference implementation's source was not read; its output was used as a black box to settle open
points (below).

Per stretch of text between special tokens:

1. **Special tokens.** Literal `[PAD]`, `[UNK]`, `[CLS]`, `[SEP]`, `[MASK]` in the input map to their ids before any normalization
   (`SplitOnSpecialTokens`, on by default, as in the reference; turn it off for untrusted text).
2. **Cleaning.** U+0000, U+FFFD and every character of category Cc, Cf, Co or Cs are dropped, except tab, line feed and carriage return.
   (So vertical tab, form feed and U+0085 are dropped, not word breaks. Unassigned characters are kept.) Remaining whitespace
   (`White_Space`) separates words. An unpaired surrogate reads as U+FFFD and is dropped.
3. **CJK.** Each ideograph in U+4E00..9FFF, 3400..4DBF, 20000..2A6DF, 2A700..2B81F, 2B920..2CEAF, F900..FAFF, 2F800..2FA1F is a word of
   its own. The reference leaves U+2B820..2B91F out although the BERT README lists the whole block; that range is reproduced as observed.
4. **Accents and case.** With `StripAccents` (default: follows `LowerCase`), the word is decomposed (NFD) and nonspacing marks (Mn)
   are dropped; then it is lower-cased (simple case mapping, except U+0130, which gives `i` and U+0307).
5. **Punctuation.** Each ASCII non-alphanumeric (33..47, 58..64, 91..96, 123..126) and each character of category P* is a word.
6. **WordPiece.** Greedy longest match from the left; continuation pieces carry `##`. A word of more than 100 characters, or one that
   cannot be cut completely, is one `[UNK]`.

Truncation keeps the first `maxLength - 2` tokens between `[CLS]` and `[SEP]`; a shorter limit gives a prefix of a longer one. Work and
memory per call are bounded by the longest word, and encoding stops when the output is full.

### Conformance

Token ids against the Hugging Face `tokenizers` package (0.23.3, run in a `uv` venv, on the model's own `tokenizer.json`):

- All 1,112,064 Unicode scalar values (surrogates excluded), each as `Ab<c>Cd` and alone: 2,223,033 of 2,224,128 encodings identical.
  The 1,095 other encodings belong to 618 scalar values, listed in `testdata/models/wordpiece-reference-differences.txt`; the test
  requires exactly that set. 614 are characters that the reference does not know (it treats them as ordinary unknown characters,
  while the runtime's Unicode data classifies them as marks, punctuation or format characters); four, U+166D, U+1734, U+111C9 and
  U+1171E, have a different general category in the two data versions (the reference splits U+166D as punctuation and drops
  U+1734 and U+1171E as marks, which the runtime does not).
- 12,547 sampled texts (adversarial strings, random strings over awkward alphabets, every SciFact, ArguAna and SCIDOCS query,
  2,000 passages of each): all identical.

## Default embedder: bge-small-en-v1.5

BAAI/bge-small-en-v1.5, MIT licence, revision `5c38ec7c405ec4b44b94cc5a9bb96e735b38267a`: 12 layers, 384 dimensions, 33M parameters.

- Pooling and normalization as the model card: the first token's hidden state, scaled to unit length (floor 1e-12).
- Queries get `Represent this sentence for searching relevant passages: `; documents get nothing. At most 512 tokens per text.
- Run by OneModel in its default (fast) mode. `ExactArithmetic` selects its exact mode.
- Batching: texts are tokenized, sorted by length and run in batches under a token budget (2,048 padded tokens by default); lengths
  are padded to a multiple of 64 and row counts rounded up to a power of two, so the model sees few distinct shapes (each shape is a
  separate plan inside OneModel and costs memory).

### Weights

`tools/models/prepare_bge_small.py` downloads the published ONNX at the pinned revision (SHA-256 checked), and writes:

- `model.onnx`: fp32 as published (133 MB as OneModel file).
- `model.int8.onnx`: **shipped.** The 72 matrix-multiply weights and the word-embedding table are stored as int8 with one scale per
  output channel (per row for the table); the graph dequantizes with `DequantizeLinear` on constants, which OneModel folds when it plans.
  Activations stay float32, so a text's vector does not depend on the other texts in its batch. 34.9 MB as OneModel file.
- `model.int8dyn.onnx`: comparison only. ONNX Runtime's dynamic quantization (int8 weights and per-tensor int8 activations). Not
  shipped: a text's vector then depends on what else is in the batch (cosine 0.995 between batched and single runs in our test).

`tools/OneIndex.Models.Tool convert` turns each into a OneModel file with `ModelFormat.Convert`; `tools/models/pack-weights.sh` places
the result where `OneIndex.Models.csproj` embeds it. The weights are never committed.

Measured effect of the quantization and the BEIR reproduction: see the tables below.

RESULTS_PLACEHOLDER
