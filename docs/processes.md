# One writer, many reader processes

An index directory can be open in one writer and in any number of readers at once, in different processes. This note says how, what each operating system gives, and what is promised.

## The promise

- **One writer.** A second `IndexWriter.Open` on the same index, from this process or another, fails at once with `IndexLockedException` (an `IOException`; `LockName` is `write.lock`). It does not wait. When the first writer is disposed, or its process exits or is killed, the next `Open` succeeds immediately.
- **Readers see commits only.** `IndexSnapshot.Open(storage)` and `IndexReader.Open(storage)` show one complete commit: the newest one when they looked. Nothing a writer has not committed is visible, and a commit is visible whole or not at all (the atomic rename of docs/commit-format.md).
- **A reader keeps its commit.** While a snapshot is open, in any process, the writer does not delete a file of that commit however many commits and merges it makes. When the snapshot is disposed, or the reader's process dies, the files become garbage and are deleted by whoever looks next.
- **Refresh is cheap.** `IndexReader.Refresh()` lists the directory (one call). Only if a newer commit exists does it open it, pinning the new commit before letting go of the old one.
- **Read-only storage** (HTTP range, `ReadOnlyStorage`, `new FileSystemStorage(dir, readOnly: true)`) cannot lock, so it cannot pin. It still reads, and needs no lock because it cannot write; opening a commit while a writer deletes files can fail with `CorruptIndexException` naming a vanished file, and the caller opens again.

## Files that coordinate

| File | Meaning |
|---|---|
| `write.lock` | Empty. Held with an exclusive lock for the life of a writer. Stays on disk. |
| `reader-<generation 16 hex>-<random 16 hex>.lease` | Empty. Held with an exclusive lock for the life of one open snapshot of that generation. Deleted when the snapshot is disposed. |
| `viewer-<generation 16 hex>-<random 16 hex>.lease` | Empty. Held by the writer for each open near-real-time view. Pins that generation (1 before the first commit) and every later commit, because the view may read a segment that only a later commit lists and a merge may drop it from the one after. |

All are created through `IStorage.TryLock(name)`, so the fault-injecting wrapper sees them and a power cut can lose or keep them like any file. They carry no data, so there is nothing to corrupt; a stale one is only a name.

`TryLock` creates the file if needed and takes an exclusive, non-blocking lock tied to the open handle. It returns `null` if somebody else holds it. The lock goes away when the handle is disposed or the process ends.

## Pinning protocol

Reader, to pin the newest commit:

1. List the directory; let *G* be the newest generation.
2. Create and lock a lease for *G*. If the lock is refused, start over (a new random name).
3. List again. If *G* is still the newest and the lease file is still listed, the commit is pinned. Otherwise drop the lease and start over with the newer generation.
4. Open commit *G* by name and verify its files.

Writer, before it deletes any file that no commit and no in-process snapshot needs any more (after a commit, when a merge's sources are released, at `Open`, at `Dispose`, and at `ReclaimFiles()`):

1. List the leases. For each, try to take its lock. If that works, the holder is gone: delete the lease. If it is refused, the holder is alive.
2. Parse the commit of every live lease's generation; its commit file and every file it lists are not deleted now but remembered, and deleted by a later pass when no lease pins them.

Why this is safe: the writer publishes commit *G*+1 (rename) before it scans leases for the first time after that. A reader whose step 3 saw *G* still newest ran before the rename, so its lease existed before the scan and the scan sees it. A reader whose step 3 ran after the rename sees *G*+1 and starts over. The lease is locked before it is checked, so there is no moment where a commit is relied on without a visible lease. A reader that holds nothing yet can only ever be handed files of the newest commit, which the writer never deletes.

One race needs care on Unix: a reader that created a lease file but has not locked it yet could be found "dead" by a scanner. The scanner takes the lock itself, deletes the file while still holding it, and only then lets go; the reader's own lock attempt then either fails (it starts over) or finds its file gone in step 3 (it starts over). On Windows an open file cannot be deleted, but creation and lock are one atomic call there.

## Who reclaims

- The **writer** reclaims at every commit, at `Dispose` and on `ReclaimFiles()`, and `Open` removes everything no commit lists except what a live lease pins.
- A **reader** that disposes its last snapshot runs `IndexMaintenance.CollectGarbage`, which deletes the files of superseded commits that no live lease pins. It never touches a file that no commit lists, because those may be a running writer's work in progress. So a directory whose writer has gone does not stay full of old commits after its readers leave; files an interrupted writer left behind go at the next writer's `Open`.
- A **killed reader** leaves a lease nobody holds. The next scan, by any writer or reader, deletes it.

## What each operating system gives

`FileSystemStorage.TryLock` opens the file with `FileShare.None`. What .NET does with that:

| OS | Mechanism | Observed |
|---|---|---|
| Linux | The runtime takes `flock(LOCK_EX \| LOCK_NB)` on the open file. Advisory, owned by the open file description, so a second open in the same process conflicts too. Released by the kernel when the descriptor closes or the process dies. A refusal arrives as an `IOException`. | Verified here: same-process and cross-process refusal, release on `kill -9`. Switching it off with `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` makes it a silent no-op; `TryLock` detects that (a second exclusive open succeeds) and throws `NotSupportedException` rather than let two writers think they are alone. |
| macOS | The same `flock` path in the runtime. | Verified by the multi-process campaign on an arm64 Mac (see Tests). |
| Windows | `FileShare.None` is a kernel share mode: the second open fails with a sharing violation, and the handle is closed when the process ends. Mandatory rather than advisory. Readers that open data files with `FileShare.Delete` let the writer delete a file under them; the name vanishes, so later opens of it fail, which is why the lease is needed there too. | Not run: no Windows machine was available. The code paths are the documented share-mode behaviour. |

Limits worth knowing:

- Network and FUSE file systems may not implement `flock` (or implement it per machine); two machines sharing one directory are not supported. The lock is a same-machine mechanism.
- The lock is cooperative: a program that ignores `TryLock` can still write the files.
- A process that is frozen but alive keeps its lease; the files it pins stay until it exits.
- Leases are plain file creates and deletes; on a read-only file system a pinned open cannot be made (use a read-only storage).

## Near-real-time reader

`IndexWriter.OpenNearRealTimeReader()` returns an `IndexReader` over the writer's own state: flushed segments plus its deletes, committed or not. `Refresh()` flushes the buffer into a new segment and shows it; it reopens only if something changed. Rules:

- It is **not durable**. `IsDurable` is `false` on the reader and on every snapshot it hands out, and `Generation` is the last commit the view extends (0 before the first). A crash, or `Dispose` without `Commit`, discards everything it showed.
- It exists only in the writer's process. Other processes read commits.
- Its segment files are pinned by the writer's file counts and by a viewer lease, so neither a merge installed by a commit nor a reader process tidying up superseded commits deletes files a view still reads. Old commits therefore stay on disk until the view is refreshed or disposed.
- It is a copy of the delete state at the moment of the refresh: later deletes do not change a view already handed out.
- Each refresh that finds buffered documents writes a small segment; the merge policy folds them at the next commit. Refresh at the rate freshness needs, not more.
- Dispose NRT readers before the writer.

## Tests

- `tests/OneIndex.Tests/ProcessCoordinationTests.cs`: second writer refused (directory and memory), lock released on dispose and on a failed open, lease keeps files until released, stale lease removed, refresh, read-only open, NRT against committed reads and isolation, NRT not durable.
- `tests/Durability`, command `procs`: one writer process and N reader processes. Readers open, hold, refresh and verify snapshots, comparing each generation's content with the hash the writer recorded before committing, and re-verify every file after the writer committed past them. The parent kills writers and readers at random, restarts them, checks each dead writer's directory against what it acknowledged, periodically starts a second writer that must be refused, and at the end checks that a new writer opens and that no file is left over. `sweep` also opens a writer snapshot and a reader snapshot after every commit, so power cuts and failed operations land on lease files too.
