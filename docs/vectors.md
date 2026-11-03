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
  `Kernels/QuantizedKernels.cs` (the same loads for the 4-bit dot product, and a byte-to-signed-byte reinterpretation),
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

* Parameters per field: `M` (default 16; layer 0 allows `2M`, the paper's `Mmax0`; see the deviation below), `efConstruction` (default 100), flat threshold
  (default 1,000). Search breadth `ef` is a query parameter (`KnnQuery.Ef`); 0 uses `max(k, 100)`.
* **Layer 0 keeps up to `2M` links chosen at insertion** (the paper chooses `M` and lets reverse links grow a list to `Mmax0`). Measured on the
  384-dimension set (150,000 vectors) at the same build time, recall 0.90 is reached at `ef` 220 instead of 270. The upper layers choose `M`.
* **Software prefetch was tried and dropped.** Collecting a node's unvisited neighbours and reading the first cache line of each before scoring
  (which overlaps the memory reads without any intrinsic) changed the throughput at `ef` 220 by -3 to +7 percent across five settings, which is
  inside the noise of repeated runs on a shared host (medians 1035 to 1141 queries per second, minima 812 to 969), so the plain loop stays.
* **Layer assignment** draws the number of times in a row a 64-bit hash of (field seed, node number) is divisible by `M`: the same
  distribution as the paper's `floor(-ln(uniform) * 1/ln M)` (`P(level >= l) = M^-l`) without a logarithm, whose last bit could differ
  between platforms. The field seed is a fixed constant xor the CRC-32C of the field name. Levels stop at 24.
* **Deterministic build.** One thread, no clock, no random generator: the same vectors in the same order give the same graph on every
  machine (kernels above). Tests build twice and compare every link.
* **Deletes.** A deleted document stays in the graph, because removing it can cut paths. Search walks through it and leaves it out of
  the results. A segment with many deletes is rebuilt by the merge policy like any other.
* **Filters.** A query may carry a filter (a query whose matches are the allowed documents). The segment turns it into a bit set, and
  the search is described under "Filtered search" below.

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

A field declared with `VectorIndexKind.Ivf` has no graph: its clustered lists are in part `ivf` (magic `OIXL`, version 1) and the vectors part is the rerank source; see [ivf.md](ivf.md).

## Query surfaces

* Low level: `KnnQuery(field, vector, k)` with `Ef`, `Filter` and `Exact`. It finds the `k` nearest documents over all segments (each
  segment's best `k`, then the best `k` overall) and scores them; as a clause of a `BooleanQuery` it combines with text queries.
  The filter is applied before choosing neighbours; a `Filter` clause beside it in a boolean query would cut the neighbours afterwards.
* Happy path: `index.SearchVector(x => x.Embedding, vector, neighbors: 20)` returns a `SearchQuery`; `Where(...)` restricts the
  candidates (before the nearest are chosen), `Take`/`Skip` page within the `neighbors` found.
* Query JSON: `{"type":"knn","field":"Embedding","vector":[...],"k":10,"ef":100,"exact":false,"filter":{...},"boost":1}`; the tool
  schema adds one such node per indexed vector field with the vector length fixed.
* A field with a compressed copy is searched on the copy and reranked exactly (next section). Hybrid fusion and reranking by models are separate.

## Compressed copies

A vector field may ask for a compressed copy of its vectors (`[Vector(Quantization = ...)]`, `FieldDefinition.Quantization`). The graph is
built on the full-precision vectors, as always; the copy is what a search walks it with. The full-precision vectors stay in the segment and
reorder the candidates, so a result's score is always the exact one, whatever the codec.

| Codec | Per vector | Distance used while walking | Default oversample |
|---|---|---|---|
| `None` | the float vector | exact | 1 |
| `Scalar8` ("int8") | `d` signed bytes + 16 | integer dot product of 8-bit codes, corrected by the cached interval | 2 |
| `Scalar4` ("int4") | `ceil(d / 2)` bytes + 16 | integer dot product of 4-bit codes with the 8-bit query | 3 |
| `OneBit` ("sign-bit") | `ceil(d / 64) * 8` bytes | Hamming distance of the sign bits (popcount) | 5 |

* **Scalar codecs** (Veasey, "Understanding optimized scalar quantization", Elasticsearch Labs, 2024). Each vector gets its own interval
  `[a, b]`; component `x` becomes the code `round((2^n - 1)(clamp(x, a, b) - a) / (b - a))`. The interval starts at the vector's mean plus or minus
  a fixed number of standard deviations (the normal-distribution optimum: 2.514 for 4 bits, 3.611 for 8) clipped to its min and max, and is refined
  by up to five rounds of coordinate descent on the paper's anisotropic error with lambda 0.1 (an error parallel to the vector counts ten times an
  error perpendicular to it): for fixed codes the best `(a, b)` solves a 2 by 2 linear system, which is solved in closed form, and a round that does not
  lower the error is discarded. The record keeps the codes, then `a`, the step `(b - a) / (2^n - 1)`, the sum of the codes and the squared length of the
  original vector (little-endian `float`, `float`, `int`, `float`). Expanding `(a_x + s_x X)(a_y + s_y Y)` over the `d` components gives the dot
  product from the integer `X . Y`, the two sums and the intervals; for the Euclidean metric the squared distance is `|x|^2 + |y|^2 - 2 x . y`. The query is
  compressed to 8 bits with the same procedure whatever the document codec (the paper quantizes the query with at least 4). Int8 codes are stored as
  `code - 128` so the signed-byte kernel applies (`a` moves by `128 * step`). Int4 codes are packed so that byte `j` holds component `j` in its low
  nibble and component `ceil(d/2) + j` in its high nibble, which lets the kernel split a register into two halves without shuffling. Vectors are not
  centred by a corpus mean, a deviation from the paper that keeps each record self-contained (a merge copies nothing).
* **Sign bit** keeps one bit per component, set when the component is above zero, and compares by Hamming distance. It needs the cosine or
  dot-product metric (a Euclidean field with it is a schema error). It is deliberately plain: no per-vector correction, no centering.
* **Oversample.** A quantized search walks the graph with `width = max(ef, ceil(oversample * k))` and keeps the best `width` candidates by compressed
  distance; every candidate is then measured against its full-precision vector and the best `k` are returned (ties towards the smaller ordinal).
  `oversample` is a field setting (`Oversample`, 1 to 100; 0 means the codec default above) and `ef` is the query's (`KnnQuery.Ef`; 0 means
  `max(k, 100)`), so at the default `ef` the oversample binds only when `oversample * k` exceeds 100.
* **Exact and small cases use floats.** `Exact` searches, segments below the flat threshold (no graph, so no compressed copy is written), selective
  filters that fall back to scanning, and walks that exceed their visit limit all use the full-precision vectors.
* **Files.** Part `vecq` (magic `OIXQ`, version 1) beside `vectors` and `hnsw`, with the same header, footer and 64 KB blocks with per-block CRC-32C.
  After the header: `u32` document count; for each quantized field with a graph, in name order, its records in blocks and the block CRC table.
  Directory per field: name, dimensions, codec, record count, record bytes, records per block, data offset, CRC table offset and the CRC of the table.
  A reader checks that the sections tile the file, that the counts equal the vectors file's, that record and block sizes follow from the codec, and
  validates each block as it loads (finite correction factors, code sums within range for scalar codecs, zero padding bits after the last dimension for sign bits).
  Damage raises `CorruptIndexException` naming the file.
* **Schema.** Section 4 of the schema record (after section 3): a count, then for each vector field with a codec or an oversample, in ascending field
  index, the field index, the codec byte and the oversample as the bits of a `float`. A schema without codecs is written byte for byte as before; a
  reader that does not know section 4 skips it and searches the full-precision vectors, which is correct, only slower.
* **Evolution.** Changing the codec or the oversample re-indexes the field from stored source in the background like a metric change. A segment whose
  compressed copy does not match the schema's codec (written before the change) is searched on full-precision vectors until it is rebuilt.
* **Merge** writes the compressed copy of the merged segment from its full-precision vectors; nothing is copied from the sources.
* **Determinism.** Interval selection uses IEEE double arithmetic in a fixed order and integer codes; the integer kernels are exact at every width. The
  records are therefore the same bytes on every machine (a test hashes 100 records of each codec and passes on x64 and arm64).

## Filtered search

A `KnnQuery` with a `Filter` (a keyword term, a numeric or date range over a column or point, any query) turns the filter into a bit set of
documents per segment and then chooses between two ways of answering. The choice is made per segment, on the number of documents the filter admits.

* **Filtered graph walk.** After Patel, Kraft, Guestrin and Zaharia, "ACORN: Performant and Predicate-Agnostic Search Over Vector Embeddings and
  Structured Data", SIGMOD 2024 (section 5, search over the predicate subgraph). The upper layers are walked greedily without the filter, as for
  an unfiltered query. On layer 0 only documents that pass the filter are scored, queued and kept. Where a neighbour fails the filter, its own
  neighbours are looked at instead (neighbours of neighbours), so a rejected node costs a list read and no distance, and the walk is not cut off
  from the accepted nodes behind it. A node stops looking through rejected neighbours once it has collected `2M` accepted neighbours (32 by default), the
  length of an unfiltered layer-0 list. The walk ends as the unfiltered one does, when the nearest unexpanded candidate is farther than the
  `ef`-th best accepted result. If it finds fewer than `min(k, accepted)` results (a filter that splits the graph into pieces) the segment scans.
* **Exact scan.** Below the switch the segment compares the query with every accepted vector (`FlatSearch`); the result is exact.
* **The switch.** The scan is taken when the filter admits at most `20 * max(ef, k)` documents in the segment. The walk's cost depends on `ef` and
  hardly on the selectivity (measured, below), while the scan's cost is proportional to the number of accepted vectors. On 150,000 vectors of 384 dimensions at
  `ef` 100 they cost the same at about 1,500 accepted documents (15 times `ef`), and at `ef` 40 at about 650, so the break-even is close to
  linear in `ef` with a factor of 15. The scan is exact and the walk is not (recall 0.83 to 0.91 at `ef` 100 where the walk alone is used at 1%), so the scan is taken a little past break-even,
  where it costs up to 1.3 times the walk. At the default `ef` of 100 the scan serves segments with up to 2,000 accepted documents, and a larger `ef` widens that range (a caller asking for
  more recall gets the exact scan sooner). Compressed copies use the same rule; the scan reads the full-precision vectors.
* **Compressed copies.** The filtered walk runs on the int8, int4 or sign-bit codes with the same filter, keeps `max(ef, oversample * k)` accepted
  candidates, and reranks them with the full-precision vectors, exactly as the unfiltered search does. Deleted documents are rejected nodes in the same way.
* **Deviations from the paper.** The graph is the ordinary graph. The paper builds ACORN-gamma with a denser neighbour list (expansion factor gamma) so a predicate subgraph
  stays connected, and describes ACORN-1 with the ordinary degree, which is the variant used here. The second hop is taken only through rejected neighbours (the paper expands every neighbour). The switch to the exact scan is the
  paper's own fallback for very selective predicates, with a threshold measured here rather than derived.
* **Hybrid, facets and sort.** The filter belongs to the vector arm (`HybridQuery.Vector.Filter`), as text filters belong to the text arm. A filtered knn clause is an ordinary query, so
  facets, sorts and aggregations see the same K documents.

### Measured

Method: `tools/vector_filtered_rows.sh` (bench `--mode filtered`). The embedding sets above, one segment, `ef` 100 (the default) and the `ef` that reaches 0.95 unfiltered
(450 at 384 dimensions, 270 at 768, float graph). Recall@10 is against the exact top 10 over the passing documents (brute force). Two filters over the same documents: *uncorrelated*, a hash of the document number
(a keyword term, or a range over a number assigned at random, selects the same way), and *correlated*, the lowest slice of the projection of the vector on one fixed random direction (a range over a column that follows the
vectors' position in space, so a query's neighbours are mostly inside the filter or mostly outside it). Selectivities 50%, 10%, 1% and 0.1%. Keyword, column and point filters reach the
search as the same bit set (`tests/OneIndex.Tests/FilteredVectorTests.cs` runs a keyword term, two numeric ranges and a correlated range through `KnnQuery.Filter`). *Post-filter* is the naive
alternative: an unfiltered search for `ceil(40 / selectivity)` neighbours (at most 10,000, with `ef` at least that), keeping those that pass.
Timings are indicative: they ran as queued jobs on a shared 32-core host (x64, AMD Ryzen 9 7950X3D) whose load average was above 15 during the runs; an earlier run on a quiet host (load 4) gave 1.5 to 3 times the queries per second shown.
Compare rows with each other, not with the unfiltered table. Recall does not depend on load.

Recall@10 / queries per second, 384 dimensions, 150,000 vectors, float graph, `ef` 100 (the default):

| Filter | Selectivity (accepted) | Filtered walk with switch | Walk alone | Exact scan | Post-filter |
|---|---|---|---|---|---|
| uncorrelated | 50% (75,002) | 0.866 / 937 | 0.866 / 821 | 1.000 / 56 | 0.843 / 1558 |
| uncorrelated | 10% (15,003) | 0.909 / 1439 | 0.909 / 1236 | 1.000 / 202 | 0.930 / 519 |
| uncorrelated | 1% (1,505) | 1.000 / 4123 | 0.833 / 2499 | 1.000 / 3729 | 0.993 / 74 |
| uncorrelated | 0.1% (152) | 1.000 / 7537 | 0.958 / 5886 | 1.000 / 7481 | 0.210 / 38 |
| correlated | 50% (75,000) | 0.880 / 1116 | 0.880 / 989 | 1.000 / 55 | 0.843 / 1403 |
| correlated | 10% (15,000) | 0.897 / 1433 | 0.897 / 975 | 1.000 / 220 | 0.869 / 408 |
| correlated | 1% (1,500) | 1.000 / 3938 | 0.907 / 2053 | 1.000 / 4362 | 0.801 / 66 |
| correlated | 0.1% (150) | 1.000 / 6279 | 0.938 / 5322 | 1.000 / 7073 | 0.164 / 37 |

At `ef` 450 (384 dimensions): uncorrelated 50% 0.966 / 304 (post-filter 0.954 / 406), 10% 0.984 / 305 (0.938 / 460), 1% and 0.1% 1.000 (exact scan); correlated 50% 0.969 / 303 (0.950 / 361),
10% 0.980 / 282 (0.881 / 455), 1% and 0.1% 1.000 (exact scan; post-filter 0.801 and 0.164).

768 dimensions, 75,000 vectors, float graph, `ef` 100: uncorrelated 50% 0.905 / 640 (post-filter 0.886 / 1100), 10% 0.938 / 719 (0.948 / 404), 1% 1.000 / 7515 (0.995 / 61), 0.1% 1.000 / 13843 (0.234 / 36);
correlated 50% 0.903 / 770 (0.871 / 1100), 10% 0.930 / 971 (0.901 / 309), 1% 1.000 / 5839 (0.859 / 57), 0.1% 1.000 / 12474 (0.158 / 33). At `ef` 270: 50% 0.961 and 0.962, 10% 0.980 and 0.982,
1% and 0.1% 1.000.

int4 copy (oversample 3, float rerank), 384 dimensions, `ef` 100: uncorrelated 50% 0.862 / 1029, 10% 0.905 / 1511, 1% 1.000 / 3493, 0.1% 1.000 / 6738; correlated 0.870 / 1196, 0.889 / 2395, 1.000 / 3326, 1.000 / 7060. At `ef` 450 the recalls are 0.962,
0.981, 1.000, 1.000 (uncorrelated) and 0.967, 0.979, 1.000, 1.000 (correlated). 768 dimensions, `ef` 100: uncorrelated 0.907 / 1228, 0.938 / 2330, 1.000 / 6882, 1.000 / 13564; correlated 0.903 / 1200, 0.932 / 1499, 1.000 / 7037, 1.000 / 13618;
at `ef` 270: 0.963, 0.983, 1.000, 1.000 and 0.961, 0.980, 1.000, 1.000.

What the rows say:

* Filtered recall at a given `ef` is about the unfiltered recall at that `ef` (0.86 to 0.91 at 100, 0.96 to 0.98 at 450 or 270) from 50% down to 10%, and exact below the switch.
* **Post-filtering is acceptable only for mild filters.** At 50% it is as accurate as the walk at the same `ef` and faster (it asks for 80 neighbours, hardly more than the walk visits).
  At 10% it is comparable (uncorrelated) or worse (correlated, 0.87 against 0.90 at `ef` 100, 0.88 against 0.98 at 450). Below 10% it fails (correlated 1%: 0.80; 0.1%: 0.16 to 0.21, where the
  10,000-neighbour cap stops it) or is 30 to 100 times slower than the scan (uncorrelated 1%: 74 against 4,123 queries per second). Post-filtering does not guarantee a full page of `k`; the walk and the scan return `min(k, accepted)`.
* The walk alone loses recall at 1% (0.83 to 0.91 at `ef` 100): few accepted nodes are near a query and the second hop is capped. The switch removes that range.
* The second-hop target (`2M`) was chosen on a sweep from 12 to 64 on the 384-dimension set at 50%, 10% and 4%: a smaller target is faster per `ef` and loses recall, and at matched recall
  the curves are close, so 32 stays (it is the length of an unfiltered list).

Information rows (no managed comparator implements a filtered graph search, so there is nothing to gate against): queries per second at the smallest swept `ef` that reaches the recall,
filtered walk with switch, float graph, 384 dimensions at 0.90 / 0.95 recall: uncorrelated 50% 808 (`ef` 140) / 418 (300), 10% 860 (100) / 607 (200), 1% and 0.1% exact (2621, 5995); correlated 50% 688 / 376, 10% 1064 / 538, 1% and 0.1%
exact (3457, 5893). 768 dimensions: uncorrelated 50% 793 (100) / 438 (200), 10% 1677 (70) / 671 (140); correlated 50% 685 / 461, 10% 1414 / 666; exact below. int4, 384 dimensions: uncorrelated 50% 1328 (140) / 352 (450), 10% 3252 / 1055;
correlated 50% 1055 / 629, 10% 2005 / 716. Peak resident memory of the 384-dimension process was 980 MB for the float index and 982 MB for the int4 index (150,000 vectors; the process also holds the data and the truth sets).

## Method of the measurements

* Embedding sets: `tools/embeddings.py` (the Wikipedia abstracts of the text benchmarks, BAAI/bge-small-en-v1.5 at 384 and
  BAAI/bge-base-en-v1.5 at 768 dimensions, text cut to 128 tokens, queries are held-out article titles with the model's query
  instruction); ground truth is the exact top 100 by dot product computed with numpy.
* Recall: `bench/OneIndex.VectorBench` (`--engine oneindex|oneindex-segment|hnswnet|curiosity`), single-threaded build and search,
  recall@10 against the exact top 10, queries/second as the median of three passes over the query set, `ef` swept over a grid; the
  reported row for a target is the smallest `ef` that reaches it. Hnsw.Net 0.1.4 and curiosity-ai HNSW 26.9.5494 run through their
  public API (the two cannot be referenced together, so `bench/OneIndex.VectorBench.Curiosity` is the same runner for the second).
* Compressed copies: `tools/vector_codec_table.sh` (the `oneindex-segment` engine with `--quant` and `--oversample`); float rows against the comparators:
  `tools/vector_bench_group.sh`.
* Kernels: `bench/OneIndex.Bench` `KernelBenchmarks`.
* Durability: `tests/Durability` carries a vector field (flat threshold 3, so merges of tiny segments build and continue graphs); every
  recovered snapshot is checked for the vector of every live document and for exact nearest neighbours against brute force.
* Filtered search: `tools/vector_filtered_rows.sh`. The durability workload also runs filtered nearest-neighbour queries (a keyword term and a numeric range) after every recovery and requires every hit to pass the filter and the exact result to equal brute force.
* Fuzz: `fuzz` target `vectors` (it includes a filtered walk).

## Measured

Embedding sets (BAAI/bge-small-en-v1.5 at 384 dimensions, 150,000 base vectors; BAAI/bge-base-en-v1.5 at 768 dimensions, 75,000 base
vectors; 1,000 held-out title queries; the first abstracts of the Wikipedia dump; manifests with SHA-256 are written by `tools/embeddings.py`).
The sets are generated, never committed. M=16, efConstruction=100, one thread for build and search, recall@10 against exact search, `ef` on a
grid (the row for a target is the smallest `ef` that reaches it, so a row can overshoot its target). Queries per second are the median of three
passes. The graph builder takes `1.5 x efConstruction` candidates per insertion on layer 0 (see HNSW above).

### Float graph against the managed comparators

`tools/vector_bench_group.sh`, one engine after another on an otherwise idle machine. Hnsw.Net 0.1.4 and
curiosity-ai HNSW 26.9.5494 run through their public API.

| Machine | Set | Engine | Build | Peak RSS | @0.90 (ef, recall, qps) | @0.95 | @0.99 |
|---|---|---|---|---|---|---|---|
| x64 | 384 | OneIndex | 86.4 s | 510 MB | 180, 0.9065, 1494 | 450, 0.9543, 675 | not reached (0.9839 at 1300) |
| x64 | 384 | Hnsw.Net | 88.2 s | 591 MB | 180, 0.9000, 1316 | 640, 0.9604, 445 | not reached (0.9806) |
| x64 | 384 | curiosity-ai | 55.2 s | 675 MB | 900, 0.9150, 331 | not reached (0.9333) | not reached |
| x64 | 768 | OneIndex | 60.6 s | 504 MB | 140, 0.9152, 1284 | 270, 0.9513, 735 | 1300, 0.9914, 198 |
| x64 | 768 | Hnsw.Net | 63.7 s | 577 MB | 140, 0.9073, 1259 | 320, 0.9521, 650 | 1300, 0.9905, 195 |
| x64 | 768 | curiosity-ai | 35.4 s | 676 MB | 450, 0.9037, 481 | 1300, 0.9514, 200 | not reached |
| arm64 | 384 | OneIndex | 83.3 s | n/a | 180, 0.9065, 1313 | 450, 0.9543, 601 | not reached (0.9839) |
| arm64 | 384 | Hnsw.Net | 112.2 s | n/a | 180, 0.9000, 1278 | 640, 0.9604, 437 | not reached (0.9806) |
| arm64 | 384 | curiosity-ai | 64.2 s | n/a | 900, 0.9152, 304 | not reached | not reached |
| arm64 | 768 | OneIndex | 56.8 s | n/a | 140, 0.9152, 1254 | 270, 0.9513, 707 | 1300, 0.9914, 183 |
| arm64 | 768 | Hnsw.Net | 88.3 s | n/a | 140, 0.9073, 1231 | 320, 0.9521, 622 | 1300, 0.9905, 198 |
| arm64 | 768 | curiosity-ai | 45.3 s | n/a | 450, 0.9049, 439 | 1300, 0.9527, 180 | not reached |

Machines: x64 is a Ryzen 9 7950X3D (32 threads), .NET 10.0.402, load 3.7 before and 2.9 to 6.7 during; arm64 is an Apple M2 (8 cores), .NET 10.0.403, load
about 2 to 4 throughout. Index files are 250.6 MB (384) and 240.5 MB (768) for the vectors file plus the graph; Hnsw.Net's saved index is 253.4 MB and 241.9 MB.

Throughput at matched recall against Hnsw.Net, the faster managed comparator at every target both reach (curiosity-ai's graph only reaches 0.90 at `ef`
900 on 384 and 450 on 768, and never reaches 0.95 on 384, so it is far behind at matched recall). The ratio is ours over Hnsw.Net, with queries per second
linearly interpolated between grid points to the target recall; neither engine reaches 0.99 on 384, so the last 384 row is the best recall both reach (0.98).

| Row | x64 | arm64 |
|---|---|---|
| 384 @0.90 | 1.23x | 1.11x |
| 384 @0.95 | 1.34x | 1.24x |
| 384 @0.98 (best both reach) | 1.31x | not measured |
| 768 @0.90 | 1.12x | 1.11x |
| 768 @0.95 | 1.13x | 1.13x |
| 768 @0.99 | 1.09x | 1.00x |
| Build time, ours over Hnsw.Net (smaller is better) | 0.98x (384), 0.95x (768) | 0.74x, 0.64x |

Build time against curiosity-ai is 1.57x (384) and 1.71x (768) on x64: its graph builds faster and is much worse (recall 0.933 at `ef` 1300 on 384).

Native libraries, information only (hnswlib 0.8.0, faiss-cpu 1.15.1 through `tools/native/vector_rows.py`, run on a busy host,
so their throughput is not comparable to the rows above): 384 hnswlib builds in 127 s (contended) and reaches 0.90 at `ef` 270, 0.95 at 640; faiss builds in 36.5 s and
reaches 0.90 at 270, 0.95 at 540. 768: hnswlib 30.9 s, 0.90 at 180, 0.95 at 450, 0.99 at 1800; faiss 24.3 s, 0.90 at 180, 0.95 at 380, 0.99 at 1800.

The first version of this graph needed a larger `ef` than Hnsw.Net for the same recall. The changes (layer-0 neighbours chosen up to the paper's `Mmax0`, and a wider
candidate list on layer 0 during insertion) follow the paper's parameters and were judged only by recall-against-`ef` curves of the comparator's public API. The curves (150,000 x 384; recall@10 at `ef` 140, 220, 450, 1300) were Hnsw.Net .8825, .9138, .9462, .9806; this
the first version of this graph reached 0.90 at `ef` 220 to 270 and 0.95 at 640, and with the changes now in place .8878, .9206, .9543, .9839 (0.90
at 180, 0.95 at 450). On 768 dimensions, factors 1.0, 1.25 and 1.5 reached 0.95 at `ef` 380, 320 and 270, and cost 64.8 s, 94.0 s and 107.2 s to build on a
loaded host (Hnsw.Net 135.7 s in the same job); the factor of 1.5 is the one that keeps the 384-dimension build time at or below Hnsw.Net's (0.98x).
Topping connections up with pruned candidates (`HnswBuilder.KeepPruned`) raised recall at a fixed small `ef` but lowered throughput at matched recall (50,000
vectors, 0.90: 1540 against 1756 qps; 0.95: 853 against 1048), so it stays off.

### Compressed copies

`tools/vector_codec_table.sh`, segment engine (files on disk, rerank from the full-precision file), k = 10. The search width is `max(ef, 10 x oversample)`, so the
`ef = 10` column shows each oversample on its own (width 10, 20, 30, 50, 100) and from `ef = 100` the oversample no longer binds. Recall is deterministic; queries per
second come from a run on a host at load 10 to 25 and are indicative only (single runs vary by a factor of two between neighbouring grid points).

Recall@10 by search width (`ef` for the float and the `bit-o1` rows):

| Set | Codec (oversample) | w=10 | 20 | 30 | 50 | 100 | 220 | 450 | 900 | 1300 | Extra bytes per vector |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 384 | float | .4972 | .6313 | .7076 | .7802 | .8625 | .9214 | .9547 | .9761 | .9837 | 0 |
| 384 | int8 (2) | .6341 | .6341 | .7056 | .7805 | .8606 | .9222 | .9550 | .9759 | .9835 | 400 |
| 384 | int4 (3) | .6921 | .6921 | .6921 | .7733 | .8538 | .9181 | .9539 | .9755 | .9828 | 208 |
| 384 | sign-bit | .2165 | .3500 | .4336 | .5355 | .6649 | .7818 | .8658 | .9264 | .9479 | 48 |
| 768 | float | .5486 | .6814 | .7518 | .8248 | .8926 | .9414 | .9719 | .9862 | .9913 | 0 |
| 768 | int8 (2) | .6831 | .6831 | .7508 | .8260 | .8920 | .9415 | .9706 | .9874 | .9915 | 784 |
| 768 | int4 (3) | .7505 | .7505 | .7505 | .8256 | .8930 | .9422 | .9702 | .9872 | .9918 | 400 |
| 768 | sign-bit | .3014 | .4739 | .5640 | .6702 | .7945 | .8888 | .9441 | .9742 | .9840 | 96 |

(A quantized row at a width below its oversample times 10 shows the oversample's width: int8 at `ef` 10 is width 20, int4 width 30.)

The sign-bit codec by oversample, with the default `ef` of 100 and with `ef` = k (so the oversample alone sets the width):

| Set | Oversample | Width at ef=10 | Recall at ef=10 | Recall at default ef=100 | Indicative qps at ef=10 / ef=100 |
|---|---|---|---|---|---|
| 384 | 1 | 10 | .2165 | .6649 | 10789 / 5682 |
| 384 | 2 | 20 | .3500 | .6649 | 6733 / 4457 |
| 384 | 3 | 30 | .4336 | .6649 | 6096 / 3414 |
| 384 | 5 (default) | 50 | .5355 | .6649 | 5378 / 3822 |
| 384 | 10 | 100 | .6649 | .6649 | 3725 / 3845 |
| 768 | 1 | 10 | .3014 | .7945 | 8096 / 4953 |
| 768 | 2 | 20 | .4739 | .7945 | 7540 / 5877 |
| 768 | 3 | 30 | .5640 | .7945 | 5679 / 7201 |
| 768 | 5 (default) | 50 | .6702 | .7945 | 14323 / 4611 |
| 768 | 10 | 100 | .7945 | .7945 | 4836 / 6347 |

What it costs, in words:

* **Memory.** The graph and the full-precision vectors are in every row; the copy is extra and is read while walking. Per 150,000 x 384 vectors: float
  vectors 225,014 KB, graph 19,752 KB, int8 copy 58,597 KB, int4 copy 30,470 KB, sign-bit copy 7,031 KB. For 75,000 x 768: int8 57,425 KB, int4
  29,298 KB, sign-bit 7,031 KB. Total index bytes: 384: 250.6 MB float, 310.6 MB int8, 281.8 MB int4, 257.8 MB sign-bit; 768: 240.5, 299.3, 270.5, 247.7 MB.
  Peak process memory while building was 844 to 918 MB for every codec (the build holds the float vectors). The compressed copy buys speed and a smaller
  working set for the walk, not a smaller index: the float file stays for the rerank.
* **Recall.** int8 and int4 follow the float curve within 0.01 from a width of 100. The sign bit does not: at width 100 it reaches 0.665 (384) and 0.795 (768),
  and 0.95 needs a width of about 450 on 768 dimensions (0.944) to 640 (0.962 at `ef` 640 in the earlier run), while on 384 dimensions it reaches
  0.926 at 900 and 0.948 at 1300 and not 0.95. The cause is not established (the sign of an uncentred component carries little
  when the vectors share a large common offset is a guess; centring on a corpus mean would test it, and this codec deliberately does not centre). Because the oversample only matters while `10 x oversample` exceeds `ef`, the default of 5
  binds only at `ef` below 50; callers who want the high-recall region of this table raise `ef` (or the oversample, to 45 for width 450).
* **Speed.** Hamming over 384 bits is 4.5 ns and over 768 bits 8.6 ns per pair against 42 and 74 ns for the float dot product, so a walk is much cheaper per
  visited node; at equal recall the extra width eats most of it (the sign bit needs a width of 900 for 0.926 on 384 dimensions where the float graph needs 220, so the
  saving per node is spent on visiting more nodes). int4 at a width of 100 to 450 is about 1.5x to 2.2x the float throughput in the same run.

### Kernels

BenchmarkDotNet, in-process short job, mean per call over one pair of vectors, 0 bytes allocated. Width 0 is the scalar path, 1 Vector128, 2 Vector256, 3 Vector512.

| Machine | Dims | Kernel | Scalar | Vector128 | Vector256 | Vector512 |
|---|---|---|---|---|---|---|
| Ryzen 9 7950X3D | 384 | float dot | 177 ns | 42.5 ns | 24.4 ns | 34.2 ns |
| Ryzen 9 7950X3D | 384 | int8 dot | 230 ns | 31.9 ns | 16.6 ns | 16.7 ns |
| Ryzen 9 7950X3D | 384 | int4 dot | 138 ns | 25.8 ns | 14.4 ns | 16.2 ns |
| Ryzen 9 7950X3D | 384 | Hamming | 5.5 ns | 4.5 ns | 4.5 ns | 4.9 ns |
| Ryzen 9 7950X3D | 768 | float dot | 340 ns | 74.4 ns | 63.5 ns | 61.5 ns |
| Ryzen 9 7950X3D | 768 | int8 dot | 390 ns | 62.7 ns | 31.9 ns | 31.9 ns |
| Ryzen 9 7950X3D | 768 | int4 dot | 273 ns | 50.1 ns | 26.6 ns | 26.6 ns |
| Ryzen 9 7950X3D | 768 | Hamming | 7.9 ns | 8.6 ns | 8.2 ns | 8.2 ns |
| Apple M2 | 384 | float dot | 224 ns | 36.3 ns | n/a | n/a |
| Apple M2 | 384 | int8 dot | 133 ns | 26.2 ns | n/a | n/a |
| Apple M2 | 384 | int4 dot | 126 ns | 23.0 ns | n/a | n/a |
| Apple M2 | 384 | Hamming | 11.8 ns | 12.0 ns | n/a | n/a |
| Apple M2 | 768 | float dot | 419 ns | 69.6 ns | n/a | n/a |
| Apple M2 | 768 | int8 dot | 310 ns | 59.2 ns | n/a | n/a |
| Apple M2 | 768 | int4 dot | 281 ns | 49.8 ns | n/a | n/a |
| Apple M2 | 768 | Hamming | 25.8 ns | 26.2 ns | n/a | n/a |

The Hamming kernel is population counts over 64-bit words, which the runtime compiles to the hardware instruction on both machines; it has no vector path.
All widths give bit-identical results; `VectorKernelTests` and `QuantizedVectorTests` pass on both machines.

Code that uses `unsafe`-class APIs (`Unsafe.`, `MemoryMarshal`, `stackalloc`) is limited to `Kernels/VectorKernels.cs` and `Kernels/QuantizedKernels.cs` (the SIMD
kernels; the 4-bit dot product at 768 dimensions goes from 273 ns scalar to 26.6 ns with Vector256 on x64 and from 281 ns to 49.8 ns with Vector128 on the M2), `Codecs/VectorPartReader.cs` and
`Codecs/VectorFiles.cs` (bulk conversion of file blocks to floats and integers) and `Codecs/VectorSegmentCodec.cs` (the same conversions at merge time); each
states its bounds where it is used.

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
