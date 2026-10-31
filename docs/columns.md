# Columns

This note is normative for the `columns` part of a segment: per-document values of the fields that filtering, sorting, faceting and aggregation read. File frames and the codec interface are in `commit-format.md` and `segments.md`.

## What gets a column

A field has a column when the schema marks it `Column` or `Facet` and its type is keyword, integral, fractional, date, bool or geo point. A segment holds one `columns` file with the columns that have at least one value in the segment; a field no document of the segment has a value for has no column, and `GetColumn` returns `null`. The file is absent when no field qualifies.

## Value model

A column is a sequence of *values*, in document order, plus a map from documents to runs of values. A document has any number of values (0 for a field it lacks; 1 for single-valued fields; any number for `Multi` fields). Values of one document keep the order the document gave them; they are not sorted or de-duplicated, so every value reads back exactly as written.

Every value is held as a signed 64-bit integer (its *raw* form):

| Type | Raw form |
|---|---|
| integral, date | the integer; a date is milliseconds since 1970-01-01T00:00:00Z |
| bool | 0 or 1 |
| fractional | `SortableBits.Encode(value)`: an order-preserving bijection of the IEEE 754 bits, so NaN payloads and the sign of zero are kept exactly and the minimum and maximum of a column are meaningful |
| geo point | two sequences: latitude and longitude, each `SortableBits.Encode` of the double |
| keyword | the ordinal of the value in the segment's sorted dictionary |

## File, `OIXK` version 1

```
header (8 bytes)                 magic "OIXK", version 1
u32  maxDoc                      documents in the segment; must equal the commit's count
sections                         one per column, back to back
directory                        see below
u64  directory offset            absolute offset of the directory
footer (12 bytes)                checksum
```

Directory: `varuint count`, then per column, ordered by field name (ordinal comparison of the UTF-16 name, strictly increasing): `varuint nameLength, name (UTF-8)`, `u8 type` (the `FieldType` value), `u8 layout`, `varuint documentsWithValue`, `varuint valueCount`, `i64 minRaw`, `i64 maxRaw` (the smallest and largest raw value; a column with no values is not written), `varuint sectionOffset`, `varuint sectionLength`. Sections tile the space between the `maxDoc` field and the directory exactly.

### Layouts

The layout is the cheapest map from a document to its values:

| Layout | When | Section |
|---|---|---|
| 0 dense | every document has exactly one value | the value sequence; the value index is the document id |
| 1 optional | no document has more than one value, some have none | a presence bitmap (`ceil(maxDoc/64)` little-endian `u64`), then the value sequence of the documents that have one; the value index is the number of set bits before the document (the reader keeps a rank per 64 documents) |
| 2 multi | some document has several values | an offsets sequence of `maxDoc + 1` integers (value index where each document's run starts; the last is the value count), then the value sequence |

A keyword column's section starts with its dictionary: `u32 termCount`, `u32 totalBytes`, `termCount + 1` `u32` offsets into the bytes, then the UTF-8 terms back to back. Terms are distinct and strictly increasing in byte order, so an ordinal order is the keyword order and a keyword is found by binary search. A geo column has a second value sequence (longitude) after the first.

### Integer sequences

Offsets and values use one encoding, chosen for the access pattern: a read of one value must not decode a block, and a scan must decode whole blocks fast.

```
u32 count; u32 blockCount; u32 dataLength (includes 8 zero bytes of padding at the end)
blockCount records of 24 bytes:  u8 kind, u8 width, u16 0, u32 dataOffset, i64 base, i64 aux
data
```

Blocks hold 1,024 values (the last holds the rest). Each block is stored in the smallest of three forms:

1. **Constant** (kind 0): every value equals `base`; no data.
2. **Packed** (kind 1): frame of reference with a common divisor. `aux` is the greatest common divisor `g` of the differences from the block minimum (`base`); value = `base + g × u`, with `u` packed in `width` bits (1 to 64), little-endian bit fields. The divisor catches timestamps in whole seconds and prices in cents; the base catches clustered values such as sorted or recent timestamps and the monotone offsets of multi-valued columns.
3. **Table** (kind 2): used when the block has 2 to 256 distinct values and the range is wide (more than 8 bits) and a table is smaller. `aux` is the number of distinct values `n`; data is `n` sorted `i64` followed by the values' indexes in `width = bits(n-1)` bits each. This catches status codes, enum-like values and a few repeated large numbers.

Why not delta chaining (each value minus the previous) or entropy coding: both make a random read cost a prefix scan or a variable-length decode. Frame of reference per 1,024 values costs a few bits more and lets `Get(i)` be one record read plus one unaligned 8-byte load, with no allocation.

## Reading

The file is read into memory when the segment is opened (bounded by `IndexLimits.MaxResidentFileBytes`) and its checksum is verified. Opening checks the whole structure: the directory tiles the file; section lengths agree; each integer sequence's block records are valid and stay inside their data (kind, width, divisor, table size); a dense column has one value per document; a presence bitmap's population equals the value count; offsets start at 0, never decrease and end at the value count; dictionary offsets span the bytes and terms strictly increase. Anything else raises `CorruptIndexException` naming the file. Keyword ordinals and table indexes are range-checked when read, since checking every value at open would cost a full scan.

After open, `HasValue`, `ValueCount`, `GetInt64`, `GetFloat64`, `GetKeyword`, `GetOrdinal`, `GetGeoPoint` and `ReadValues` neither allocate nor await. `ReadValues(firstValue, destination)` decodes a run of raw values in bulk (a dense column's value index is the document id). `GetKeyword` returns a span into the resident file, valid for the life of the reader. Readers are immutable and safe for concurrent use.

## Merge

A merge reads each live document's values from the sources through the document map and writes the result with the same code a flush uses. A keyword dictionary is rebuilt from the keywords the surviving documents use (so a deleted document's keywords disappear) and ordinals are remapped. The merged file is byte-for-byte the file a flush of the same documents in the same order produces; a test asserts it.

## Limits

Value counts and document counts are 32-bit. A dictionary holds at most 1 GiB of keyword bytes. A column file is bounded by `MaxResidentFileBytes` (1 GiB by default).
