using System.Globalization;
using System.Security.Cryptography;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>Another writer already has the index open. Only one writer, in any process, may modify an index at a time.</summary>
public sealed class IndexLockedException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="lockName">The lock file that is held.</param>
    public IndexLockedException(string lockName)
        : base($"The index is locked: another writer holds '{lockName}'. Close it (or let its process end) and try again.")
    {
        LockName = lockName;
    }

    /// <summary>The name of the lock file another writer holds.</summary>
    public string LockName { get; }
}

/// <summary>
/// The files that coordinate processes sharing one index directory: the writer lock and reader leases (docs/processes.md).
/// A lease is an exclusively locked, empty file named <c>reader-&lt;generation&gt;-&lt;random&gt;.lease</c>. While its lock is held, the
/// writer keeps every file of that generation's commit; when the holder is gone (it exited, or was killed, and the operating
/// system dropped the lock) anyone who finds the lease may delete it.
/// </summary>
internal static class IndexLocks
{
    public const string WriterLockName = "write.lock";

    private const string LeasePrefix = "reader-";

    // Same length as LeasePrefix. A viewer lease pins its generation and every later commit (see AcquireViewLease).
    private const string ViewPrefix = "viewer-";
    private const string LeaseExtension = ".lease";

    public static string LeaseName(long generation, ulong salt, bool openEnded = false) => $"{(openEnded ? ViewPrefix : LeasePrefix)}{generation:x16}-{salt:x16}{LeaseExtension}";

    public static bool TryParseLease(string name, out long generation, out bool openEnded)
    {
        generation = 0;
        openEnded = name.StartsWith(ViewPrefix, StringComparison.Ordinal);
        if (name.Length != LeasePrefix.Length + 16 + 1 + 16 + LeaseExtension.Length
            || !(openEnded || name.StartsWith(LeasePrefix, StringComparison.Ordinal))
            || !name.EndsWith(LeaseExtension, StringComparison.Ordinal)
            || name[LeasePrefix.Length + 16] != '-')
        {
            return false;
        }

        return long.TryParse(name.AsSpan(LeasePrefix.Length, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out generation)
            && generation >= 1
            && ulong.TryParse(name.AsSpan(LeasePrefix.Length + 17, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Takes the writer lock, or returns <see langword="null"/> when the storage cannot lock (nothing to exclude).</summary>
    /// <exception cref="IndexLockedException">Another writer holds it.</exception>
    public static IStorageLock? AcquireWriterLock(IStorage storage, CancellationToken cancellationToken)
    {
        if (!storage.SupportsLocks)
        {
            return null;
        }

        return storage.TryLock(WriterLockName, cancellationToken) ?? throw new IndexLockedException(WriterLockName);
    }

    /// <summary>
    /// Finds the leases whose holders are alive and returns every file of the commits they pin, or <see langword="null"/> when no
    /// lease is live. Leases whose holders are gone are deleted on the way.
    /// </summary>
    public static HashSet<string>? LivePinnedFiles(IStorage storage, IndexLimits limits, CancellationToken cancellationToken)
    {
        if (!storage.SupportsLocks)
        {
            return null;
        }

        HashSet<string>? pinned = null;
        HashSet<long>? done = null;
        IReadOnlyList<string> listing = storage.List(cancellationToken);
        foreach (string name in listing)
        {
            if (!TryParseLease(name, out long generation, out bool openEnded))
            {
                continue;
            }

            IStorageLock? probe = storage.TryLock(name, cancellationToken);
            if (probe is not null)
            {
                // Nobody holds it: the reader is gone. On Unix delete while still holding the lock, so a reader that created the
                // file but has not locked it yet fails to lock it and retries instead of trusting a file we are about to remove.
                // Windows refuses to delete an open file; there the lock is taken atomically at creation, so order does not matter.
                bool deleted = TryDelete(storage, name);
                probe.Dispose();
                if (!deleted)
                {
                    TryDelete(storage, name);
                }

                continue;
            }

            done ??= [];
            pinned ??= new HashSet<string>(StringComparer.Ordinal);
            if (!openEnded)
            {
                PinCommit(storage, generation, limits, done, pinned, cancellationToken);
                continue;
            }

            // A viewer lease pins every commit from its generation on: a near-real-time view may use a segment that only a later
            // commit lists, and a merge may drop that segment from the commit after.
            foreach (string other in listing)
            {
                if (CommitFileNames.TryParse(other, out long g) && g >= generation)
                {
                    PinCommit(storage, g, limits, done, pinned, cancellationToken);
                }
            }
        }

        return pinned;
    }

    private static void PinCommit(IStorage storage, long generation, IndexLimits limits, HashSet<long> done, HashSet<string> pinned, CancellationToken cancellationToken)
    {
        if (!done.Add(generation))
        {
            return;
        }

        string commitName = CommitFileNames.For(generation);
        try
        {
            CommitInfo commit = CommitProtocol.ReadCommit(storage, commitName, limits, cancellationToken);
            pinned.Add(commitName);
            foreach (SegmentInfo s in commit.Segments)
            {
                foreach (SegmentFile f in s.Files)
                {
                    pinned.Add(f.FileName);
                }
            }
        }
        catch (Exception e) when (e is IOException or CorruptIndexException)
        {
            // A lease for a commit that is gone or damaged protects nothing it could still read.
            pinned.Add(commitName);
        }
    }

    /// <summary>
    /// Pins a near-real-time view: the commit of <paramref name="generation"/> (the writer's newest, or 0 before the first) and every
    /// later one. Only the writer takes these, and it is the only process that creates commits, so no listing protocol is needed.
    /// </summary>
    public static ReaderLease? AcquireViewLease(IStorage storage, long generation, CancellationToken cancellationToken)
    {
        if (!storage.SupportsLocks)
        {
            return null;
        }

        for (int attempt = 0; attempt < 100; attempt++)
        {
            IStorageLock? held = storage.TryLock(LeaseName(Math.Max(1, generation), NextSalt(), openEnded: true), cancellationToken);
            if (held is not null)
            {
                return new ReaderLease(storage, held);
            }
        }

        throw new IOException("Could not create a lease file: every name tried was taken.");
    }

    private static bool TryDelete(IStorage storage, string name)
    {
        try
        {
            storage.Delete(name);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pins the newest commit for this process. The order matters: lock a lease for generation G, then list again; if G is still the
    /// newest and the lease is still there, the writer had not yet published G+1, so its later scan will see the lease before it
    /// deletes anything of G. Otherwise start over with the newer generation.
    /// </summary>
    public static ReaderLease? AcquireLease(IStorage storage, CancellationToken cancellationToken, out long generation)
    {
        generation = 0;
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> names = storage.List(cancellationToken);
            long latest = CommitProtocol.LatestGeneration(names);
            if (latest == 0)
            {
                return null;
            }

            string lease = LeaseName(latest, NextSalt());
            IStorageLock? held = storage.TryLock(lease, cancellationToken);
            if (held is null)
            {
                continue;
            }

            IReadOnlyList<string> after = storage.List(cancellationToken);
            if (CommitProtocol.LatestGeneration(after) == latest && ContainsOrdinal(after, lease))
            {
                generation = latest;
                return new ReaderLease(storage, held);
            }

            new ReaderLease(storage, held).Dispose();
        }

        throw new IOException("Could not pin a commit: the index kept changing under the reader.");
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> names, string name)
    {
        foreach (string n in names)
        {
            if (string.Equals(n, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // The salt only makes lease names unique; it is never persisted in an index file and needs no reproducibility.
    private static ulong NextSalt()
    {
        Span<byte> bytes = stackalloc byte[8]; // fixed 8-byte buffer
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}

/// <summary>A held reader lease; disposing it drops the lock and deletes the file.</summary>
internal sealed class ReaderLease(IStorage storage, IStorageLock held) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        string name = held.Name;
        held.Dispose();
        try
        {
            storage.Delete(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A lease that stays is found dead by the next scan.
        }
    }
}

/// <summary>Housekeeping for an index directory that needs no writer and no lock.</summary>
public static class IndexMaintenance
{
    /// <summary>
    /// Deletes the files of superseded commits (older than the newest) that no live reader lease pins and the newest commit does not
    /// use. Files no commit lists, such as an interrupted writer's output, are left to the next writer, which cannot tell them from
    /// another writer's work in progress; so this is safe to run while a writer is active. Readers call it when they let go of a
    /// commit, which reclaims files after the writer is gone.
    /// </summary>
    /// <param name="storage">The writable index storage.</param>
    /// <param name="limits">The limits to enforce; default <see cref="IndexLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of files deleted.</returns>
    public static int CollectGarbage(IStorage storage, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        limits ??= IndexLimits.Default;
        IReadOnlyList<string> names = storage.List(cancellationToken);
        long latest = CommitProtocol.LatestGeneration(names);
        if (latest == 0)
        {
            return 0;
        }

        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (CommitFileNames.TryParse(name, out long generation) && generation < latest)
            {
                candidates.Add(name);
                try
                {
                    CommitInfo old = CommitProtocol.ReadCommit(storage, name, limits, cancellationToken);
                    foreach (SegmentInfo segment in old.Segments)
                    {
                        foreach (SegmentFile file in segment.Files)
                        {
                            candidates.Add(file.FileName);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or CorruptIndexException)
                {
                    // Gone or damaged: only the file itself can go.
                }
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        // The newest commit is read after the candidates were chosen from the listing, never before: a commit published in between
        // only makes more files candidates, and what the newest commit lists is kept.
        CommitInfo newest;
        try
        {
            newest = CommitProtocol.ReadCommit(storage, CommitFileNames.For(latest), limits, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return 0; // a newer commit appeared and the writer already removed this one; the next call sees the new state
        }

        candidates.ExceptWith(IndexWriter.FilesOf(newest));
        HashSet<string>? pinned = IndexLocks.LivePinnedFiles(storage, limits, cancellationToken);
        int deleted = 0;
        foreach (string name in candidates)
        {
            if (pinned is not null && pinned.Contains(name))
            {
                continue;
            }

            if (storage.Exists(name, cancellationToken))
            {
                storage.Delete(name, cancellationToken);
                deleted++;
            }
        }

        return deleted;
    }
}
