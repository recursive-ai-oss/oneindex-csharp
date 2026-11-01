# Vectors: distance kernels, HNSW and exact search

This note is normative for the vector part of a segment: what the kernels compute, how closeness becomes a score, how the graph is
built and searched, what the two files contain and how a merge treats them. Measured results are at the end.

## Metrics and scores

A vector field has a metric, chosen in the schema (`[Vector(Metric = ...)]` or `FieldDefinition.Metric`) and stored with it.

| Metric | What is indexed | Internal distance (smaller is nearer) | Score (larger is nearer, never negative) |
|---|---|---|---|
| `Cosine` (default) | the vector scaled to length 1 | minus the dot product | `(1 + cos) / 2`, clamped to 0..1 |
| `DotProduct` | the vector as given | minus the dot product | `1 + dot` if `dot >= 0`, else `1 / (1 - dot)` |
| `Euclidean` | the vector as given | squared Euclidean distance | `1 / (1 + d²)` |

* The query of a cosine field is scaled the same way. A vector of length 0 stays all zeros (its cosine with anything is taken as 0,
  score 0.5). The stored source keeps the original vector; the vectors file keeps the scaled one, which is what `TryGetVector` returns.
* Documents with a NaN or an infinity in a vector are rejected when added (`ArgumentException`), and so are such queries.
* A document may lack a vector. A vector field holds one vector per document (no multi-valued vector fields yet).
* Scores are monotone in the distance, so ranking by score is ranking by distance. Ties break towards the smaller document id.

## Distance kernels

`OneIndex.Kernels.VectorKernels` (internal, no dependency on the rest of the engine) computes dot product, squared Euclidean distance
and sum of squares over `float`, and dot product and squared distance over `sbyte`.

* **One result on every machine.** The float order of additions is fixed by the kernel, not by the instruction set: element `i` goes to
  lane `i mod 16`, in index order, with a separate multiply and add (never a fused multiply-add); the sixteen lanes are folded by halves
  (`j` with `j+8`, then `j+4`, `j+2`, `j+1`). A 512-bit register is those sixteen lanes, 256-bit code keeps two registers, 128-bit code
  four, scalar code sixteen variables. A tail shorter than 16 is padded with zeros, which adds exactly zero. All widths, and x64 and
  arm64, therefore return the same bits, so a graph built on one machine is the graph built on any other. The tests assert bit equality
  against the scalar path for lengths 0 to 300 and compare with a double-precision sum.
* **Special values.** No flushing or clamping: denormals take part at full precision, infinities follow IEEE 754, a NaN input gives a
  NaN result (payload unspecified). Indexed data and queries are checked finite before they reach a kernel.
* **Width.** `Vector512`, `Vector256` or `Vector128` is chosen once from `IsHardwareAccelerated` (false for 512 bits on processors
  where the runtime judges it slower); `Vector128` is also the WebAssembly SIMD path and the arm64 AdvSimd path. The scalar path is the
  fallback and the reference.
* **Integer kernels** widen to 16 bits, multiply, widen to 32 bits and add; integer sums are exact so every width agrees. Dot product
  accepts up to 131,071 elements and squared distance up to 32,768 without overflowing `int`; longer inputs throw.
* Files with `unsafe` constructs, `Unsafe.*`, `MemoryMarshal` or intrinsics: `Kernels/VectorKernels.cs` (loads through
  `Vector128/256/512.LoadUnsafe` behind a length check made once per call, and fixed 16-float `stackalloc` tail buffers),
  `Codecs/VectorPartReader.cs` and `Codecs/VectorSegmentCodec.cs` and `Codecs/VectorFiles.cs` (`MemoryMarshal.Cast` between the bytes of
  a checked block and the floats or ints in it; bounds are those of the block), `Vectors/HnswBuilder.cs` is entirely safe code.

## Exact search

`FlatSearch` compares the query with every accepted vector and keeps the best `k` in a bounded heap. It serves segments below the
field's flat threshold (default 1,000 vectors; they get no graph), is used when a filter is selective (below), and is the ground truth
that the recall measurements and the durability checks use. `FlatSearch.SearchBatch` answers many queries in one pass over the
vectors, a tile of 128 vectors at a time against all queries, which is the shape of a matrix product.

## HNSW

Implemented from Malkov and Yashunin, "Efficient and robust approximate nearest neighbor search using Hierarchical Navigable Small
World graphs", IEEE TPAMI 42(4), 2020 (arXiv:1603.09320): algorithm 1 (INSERT), 2 (SEARCH-LAYER), 4 (SELECT-NEIGHBORS-HEURISTIC, no
extended candidates; connections dropped by pruning are not refilled, which is the default of an internal switch) and 5 (K-NN-SEARCH).

* Parameters per field: `M` (default 16; layer 0 allows `2M`, the paper's `Mmax0`), `efConstruction` (default 100), flat threshold
  (default 1,000). Search breadth `ef` is a query parameter (`KnnQuery.Ef`); 0 uses `max(k, 100)`.
* **Layer assignment** draws the number of times in a row a 64-bit hash of (field seed, node number) is divisible by `M`: the same
  distribution as the paper's `floor(-ln(uniform) * 1/ln M)` (`P(level >= l) = M^-l`) without a logarithm, whose last bit could differ
  between platforms. The field seed is a fixed constant xor the CRC-32C of the field name. Levels stop at 24.
* **Deterministic build.** One thread, no clock, no random generator: the same vectors in the same order give the same graph on every
  machine (kernels above). Tests build twice and compare every link.
* **Deletes.** A deleted document stays in the graph, because removing it can cut paths. Search walks through it and leaves it out of
  the results. A segment with many deletes is rebuilt by the merge policy like any other.
* **Filters.** A query may carry a filter (a query whose matches are the allowed documents). The segment turns it into a bit set. A
  filtered graph search keeps walking through rejected nodes and collects accepted ones until `ef` are found. If fewer than
  `sqrt(ef * n)` documents pass, an exact scan of the accepted vectors is cheaper than walking to `ef / selectivity` nodes and is used
  instead; a graph walk that visits more nodes than there are accepted documents stops and falls back to the exact scan, so the cost is
  bounded by twice the exact one. (ACORN-style traversal is a later unit.)

### Merge

A merge builds the merged segment's graph over the live vectors of the sources in their merged order. When the source with the most
vectors has a graph with the same `M` and none of its vectors is deleted, its graph is copied (node numbers moved up by the number of
vectors before it) and only the other sources' vectors are inserted, in order (the paper's incremental construction; the starting graph
is just a valid state to continue from). Otherwise all vectors are inserted. Both are deterministic functions of the merge inputs; the
first differs from flushing the same documents in one segment, which is documented, not a defect. The benchmark runner times both.

## Files

Both files have the common header (magic, version) and footer (CRC-32C of the file), and both are read through the storage interface by
range. Vectors and level-0 links are read in blocks of about 64 KB holding whole records; each block has its own CRC-32C in a table
that is checked when the block is first loaded, so opening a segment does not read its vectors and damage is found by the first read
that touches it, raising `CorruptIndexException` with the file name. Loaded blocks stay cached for the life of the reader; reading a
cached vector or link neither allocates nor waits. The little-endian layout is read in place, so big-endian platforms are rejected.

**`vectors` (magic `OIXV`, version 1)** after the header: `u32` document count of the segment; for each field, in name order: the
vectors (`count * dims * 4` bytes, `float` little endian, ordinal order, in blocks), for a segment where not every document has a vector
the table of the document of each ordinal (`count * 4` bytes, strictly increasing), and the block CRC table (`4` bytes per block).
Then the directory: field count, and per field the name, dimensions, metric, encoding (0: 32-bit floats; later compressed copies add
parts of their own and rerank from this file), count, flags (bit 0: sparse), offsets of the three sections, vectors per block and the
CRCs of the document table and of the CRC table. Trailer: directory offset (`u64`), CRC-32C of the directory (`u32`).

**`hnsw` (magic `OIXH`, version 1)** after the header: `u32` document count; for each field with a graph, in name order: layer 0 as
fixed-size records of `2M + 1` `int`s (link count, then the links, unused slots zero), in blocks with a CRC table; then the upper layers
as one checked blob: node count, then for each node above layer 0 its number (as the gap from the previous), its level and, for each
layer from 1, the link count and the links (varints). Directory per field: name, node count, `M`, top level, entry point, offsets,
records per block, CRC of the CRC table, offset, length and CRC of the upper-layer blob.

Readers check, before trusting an offset or count: the sections tile the file exactly; counts are within the segment and the limits
object; every link is a node other than the one holding it, with at most `2M` links on layer 0 and `M` above; upper-layer links point to
nodes that are in that layer; the entry point is in the top layer; the ordinal-to-document table is strictly increasing and inside the
segment. A segment smaller than the field's flat threshold has a `vectors` part and no `hnsw` part.

Crash behaviour is that of every part: the files are written, fsynced and listed by the commit; a crash before the commit leaves
unreferenced files that are reclaimed.

## Query surfaces

* Low level: `KnnQuery(field, vector, k)` with `Ef`, `Filter` and `Exact`. It finds the `k` nearest documents over all segments (each
  segment's best `k`, then the best `k` overall) and scores them; as a clause of a `BooleanQuery` it combines with text queries.
  The filter is applied before choosing neighbours; a `Filter` clause beside it in a boolean query would cut the neighbours afterwards.
* Happy path: `index.SearchVector(x => x.Embedding, vector, neighbors: 20)` returns a `SearchQuery`; `Where(...)` restricts the
  candidates (before the nearest are chosen), `Take`/`Skip` page within the `neighbors` found.
* Query JSON: `{"type":"knn","field":"Embedding","vector":[...],"k":10,"ef":100,"exact":false,"filter":{...},"boost":1}`; the tool
  schema adds one such node per indexed vector field with the vector length fixed.
* Hybrid fusion, reranking and quantized codecs are separate units.

## Method of the measurements

* Embedding sets: `tools/embeddings.py` (the Wikipedia abstracts of the text benchmarks, BAAI/bge-small-en-v1.5 at 384 and
  BAAI/bge-base-en-v1.5 at 768 dimensions, text cut to 128 tokens, queries are held-out article titles with the model's query
  instruction); ground truth is the exact top 100 by dot product computed with numpy.
* Recall: `bench/OneIndex.VectorBench` (`--engine oneindex|oneindex-segment|hnswnet|curiosity`), single-threaded build and search,
  recall@10 against the exact top 10, queries/second as the median of three passes over the query set, `ef` swept over a grid; the
  reported row for a target is the smallest `ef` that reaches it. Hnsw.Net 0.1.4 and curiosity-ai HNSW 26.9.5494 run through their
  public API (the two cannot be referenced together, so `bench/OneIndex.VectorBench.Curiosity` is the same runner for the second).
* Kernels: `bench/OneIndex.Bench` `KernelBenchmarks`.
* Durability: `tests/Durability` carries a vector field (flat threshold 3, so merges of tiny segments build and continue graphs); every
  recovered snapshot is checked for the vector of every live document and for exact nearest neighbours against brute force.
* Fuzz: `fuzz` target `vectors`.

## Measured

Indicative runs, one machine, single-threaded build and search, M=16, efConstruction=100, recall@10 against exact search, `ef` on a grid of
22 values (the smallest `ef` that reaches the target is reported, so rows are quantized by the grid). The host was shared with other jobs
(load 3 to 16), so timings are only indicative.

Embedding sets (BAAI/bge-small-en-v1.5 at 384 dimensions, 150,000 base vectors; BAAI/bge-base-en-v1.5 at 768 dimensions, 75,000 base
vectors; 1,000 held-out title queries; the first abstracts of the Wikipedia dump; manifests with SHA-256 are written by `tools/embeddings.py`).
The sets are generated, never committed.

| Set | Engine | Build | Peak RSS | @0.90 (ef, qps) | @0.95 | @0.99 |
|---|---|---|---|---|---|---|
| 384 | OneIndex (in memory) | 61 s | 507 MB | 270, 877 | 640, 413 | not reached (0.976 at ef 1300) |
| 384 | OneIndex (segment files) | 77 s | 915 MB | 270, 642 | 640, 347 | not reached |
| 384 | Hnsw.Net 0.1.4 | 123 s | 591 MB | 180, 1166 | 540, 449 | not reached (0.981) |
| 384 | curiosity-ai HNSW 26.9.5494 | 68 s | 626 MB | 760, 179 | not reached | not reached |
| 384 | hnswlib 0.8.0 (information) | 60 s | 506 MB | 270, 1098 | 640, 386 | not reached |
| 384 | faiss-cpu 1.15.1 (information) | 61 s | 506 MB | 270, 924 | 540, 536 | not reached |
| 768 | OneIndex (in memory) | 43 s | 502 MB | 180, 1078 | 450, 422 | 1800, 137 |
| 768 | OneIndex (segment files) | 53 s | 844 MB | 180, 667 | 450, 306 | 1800, 102 |
| 768 | Hnsw.Net 0.1.4 | 91 s | 573 MB | 140, 1094 | 320, 516 | 1300, 156 |
| 768 | curiosity-ai HNSW | 53 s | 677 MB | 450, 267 | 1300, 78 | not reached |
| 768 | hnswlib 0.8.0 (information) | 44 s | 490 MB | 180, 895 | 450, 370 | 1800, 134 |
| 768 | faiss-cpu 1.15.1 (information) | 33 s | 497 MB | 180, 1203 | 380, 593 | 1800, 158 |

Index files: 250.6 MB (384 d) and 240.5 MB (768 d) for the vectors file plus the graph; Hnsw.Net's saved index is 253.4 MB and 241.9 MB.
Against the faster managed comparator (Hnsw.Net) our in-memory search reaches 0.75x, 0.92x (384 d) and 0.99x, 0.82x, 0.88x (768 d) of its
throughput at 0.90, 0.95, 0.99, and builds 2.0x faster. At equal `ef` OneIndex and hnswlib reach the same recall (the same algorithm and
parameters), Hnsw.Net reaches a given recall at a smaller `ef`. Tuning the search is left for the stage's benchmark rows.
On a 50,000-vector prefix, topping connections up with pruned candidates (`HnswBuilder.KeepPruned`) raised recall at a fixed small `ef`
(0.681 against 0.657 at ef 20) but lowered throughput at matched recall (0.90: 1540 against 1756 qps; 0.95: 853 against 1048), so it stays off.

Kernels (BenchmarkDotNet, in-process short job, mean per call, one pair of vectors, 0 bytes allocated):

| Machine | Dimensions | Dot scalar | Vector128 | Vector256 | Vector512 | Int8 dot scalar | Vector128 | Vector256 | Vector512 |
|---|---|---|---|---|---|---|---|---|---|
| Ryzen 9 7950X3D | 384 | 231 ns | 43 ns | 28 ns | 28 ns | 198 ns | 32 ns | 18 ns | 17 ns |
| Ryzen 9 7950X3D | 768 | 371 ns | 78 ns | 51 ns | 58 ns | 405 ns | 65 ns | 34 ns | 35 ns |
| Apple M2 (AdvSimd) | 384 | 228 ns | 36 ns | n/a | n/a | 125 ns | 25 ns | n/a | n/a |
| Apple M2 (AdvSimd) | 768 | 399 ns | 65 ns | n/a | n/a | 244 ns | 49 ns | n/a | n/a |

All widths give bit-identical results; `VectorKernelTests` passes on both machines.

Code that uses `unsafe`-class APIs (`Unsafe.`, `MemoryMarshal`, `stackalloc`) is limited to `Kernels/VectorKernels.cs` (the SIMD kernels),
`Codecs/VectorPartReader.cs` and `Codecs/VectorFiles.cs` (bulk conversion of file blocks to floats and integers) and
`Codecs/VectorSegmentCodec.cs` (the same conversions at merge time); each states its bounds where it is used.

## Schema record and evolution

The dimension of a vector field is part of its field record. Its other settings (metric, M, efConstruction, flat threshold) are
written in schema section 3 (schema format 2: sections are an ascending id and a length-prefixed payload; a reader that does not know
a section skips it). Section 3 is a varint count, then per vector field whose settings differ from the defaults, in ascending field
index: the field index, the metric byte, M, efConstruction and the flat threshold as varints. A schema whose vector fields all use
the defaults is written as format 1, byte for byte what it was before settings existed. Reading rejects a section that names a
field that is not a vector, is out of order, is empty, or holds an unknown metric.

On open, a changed metric, M, efConstruction or flat threshold rebuilds the vectors from stored source in the background like an
analyzer change (the graph and the scores both change). A changed dimension is a schema evolution error naming the field.

## Embedding set hashes

SHA-256 of the files written by `tools/embeddings.py` (the sets are not committed).

| Set | File | SHA-256 |
|---|---|---|
| bge-small-en-v1.5, 384 d, 150,000 vectors (rev 5c38ec7c405ec4b44b94cc5a9bb96e735b38267a) | `base.f32` | `0b5dd91f914327a2f6215f3b1d587c3bf0e86625208e3c82d74c4646a09fce5b` |
| | `queries.f32` | `be7b933cd71815d93010c47579432d2fd3ee051a86afc0c4ea6e2cb5255207d7` |
| | `truth.i32` | `1f422a4d0304cfdb37c2976a5856c8667ef6274e122be4b2eb74b5f2f9663406` |
| bge-base-en-v1.5, 768 d, 75,000 vectors (rev a5beb1e3e68b9ab74eb54cfd186867f64f240e1a) | `base.f32` | `edd186263e2aceee456c19359f3ecf99d575eb5b50842c31f9623b202034c7fc` |
| | `queries.f32` | `84c9ec58d1e583172c11dccb0a3a31f079da4cc06c45c173093f679e9f85a04d` |
| | `truth.i32` | `b7e271b9963e0aa77e8cb5694fc4bc93a7f1b00e4f6774800ccabd905fec3839` |
