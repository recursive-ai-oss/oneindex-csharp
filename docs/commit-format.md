# Index files, commit file and commit protocol

All multi-byte integers are little-endian. A *varint* is an unsigned LEB128 integer in its shortest form (a reader rejects a trailing zero group and values past 64 bits). A *string* is a varint byte length and that many bytes of UTF-8 (invalid UTF-8 is rejected). *Bytes* are a varint length and the bytes.

## The index file frame

Every index file has the same frame:

    offset 0            u32  magic       file-type tag, four ASCII characters (commit file: "OIXC")
    offset 4            u32  version     format version of this file type, at least 1
    offset 8            ...  body
    offset length-12    u32  footer magic   0x4658494F ("OIXF")
    offset length-8     u32  algorithm      1 = CRC-32C
    offset length-4     u32  checksum       CRC-32C of every byte before this field

CRC-32C is the Castagnoli polynomial (reflected 0x82F63B78), initial value and final xor all ones; the check value of "123456789" is `E3069283`. A reader checks, in this order: the file is at least 20 bytes, the magic, the footer magic, the algorithm, the checksum, then the version (so a file that is corrupt is reported as corrupt, and only an intact file from a newer writer as unsupported).

The footer checksum doubles as the file's identity: a commit records it for each file it lists, so a file replaced by different bytes of the same length is noticed from 12 bytes at the end.

## Commit file, version 1

File name `commit-<generation as 16 lowercase hex digits>.oic`. Body:

    u64    generation          at least 1; must equal the generation in the file name
    u64    next segment id     the number the writer names the next segment with
    bytes  applied position    opaque, at most 1,024 bytes; the application's marker (for example how far an external change log has been applied)
    bytes  schema              the serialized schema (below), at most 4 MiB; empty if none
    varint user data count     at most 1,024
      string key, string value     keys strictly ascending by ordinal byte order
    varint segment count       at most 100,000
      string  segment name
      varint  document count   including deleted documents
      varint  deleted count    at most the document count
      varint  delete generation   0 when the segment has no delete bitmap
      varint  file count       at most 1,024
        string part            "terms", "postings", "lengths", "columns", "points", "stored", "deletes", "ids"
        string file name       a valid storage name; no file is listed twice in a commit
        string codec name
        varint codec version
        varint file length
        u32    file checksum    the footer checksum of that file

Segment names are unique within a commit. A parsed commit that is serialized again gives the same bytes: the format is canonical, which the fuzz target checks.

### Codec registry

The codecs this build reads are a fixed list in `CodecRegistry`, each with the newest version it understands. A commit that names an unknown codec, or a newer version than listed, is refused as `UnsupportedFormatException`. The codec's owner raises the number when its on-disk format changes; reading older versions stays supported until a release says otherwise.

## Schema, version 1

    4 bytes   "OIXS"
    u32       version (1)
    varint    field count   at most 4,096
      string  name
      u8      type          1 text, 2 keyword, 3 integral, 4 fractional, 5 date, 6 bool, 7 geo point, 8 bytes, 9 vector, 10 json
      u8      flags         1 indexed, 2 stored, 4 column, 8 facet, 16 multi-valued
      u32     boost         IEEE 754 single bits
      string  analyzer      empty when none
      u8      index options 0 none, 1 docs, 2 freqs, 3 positions, 4 offsets
      varint  dimensions    vector fields only
    string    identifier field name, empty when none

Fields keep declaration order; a field's position is its field id. The reader runs the same validation as the schema builder (unique names, types and options that make sense together, a stored single-valued keyword for the identifier).

## Commit protocol

A commit makes a set of already-written segment files visible:

1. The caller writes each new segment file with `IndexFileWriter`, which appends the footer and either fsyncs the file or leaves it open to be synced together with the rest of the segment, and records its length and checksum.
2. `CommitProtocol.Commit` serializes the commit, then checks that the generation is above the newest commit in storage and that every file the commit lists exists with the recorded length and footer checksum.
3. It verifies the files that no earlier commit listed (segment files never change, so files an earlier commit verified are not opened again), then calls `SyncDirectory` so the names of the new files are durable before any commit refers to them.
4. It deletes any leftover temporary commit file, writes the commit to `commit-<generation>.tmp`, and fsyncs it.
5. It renames the temporary file to `commit-<generation>.oic`, atomically.
6. It calls `SyncDirectory` again. Only now does `Commit` return: a commit that was acknowledged is durable.

A crash before step 5 leaves the previous commit as the newest. A crash after it leaves the new one (or, if the rename was not yet durable, the previous one; the caller had not been told yet). No instant in between is observable by a reader, because a reader only ever lists `*.oic` files and the rename is atomic.

`OpenLatest` finds the highest generation among `commit-*.oic` names, parses it, checks that the name and the recorded generation agree, and checks that every listed file exists with the recorded length and footer checksum. It returns `null` for storage that holds no commit. If the newest commit or a file it lists is damaged it throws `CorruptIndexException` naming that file; it never falls back to an older commit silently, because that would drop acknowledged data without a word. `TryOpenLatest` reports the same as a return value. `VerifyFiles` additionally reads every byte of every listed file and checks the checksums.

Older commit files and the segment files only they list are removed by the writer, after no reader snapshot refers to them (reference counts per file; see `segments.md`). The `ids` and `deletes` parts and the naming of segment files are defined there. Leftover `*.tmp` files are ignored by readers and deleted by the next commit of the same generation or by cleanup.

## How this is tested

A four-commit workload runs under the fault injector with a power cut at every storage operation (77 positions, from before the first operation to after the last), each of the three crash policies and eight seeds: 1,848 schedules. After each, the wrapped storage must open without error, must hold every acknowledged commit or a newer one, and must pass `VerifyFiles`. The same suite run against a protocol with the syncs removed finds violations, which shows the schedules are able to catch a broken protocol.
