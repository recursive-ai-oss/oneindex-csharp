# Point index

This note is normative for the `points` part of a segment: range lookups over numeric, date and bool fields without scanning the documents. Frames and the codec interface are in `commit-format.md` and `segments.md`.

## What is indexed

Every field that is indexed and numeric (integral, fractional, date, bool) gets a tree of (value, document) pairs, one pair per value of each document (a multi-valued field contributes several). The value is the same ordered 64-bit integer the query layer uses for range bounds (`NumericTerms.Ordered`): the integer itself for integral and date, 0 or 1 for bool, and `SortableBits.Encode(value + 0.0)` for fractional, so that -0.0 and +0.0 are one value and NaN sorts above every number (a range with an infinite bound leaves NaN out).

## Structure

The tree is one-dimensional, so it is the degenerate case of a block KD-tree: the pairs are sorted by (value, document) and cut into **leaves of at most 512 pairs**; the "inner nodes" are the sorted array of leaf minima and maxima, searched by binary search. A simpler structure would not be faster in one dimension, and the leaves are what a second dimension (geo) would keep: a later format version adds a split tree above leaves of the same shape (Procopiuc, Agarwal, Arge and Vitter, "Bkd-tree: a dynamic scalable kd-tree", SSTD 2003, describe the leaf blocks and bulk-built tree this follows).

Query cost: a binary search over the directory, then the leaves the range touches. A leaf wholly inside the range contributes its documents without looking at values; only the (at most two) boundary leaves are filtered. A leaf is read from storage when a query needs it; only the directory is resident.

## File, `OIXR` version 1

```
header (8 bytes)                 magic "OIXR", version 1
u32  maxDoc
trees                            one per field, back to back
directory
u64  directory offset
footer (12 bytes)
```

Directory: `varuint count`, then per field, ordered by name (strictly increasing): `varuint nameLength, name`, `varuint pairCount`, `i64 minValue`, `i64 maxValue`, `varuint treeOffset`, `varuint treeLength`. Trees tile the file between the `maxDoc` field and the directory.

Tree: `u32 leafCount`, `u32 dataLength`, `leafCount` records of 32 bytes, then the leaf data.

Leaf record: `i64 min`, `i64 max`, `u32 dataOffset` (from the start of the leaf data; leaves are contiguous), `u16 count` (1 to 512), `u8 valueWidth`, `u8 docWidth`, 8 reserved zero bytes. Leaf data: the `count` values minus `min` packed in `valueWidth` bits (the exact number of bits `max - min` needs), then the `count` document ids packed in `docWidth` bits (at most 31).

Writers sort by (value, document), so equal inputs give equal bytes however the pairs were gathered; a merge reads the sources' leaves, drops deleted documents, maps the others through the document map and sorts again, giving the bytes a flush of the same documents would.

## Reading

`GetRange(lower, upper)` takes inclusive ordered integers (the query layer turns exclusive and open bounds into inclusive ones) and returns a `DocIdSetIterator` over the matching documents in increasing order, each once however many of its values match. The iterator is built when `GetRange` returns: matches are gathered into a sorted array when few (an upper bound is known from the leaf counts) and into a bitmap when many (`bound × 32 ≥ maxDoc`: a bitmap costs `maxDoc/8` bytes, an array 4 bytes per match). Iterating, `NextDoc` and `Advance` allocate nothing; building allocates the array or bitmap and a pooled read buffer.

Opening reads the header, the trailer, the directory and each tree's leaf records, and checks: the footer; the directory tiles the file; leaves are in non-decreasing value order and each leaf's value width is exactly what its range needs; the leaf data lengths add up to the tree length; the pair counts agree with the directory. The file's full checksum is the commit's to check (as for postings); a leaf's document ids are checked against `maxDoc` as they are used. All failures raise `CorruptIndexException` naming the file.

## Limits

Document ids and leaf counts are 32-bit. A leaf is 512 pairs, at most about 6 KB. Only the leaf records are resident (32 bytes per 512 pairs).
