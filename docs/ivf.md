# IVF: a clustered vector index

An inverted-file (IVF) index groups vectors by cluster and answers a query by scanning only the few clusters whose centres are nearest to it (Jegou, Douze and Schmid, "Product quantization for nearest neighbor search", TPAMI 2011, section 2; the clustered lists follow Sivic and Zisserman, ICCV 2003). Unlike a graph, a search reads a handful of contiguous ranges, which is what a static index needs: one range read per probed list.

```csharp
byte[] image = IvfBuilder.Build(vectors, dimensions, new IvfOptions { Storage = IvfStorage.EightBit });   // or IvfBuilder.Write(storage, name, ...)
using IvfIndex index = IvfIndex.Open(storage.OpenRead("v.ivf"));
IvfHit[] hits = index.Search(query, k: 10, nprobe: 16);                  // Id is the vector's number; Score larger is nearer
```

IVF is the vector layout of the static index ([static-index.md](static-index.md)). On the server a vector field uses HNSW by default; `[Vector(Index = VectorIndexKind.Ivf)]` (or `FieldDefinition.VectorIndex`) makes it an IVF field, described in "IVF as a per-field index" below.

## Building

`IvfBuilder` runs k-means on a sample (64 training vectors per list by default, 12 Lloyd iterations, seeded; spherical k-means for cosine, so the centres are unit vectors). The number of lists defaults to about the square root of the vector count, from 1 to 4096. The same input and options give the same bytes. Every vector goes to its nearest centre.

`IvfOptions.Storage`:
- `FullPrecision`: lists hold float vectors; probed lists are scored exactly.
- `EightBit` (the static default): lists hold the vector's 8-bit codes with one scale per vector; probed lists are scored on the codes, the best `oversample × k` candidates (default 4) are then re-scored from a full-precision region at the end of the file, so reads of full rows are limited to the candidates.

Cosine, dot product and Euclidean metrics follow [vectors.md](vectors.md). A filter callback on the vector number is applied while scanning.

## File format `OIVF` version 1

All integers little-endian.

| Offset | Content |
|---|---|
| 0 | magic `OIVF` (`0x4656494F`), `u32` version 1, `u32` dimensions, `u32` metric, `u32` list count, `u32` vector count, `u32` storage (0 full precision, 1 eight bit), 4 bytes zero |
| 32 | `i64` offset of the full-precision region (0 when storage is full precision), 8 bytes zero. The header is 48 bytes. |
| 48 | centroids: list count × dimensions `f32` |
| after | directory: per list `i64` file offset of the list, `u32` count, `u32` first position |
| lists | per list: its vector numbers (`u32` each), then its vectors (`f32 × dimensions`) or codes (`f32` scale, then `i8 × dimensions`) |
| rerank | eight-bit only: the full-precision vectors in list order (`f32 × dimensions` each), reached by position |
| end | CRC-32C of everything before it (`u32`), magic `OIVF` again (`u32`) |

Readers check the magic, version, sizes against the file length, each directory entry against the list region, each vector number against the count, and the CRC (on by default; `verifyChecksum: false` for a reader that has checked the bytes another way, such as a static index's per-chunk CRCs). Damage raises `CorruptIndexException`. The file is at most 2 GiB.

## IVF as a per-field index

`FieldDefinition.VectorIndex` (`Hnsw` default, or `Ivf`) and `IvfLists` (0 = about the square root of the segment's vector count) are stored in schema section 6; a reader that does not know the section skips it and searches the full-precision vectors. Changing the option re-indexes the field from stored source in the background, like a metric change.

- **Part `ivf`** (magic `OIXL`, codec version 1), beside `vectors`: `u32` document count, then for each IVF field a complete `OIVF` image, then a directory (field count; per field its name, dimensions, vector count, image offset and length) and the checksummed footer. The images use `IvfStorage.EightBitCodes` (storage value 2: the codes with a scale per vector and no float rerank region; version 1 of `OIVF` allows storage 0 to 2). The full-precision vectors stay in the `vectors` part and are what the rerank reads.
- **When it is written.** On flush and on merge, for segments with at least `FlatThreshold` vectors (smaller segments are scanned exactly); a merge rebuilds the lists from the merged vectors (no graph is written for the field). The part is skipped, and the field is scanned exactly, if its image would pass 2 GiB.
- **Search.** `KnnQuery.Ef` means the number of probed lists for an IVF field (0 = max(8, lists / 8)). The probed lists are scored on the codes, the best 3 x k candidates (the oversample) are re-scored from `vectors`, and a filter and the deletes apply while scanning. Sync and async twins as for every vector query.
- **Damage.** The reader validates the directory against the file and every image through `IvfIndex.Open`; a bad file raises `CorruptIndexException` naming the part. The `vectors` fuzz target covers the part.

Measured (indicative: the worker, one thread per query, load average 7-18 from other jobs, the same 150,000 x 384 and 75,000 x 768 embedding sets as below, M = 16 and efConstruction = 100 for HNSW, `tools/vector_ivf_rows.sh`; the sizes are the whole segment, `vectors` included):

| | recall@10 | QPS | build | index bytes | peak RSS |
|---|---:|---:|---:|---:|---:|
| 384 d HNSW ef 100 / 200 / 400 | 0.863 / 0.915 / 0.951 | 2,174 / 1,237 / 663 | 89.9 s | 250.6 MB | 945 MB |
| 384 d IVF nprobe 16 / 48 / 96 | 0.734 / 0.879 / 0.938 | 434 / 160 / 82 | 9.2 s | 289.8 MB | 1,414 MB |
| 768 d HNSW ef 100 / 200 / 400 | 0.893 / 0.937 / 0.968 | 1,541 / 840 / 423 | 57.6 s | 240.5 MB | 856 MB |
| 768 d IVF nprobe 16 / 48 / 96 | 0.757 / 0.891 / 0.953 | 289 / 100 / 45 | 10.3 s | 289.5 MB | 1,360 MB |

On text embeddings HNSW is 3 to 8 times faster at equal recall; IVF builds about 6-10 times faster and its file is a plain list layout (no pointer walk), which is why it is the static layout and an option for fields that are rebuilt often or read from slow storage. Its recall includes the float rerank. Real text embeddings have little cluster structure, so recall 0.95 needs about a quarter of the lists.

## Recall and speed

Measured on the worker (AMD Ryzen 9 7950X3D, 32 threads; one thread per query; Linux; the file was read through `FileSystemStorage`, in the page cache; the machine was shared with other jobs, so the QPS are indicative: load average 27 at the start of the run and 3 at its end). Sets: 150,000 Wikipedia abstracts embedded with bge-small-en-v1.5 (384 d) and 75,000 with bge-base-en-v1.5 (768 d); 1,000 queries; recall@10 against the exact top 10; cosine; 387 and 274 lists; `oversample` 4; median of 3 passes. Tool: `bench/OneIndex.VectorBench --mode ivf`. There is no managed IVF comparator, so these rows are reported, not gated.

**384 d, 150,000 vectors, 387 lists**

| nprobe | lists scanned | recall@10 | QPS full precision | QPS eight bit |
|---:|---:|---:|---:|---:|
| 1 | 0.3% | 0.309 | 2,550 | 3,638 |
| 4 | 1.0% | 0.536 | 834 | 1,423 |
| 16 | 4.1% | 0.749 | 209 | 413 |
| 48 | 12.4% | 0.880 | 70 | 137 |
| 96 | 24.8% | 0.944 | 35 | 69 |
| 192 | 49.6% | 0.983 | 18 | 35 |

**768 d, 75,000 vectors, 274 lists**

| nprobe | lists scanned | recall@10 | QPS full precision | QPS eight bit |
|---:|---:|---:|---:|---:|
| 1 | 0.4% | 0.351 | 2,028 | 2,740 |
| 4 | 1.5% | 0.567 | 581 | 1,066 |
| 16 | 5.8% | 0.766 | 148 | 294 |
| 48 | 17.5% | 0.894 | 49 | 97 |
| 96 | 35.0% | 0.954 | 25 | 49 |
| 192 | 70.1% | 0.991 | 13 | 25 |

The eight-bit layout gives the same recall as full precision at every probe count (the float re-score repairs the code error) and about twice the speed. Real text embeddings have little cluster structure, so reaching recall 0.95 takes a quarter to a third of the lists; this is the nature of an IVF layout without product quantization, and the reason the static index also keeps the HNSW graph for hybrid queries made through the engine.

Sizes: full precision 226,172 KiB (1.01 × the float vectors) for 384 d and 226,119 KiB (1.00×) for 768 d; eight bit 283,008 KiB and 282,662 KiB (1.26×, the codes plus the float rerank region). Build: 6 to 9 s for either set on one thread. Peak working set of the benchmark process: 1.1 to 2.3 GB, which includes the input vectors (230 MB) and the build's working copies.
