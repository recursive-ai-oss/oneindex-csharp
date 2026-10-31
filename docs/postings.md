# Postings, field lengths and block-max WAND

Formats of the **postings** part (codec `postings`, version 1) and the **lengths** part (codec `lengths`, version 1), and the top-k disjunction that uses their block impacts. File frame, integers and varints as in `commit-format.md`.

## Postings file

    frame header   magic "OIXP", version 1
    postings blobs, one per term, back to back, in the order the terms were written (fields in ordinal order, terms in byte order within a field)
    frame footer

A term's blob is found from the offset in its statistics (`term-dictionary.md`); the blob does not say how long it is, and a reader never needs to know. Whether positions and offsets are stored is a property of the field (the flags in the terms file), not of the blob.

### Blob

A term with `df` documents has `blocks = ceil(df / 128)` blocks; all are full (128 documents) but the last. Within a blob document ids are absolute and strictly increasing.

    blocks = 1:   varint lastDoc, varint maxFreq, varint minLen, then the block
    blocks > 1:   varint maxFreq, varint minLen              term-level impact
                  blocks x skip entry (16 bytes: u32 lastDoc, u32 dataOffset, u32 maxFreq, u32 minLen)
                  the blocks

`dataOffset` is the offset of a block from the start of the first block. **Impact** of a run of documents = (largest term frequency in the run, shortest field length of any document in it); the term-level impact covers every document, a skip entry covers its block (Ding and Suel 2011: the per-block maximum is what lets a query skip blocks).

### Block

    u8 docWidth, u8 freqWidth                bits per value, 0 to 32
    packed gaps     n values of docWidth bits      gap = doc - previousDoc - 1; the first previousDoc is the previous block's last document (-1 for the first block)
    packed freqs    n values of freqWidth bits     frequency - 1
    positions       only if the field stores positions, then per document, per occurrence:
                      varint position - previous position of the same document (the first is absolute)
                      with offsets also: varint zigzag(startOffset - previous start), varint endOffset - startOffset

Values are bit-packed least significant bit first into `ceil(n * width / 8)` bytes. The width is the narrowest that fits the largest value in the block (frame of reference with delta coding: Zukowski et al. 2006; Lemire and Boytsov 2015), so a dense run of documents costs 0 bits per gap and a list of frequencies of 1 costs 0 bits per frequency. Positions are varints, not bit-packed; a reader skips the positions of documents it passes without decoding them into values.

Choice of 128: 128 gaps decode in one tight loop, a block's doc and frequency bytes fit one small read (at most 1,026 bytes), and the skip table costs 16 bytes per 128 documents (about one bit per document).

### Reading

`TermPostings` reads a term's postings straight from storage ranges: the blob header, the 16-byte skip entries it needs, one block at a time (one read for the widths, gaps and frequencies), and a 512-byte window over the positions. Advance finds the target's block by galloping and then binary-searching the skip entries (reading each entry from storage), loads that block and searches inside it. After `Reset` there are no allocations while iterating, advancing or reading positions; one instance can be pointed at another term with `Reset`.

Checks, each raising `CorruptIndexException` naming the file: the term's statistics (document frequency in 1 to `maxDoc`, total frequency at least document frequency, offset inside the data area); header numbers in range; every skip entry's last document below `maxDoc`; a block inside the file; widths at most 32; documents strictly increasing, ending exactly at the block's last document, and at least as far apart as the block needs; frequencies below 2^31; position numbers within 32 bits and positions and offsets in range. Reads outside the file fail before they allocate.

## Lengths file

The number of terms each document has in each field (the BM25 field length): the count of tokens added, stacked synonyms included, so a field's lengths sum to its `sumTotalTermFreq`.

    frame header   magic "OIXL", version 1
    varint maxDoc
    repeat per field, ordinal order: u8 1, string name, u8 width (1 to 4 bytes), maxDoc x width-byte little-endian lengths
    u8 0
    frame footer

Read into memory at open (limit `IndexLimits.MaxResidentFileBytes`); document count must equal the terms file's.

## Writing

`InvertedBuffer` collects tokens per field (terms in a hash table, each term's documents, frequencies and positions in one growing integer array) and `WriteTo` feeds a `TextSegmentWriter` in term order. `TextSegmentMerger` feeds the same writer from several segments: per field a k-way merge of the term enumerators by byte order, postings copied through a document map (deleted documents map to -1; a term left with no documents is dropped). The writer is the only code that produces these bytes, and the format has exactly one encoding for a given content, so a merged segment's three files are byte-identical to files built directly from the surviving documents (tested). Terms longer than the limit are skipped by the buffer and refused by the writer.

The writer's calls are synchronous (it writes through the storage interface as terms finish); `Finish` and `FinishAsync` fsync the three files. A crash before the commit that lists the files leaves unreferenced files and nothing else.

## Block-max WAND

`BlockMaxWandScorer` is a disjunction of `Scorer`s that returns documents in increasing order and uses the collector's threshold (`SetMinCompetitiveScore`) to skip (Broder et al. 2003; Ding and Suel 2011). Each round: clauses are ordered by current document; the **pivot** is the first clause at which the sum of the clauses' `MaxScore` bounds reaches the threshold; clauses before the pivot are advanced to the pivot document; then, if the clauses up to the pivot are all at the pivot document, the bounds of their *blocks* containing it (`AdvanceShallow`, `GetMaxScore`) are summed, and if that sum is below the threshold every document up to the nearest block end is skipped (to the next clause's document if that is closer); otherwise the document is returned for scoring. A bound is skipped past only when it is below the threshold by a relative margin of 1e-5, which covers float rounding from adding bounds in a different order than scores. A document's score is the sum of its clauses' scores in clause order, so it equals exhaustive evaluation bit for bit, and with no threshold the scorer is a plain disjunction.

`PostingsScorer` connects a `TermPostings` to the bound: its score function takes (frequency, field length) and must be non-negative, non-decreasing in frequency and non-increasing in length; the block bound is the function at (max frequency, min length). BM25F supplies its own function.

### Measured on seeded corpora (test run)

Top 10 (and 3, 100) over 240 queries per corpus of 1, 2 and 5 terms drawn from common and rare terms; the result equals the exhaustive top-k (documents, order, scores) in every query. Documents handed to the collector, as a share of all matching documents: 24% to 29% for k = 10 on 6,000 and 12,000 documents, 25% for k = 3, 61% for k = 100 on 3,000 documents. Block bounds never returned more documents than whole-list bounds alone (about 5% fewer in these runs).
