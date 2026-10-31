# Stored source

This note is normative for the `stored` part of a segment: the original values of the fields the schema stores, compressed in blocks, with random access by document id. Frames and the codec interface are in `commit-format.md` and `segments.md`.

## What is stored

Every field whose `Stored` flag is set (the default) is kept, with the value exactly as given: text, keyword, integer, float (every bit), bool, date, geo point, bytes, vector and JSON. Values keep the order the document gave them, so multi-valued fields read back in order. A schema in which no field is stored has no `stored` part; a type opts out of stored source by marking its fields not stored, and a field marked not stored is simply absent from the documents read back.

The stored form is also the *source* of a document: `TryGetSource` returns its bytes (the encoding below), and `GetDocument` decodes them into a `Document`. The schema evolution machinery reads documents back through the same reader to rebuild other parts.

## File, `OIXQ` version 1

```
header (8 bytes)                 magic "OIXQ", version 1
blocks                           back to back, each compressed or raw
table of contents
u64  table offset
footer (12 bytes)
```

Table of contents: `u32 documentCount`, `u32 blockCount`, `u8 method` (1 = OneIndex LZ), `varuint fieldCount` and the stored field names (`varuint length`, UTF-8) in schema order, then `blockCount` entries of 20 bytes: `u32 firstDocument`, `u64 fileOffset`, `u32 storedLength`, `u32 rawLength`. A block whose `storedLength` equals `rawLength` is stored raw (the compressor did not shrink it); otherwise it is compressed.

The field table is the file's own: records name fields by their index in it, not by schema position, so a file stays readable when the schema later changes. A merge into a different table rewrites the records (below).

### Blocks

Documents are appended to a block until its records reach 32 KB (documents are never split, so a larger document is a block of its own). A block holds `n × u32` cumulative record end offsets, then the records. Finding document *d*: binary search the table for its block, read and decompress the block, slice the record.

A record is `varuint sourceLength`, the source, `varuint auxLength`, the auxiliary bytes. The auxiliary section is empty in version 1; it is reserved for per-document data kept beside the source, such as cached embeddings, so adding it needs no format change.

### Source encoding

`varuint count`, then per value: `varuint fieldIndex`, `u8 kind` (the `FieldValueKind` number), payload:

| Kind | Payload |
|---|---|
| text, JSON | `varuint length`, UTF-8 (JSON as written by `JsonElement.GetRawText`) |
| integral, date | zig-zag varint (a date is milliseconds since the epoch) |
| fractional | 8 bytes, the IEEE 754 bits, little-endian |
| bool | 1 byte |
| geo point | 8 + 8 bytes, latitude then longitude bits |
| bytes | `varuint length`, the bytes |
| vector | `varuint dimensions`, then the 32-bit float bits |

## Compression

Blocks use **OneIndex LZ**, a byte-oriented LZ77 written for this format: a token byte (literal length in the high nibble, match length minus 4 in the low nibble, 15 meaning "add the following bytes, each 255 meaning continue"), the literals, a 16-bit match offset (1 to 65,535) and the match length extension; the last token has literals only. Ziv and Lempel (1977) is the idea; the compressor searches a hash chain of up to 16 candidates per position and takes the longest match.

Why not the BCL's compression (measured with `bench/OneIndex.Bench`, `StoredCompressionBenchmarks`, and a browser-wasm check):

- **Determinism.** Persisted bytes must be the same on every platform. `DeflateStream` and `ZLibStream` output depends on the zlib build beneath the runtime, which differs between runtime versions and platforms.
- **Browser.** In a browser-wasm build (checked in headless Chromium and in Node, .NET 10.0), `DeflateStream`, `ZLibStream` and `GZipStream` round-trip, and gave the same compressed bytes as desktop .NET in that check; `BrotliEncoder` and `BrotliDecoder` throw `PlatformNotSupportedException`. So Brotli cannot be used, and deflate's bytes are not guaranteed to match across builds.
- **Speed.** A document fetch decompresses a whole block. The numbers are in the section below.

Measured on the first 60,000 Wikipedia abstracts (655 blocks of about 32 KB, 21.9 MB of encoded documents; BenchmarkDotNet short runs, in process, on an x64 desktop, indicative only):

| Codec | Ratio | Compress all blocks | Decompress all blocks |
|---|---|---|---|
| OneIndex LZ (16 candidates) | 1.731 | 215 ms | 24 ms |
| deflate, fastest | 1.679 | 74 ms | 42 ms |
| deflate, optimal | 2.245 | 228 ms | 46 ms |
| zlib, optimal | not recorded | 233 ms | 40 ms |
| Brotli quality 4 | not recorded | 255 ms | 52 ms |

Limiting the search to 1, 4 or 64 candidates gives ratios of 1.606, 1.696 and 1.742. OneIndex LZ decodes about twice as fast as deflate and compresses to within 3% of the fastest deflate level; optimal deflate is 30% smaller but costs the determinism and browser guarantees above. The file records its compression method, so a later method with entropy coding can be added without changing the layout.

## Reading

Opening reads the header, trailer and table of contents and validates them: the footer; the document count against the commit; block entries tile the file in order and cover the documents; `storedLength ≤ rawLength ≤` the resident file limit; a compressed block cannot expand beyond 255× (so a small entry cannot claim a huge allocation). A fetch reads the block's range from storage into a pooled buffer, decompresses into another, validates the record table (offsets rise and fill the block) and decodes the record. Decompression refuses anything that is not exactly a valid stream for the recorded size. Every failure raises `CorruptIndexException` naming the file; text that is not UTF-8, JSON that does not parse, and out-of-range dates or coordinates are corruption too.

Readers are immutable and safe for concurrent use. A fetch does not cache blocks; merges read sources through a cursor that decompresses each block once.

## Merge

Live documents are copied in order. When the source's field table equals the target's, records are copied byte for byte (source and auxiliary section). Otherwise the source part of each record is rewritten to the target's field indexes, values of fields the target does not store are dropped and the auxiliary section is copied unchanged. A merged file equals the file a flush of the same documents produces.

## Limits

Block size is 32 KB of records, or one document. A block's raw length is bounded by `IndexLimits.MaxResidentFileBytes`. The table of contents is resident (20 bytes per block).
