# Storage interface

`IStorage` is a flat set of named files. It is the only way the engine touches bytes, so one implementation can be local files, memory, read-only HTTP ranges or browser storage, and a wrapper can see (and break) every write.

## Names

A name is 1 to 255 characters from `A-Z a-z 0-9 . _ -` and does not start with a dot. There are no directories. A name can therefore never leave the storage root, and it is safe in a URL.

## Reading

`OpenRead(name)` returns an `IRangeReader` with a fixed `Length`. `Read(offset, buffer)` fills the caller's buffer and returns the bytes read; it returns fewer than requested only when the range reaches the end of the file, and 0 at or past the end. `ReadExactly` turns a short read into `CorruptIndexException`. A reader is safe to use from several threads. The hot path (a cached range) neither awaits nor allocates; the async form exists for backends that really wait (HTTP).

## Writing

`Create(name)` makes a new empty file (it fails if the name exists) and returns an append-only `IStorageWriter`. `Sync()` flushes and fsyncs the file; bytes are durable only after it returns. Disposing a writer without syncing hands bytes to the operating system but promises nothing about a crash.

`Rename(source, destination)` atomically replaces the destination. `Delete(name)` ignores a missing file. `List()` returns names in ordinal order. `SyncDirectory()` makes creations, renames and deletions durable.

A read-only backend reports `IsReadOnly` and throws `NotSupportedException` from every mutating member.

## What durable means, by platform

The commit protocol needs two promises: a synced file's bytes survive a crash, and, after `SyncDirectory`, so do the names created or renamed before it.

| Platform | File data (`Sync`) | Names (`SyncDirectory`) |
|---|---|---|
| Linux (ext4, XFS, btrfs) | `fsync` on the file | .NET cannot open a directory to fsync it, so each file created or renamed into place since the last call is fsynced again. These file systems commit the directory entry in the same journal transaction, so that makes the entry durable. Deletions are not forced, which is harmless: a deleted file that returns after a crash is garbage no commit names. |
| macOS | full sync of the file (`F_FULLFSYNC`, as .NET does for `FlushToDisk`) | the same file flushes |
| Windows (NTFS) | `FlushFileBuffers` | the same file flushes; NTFS journals metadata |
| Memory | nothing is ever durable beyond the process | no effect |

This is the strongest the platform allows without native code. A file system or a virtualized disk that reorders metadata ahead of data, or ignores flushes, can still break any database; the fault injector tests the protocol against a stricter model than the common file systems provide.

## The fault-injecting wrapper

`FaultInjectingStorage` wraps a writable storage and models a disk with a volatile cache. It sees every `Create`, `Write`, `Sync`, `Rename`, `Delete` and `SyncDirectory` and numbers them. Its model:

- A file's bytes become durable at that file's `Sync`.
- A name (creation, rename, deletion) becomes durable at `SyncDirectory`.
- A rename is atomic: it survives whole or not at all.

A seeded schedule can fail chosen operations (`IOException`, no effect) and can cut the power at a chosen operation (`SimulatedCrashException`; a write that is cut is recorded first, so it may survive torn). `Crash(policy)` then rewrites the wrapped storage to what a real disk may hold:

| Policy | Unsynced writes | Unsynced name changes |
|---|---|---|
| `LoseUnsynced` | all lost | all lost |
| `Tear` | an ordered prefix survives, the write at the cut is torn | an ordered prefix survives |
| `Reorder` | each survives whole, torn or not at all, so a later write can persist without an earlier one (a zero-filled gap) | each survives or not independently |

The same seed gives the same result. After `Crash` the wrapper is usable again with its counter reset.

## Block transform

`TransformingStorage` applies an `IBlockTransform` to every byte by file position: `Encode(offset, data)` on write and `Decode(offset, data)` on read, in place and length-preserving. The identity transform changes nothing. The slot is where encryption at rest will go; the transform must not depend on a file's name because files are renamed into place.

## Limits and exceptions

`IndexLimits` bounds every count and length a reader accepts (commit size, segments, files per segment, string length, schema size, fields, vector dimensions). `CorruptIndexException` names the damaged file; `UnsupportedFormatException` (a subclass) means the file is intact but newer than this build understands.
