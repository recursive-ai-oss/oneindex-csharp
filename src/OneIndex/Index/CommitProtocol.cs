using System.Diagnostics.CodeAnalysis;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// Publishing and finding commits. A commit is visible only when its file has been renamed into place, and the rename happens
/// only after everything the commit refers to is durable:
/// <list type="number">
/// <item>the caller wrote every new segment file and fsynced it (<see cref="IndexFileWriter.Finish"/> does);</item>
/// <item><see cref="Commit"/> checks each file listed is present, complete and the one recorded, then syncs the directory so the
/// names are durable;</item>
/// <item>it writes the commit to a temporary name and fsyncs it;</item>
/// <item>it renames the temporary file over the final name (atomic), then syncs the directory again.</item>
/// </list>
/// A crash before the rename leaves the previous commit as the latest; a crash after leaves the new one. Nothing in between is
/// observable. <see cref="Commit"/> returns only once the new commit is durable, so a returned commit is never lost.
/// </summary>
public static class CommitProtocol
{
    /// <summary>Publishes a commit durably.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="commit">The commit; its generation must exceed the latest one in storage.</param>
    /// <param name="limits">The limits to enforce; default <see cref="IndexLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the commit. Cancelling before the rename leaves the previous commit latest.</param>
    /// <param name="alreadyChecked">Names of files that an earlier commit already verified; segment files never change, so they are not opened again. Default none.</param>
    /// <exception cref="InvalidOperationException">The generation is not newer than the latest commit.</exception>
    /// <exception cref="CorruptIndexException">A listed file is missing, truncated or not the recorded one.</exception>
    public static void Commit(IStorage storage, CommitInfo commit, IndexLimits? limits = null, IReadOnlySet<string>? alreadyChecked = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(commit);
        byte[] bytes = CommitFileFormat.Serialize(commit, limits);
        long latest = LatestGeneration(storage.List(cancellationToken));
        if (commit.Generation <= latest)
        {
            throw new InvalidOperationException($"Commit generation {commit.Generation} is not newer than the latest ({latest}).");
        }

        foreach (SegmentInfo segment in commit.Segments)
        {
            foreach (SegmentFile file in segment.Files)
            {
                if (alreadyChecked is not null && alreadyChecked.Contains(file.FileName))
                {
                    continue;
                }

                using IRangeReader reader = storage.OpenRead(file.FileName, cancellationToken);
                CheckFile(file, reader, IndexFile.ReadFooterChecksum(reader, cancellationToken));
            }
        }

        storage.SyncDirectory(cancellationToken);

        string temp = CommitFileNames.TempFor(commit.Generation);
        storage.Delete(temp, cancellationToken);
        using (IStorageWriter writer = storage.Create(temp, cancellationToken))
        {
            writer.Write(bytes, cancellationToken);
            writer.Sync(cancellationToken);
        }

        storage.Rename(temp, CommitFileNames.For(commit.Generation), cancellationToken);
        storage.SyncDirectory(cancellationToken);
    }

    /// <summary>The asynchronous twin of <see cref="Commit"/>.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="commit">The commit; its generation must exceed the latest one in storage.</param>
    /// <param name="limits">The limits to enforce; default <see cref="IndexLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>A task that completes when the commit is durable.</returns>
    public static async ValueTask CommitAsync(IStorage storage, CommitInfo commit, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(commit);
        byte[] bytes = CommitFileFormat.Serialize(commit, limits);
        long latest = LatestGeneration(await storage.ListAsync(cancellationToken).ConfigureAwait(false));
        if (commit.Generation <= latest)
        {
            throw new InvalidOperationException($"Commit generation {commit.Generation} is not newer than the latest ({latest}).");
        }

        foreach (SegmentInfo segment in commit.Segments)
        {
            foreach (SegmentFile file in segment.Files)
            {
                using IRangeReader reader = await storage.OpenReadAsync(file.FileName, cancellationToken).ConfigureAwait(false);
                CheckFile(file, reader, await IndexFile.ReadFooterChecksumAsync(reader, cancellationToken).ConfigureAwait(false));
            }
        }

        await storage.SyncDirectoryAsync(cancellationToken).ConfigureAwait(false);

        string temp = CommitFileNames.TempFor(commit.Generation);
        await storage.DeleteAsync(temp, cancellationToken).ConfigureAwait(false);
        IStorageWriter writer = await storage.CreateAsync(temp, cancellationToken).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await writer.SyncAsync(cancellationToken).ConfigureAwait(false);
        }

        await storage.RenameAsync(temp, CommitFileNames.For(commit.Generation), cancellationToken).ConfigureAwait(false);
        await storage.SyncDirectoryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the newest commit in storage and checks that every file it lists is present, complete and the recorded one.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="limits">The limits to enforce; default <see cref="IndexLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The commit, or <see langword="null"/> if the storage holds none (a new index).</returns>
    /// <exception cref="CorruptIndexException">The newest commit file, or a file it lists, is damaged. An older commit is never substituted silently.</exception>
    public static CommitInfo? OpenLatest(IStorage storage, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        IReadOnlyList<string> names = storage.List(cancellationToken);
        if (!TryFindLatest(names, out string? name))
        {
            return null;
        }

        return OpenNamed(storage, name, limits, cancellationToken);
    }

    /// <summary>Opens one specific commit and checks that every file it lists is present, complete and the recorded one.</summary>
    internal static CommitInfo OpenNamed(IStorage storage, string name, IndexLimits? limits, CancellationToken cancellationToken)
    {
        limits ??= IndexLimits.Default;
        CommitInfo commit = ReadCommit(storage, name, limits, cancellationToken);
        foreach (SegmentInfo segment in commit.Segments)
        {
            foreach (SegmentFile file in segment.Files)
            {
                using IRangeReader segmentReader = OpenSegmentFile(storage, name, file, cancellationToken);
                CheckFile(file, segmentReader, IndexFile.ReadFooterChecksum(segmentReader, cancellationToken));
            }
        }

        return commit;
    }

    /// <summary>Parses a commit file without looking at the files it lists.</summary>
    internal static CommitInfo ReadCommit(IStorage storage, string name, IndexLimits limits, CancellationToken cancellationToken)
    {
        using IRangeReader reader = storage.OpenRead(name, cancellationToken);
        if (reader.Length > limits.MaxCommitFileBytes)
        {
            throw new CorruptIndexException(name, $"{reader.Length} bytes exceeds the commit file limit of {limits.MaxCommitFileBytes}");
        }

        var bytes = new byte[(int)reader.Length];
        reader.ReadExactly(0, bytes, cancellationToken);
        CommitInfo commit = CommitFileFormat.Parse(bytes, name, limits);
        CheckGeneration(name, commit);
        return commit;
    }

    /// <summary>The asynchronous twin of <see cref="OpenLatest"/>.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="limits">The limits to enforce; default <see cref="IndexLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The commit, or <see langword="null"/> if the storage holds none.</returns>
    public static async ValueTask<CommitInfo?> OpenLatestAsync(IStorage storage, IndexLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        IReadOnlyList<string> names = await storage.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!TryFindLatest(names, out string? name))
        {
            return null;
        }

        limits ??= IndexLimits.Default;
        using IRangeReader reader = await storage.OpenReadAsync(name, cancellationToken).ConfigureAwait(false);
        if (reader.Length > limits.MaxCommitFileBytes)
        {
            throw new CorruptIndexException(name, $"{reader.Length} bytes exceeds the commit file limit of {limits.MaxCommitFileBytes}");
        }

        var bytes = new byte[(int)reader.Length];
        await reader.ReadExactlyAsync(0, bytes, cancellationToken).ConfigureAwait(false);
        CommitInfo commit = CommitFileFormat.Parse(bytes, name, limits);
        CheckGeneration(name, commit);
        foreach (SegmentInfo segment in commit.Segments)
        {
            foreach (SegmentFile file in segment.Files)
            {
                IRangeReader segmentReader;
                try
                {
                    segmentReader = await storage.OpenReadAsync(file.FileName, cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException e)
                {
                    throw new CorruptIndexException(file.FileName, $"listed by {name} but missing", e);
                }

                using (segmentReader)
                {
                    CheckFile(file, segmentReader, await IndexFile.ReadFooterChecksumAsync(segmentReader, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return commit;
    }

    /// <summary>Like <see cref="OpenLatest"/> but reports damage as a return value instead of throwing.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="limits">The limits to enforce; <see langword="null"/> for the defaults.</param>
    /// <param name="commit">The newest commit when the method returns <see langword="true"/>.</param>
    /// <param name="error">The damage found, or <see langword="null"/> when the method returns <see langword="false"/> because the storage simply holds no commit.</param>
    /// <returns><see langword="true"/> when a valid latest commit was opened.</returns>
    public static bool TryOpenLatest(IStorage storage, IndexLimits? limits, out CommitInfo? commit, out CorruptIndexException? error)
    {
        try
        {
            commit = OpenLatest(storage, limits);
            error = null;
            return commit is not null;
        }
        catch (CorruptIndexException e)
        {
            commit = null;
            error = e;
            return false;
        }
    }

    /// <summary>Reads every byte of every file a commit lists and checks it against the checksum in its footer and in the commit.</summary>
    /// <param name="storage">The index storage.</param>
    /// <param name="commit">The commit to verify.</param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <exception cref="CorruptIndexException">A file is damaged.</exception>
    public static void VerifyFiles(IStorage storage, CommitInfo commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(commit);
        foreach (SegmentInfo segment in commit.Segments)
        {
            foreach (SegmentFile file in segment.Files)
            {
                using IRangeReader reader = OpenSegmentFile(storage, CommitFileNames.For(commit.Generation), file, cancellationToken);
                CheckFile(file, reader, IndexFile.VerifyChecksum(reader, cancellationToken));
            }
        }
    }

    private static IRangeReader OpenSegmentFile(IStorage storage, string commitName, SegmentFile file, CancellationToken cancellationToken)
    {
        try
        {
            return storage.OpenRead(file.FileName, cancellationToken);
        }
        catch (FileNotFoundException e)
        {
            throw new CorruptIndexException(file.FileName, $"listed by {commitName} but missing", e);
        }
    }

    private static void CheckFile(SegmentFile file, IRangeReader reader, uint footerChecksum)
    {
        if (reader.Length != file.Length)
        {
            throw new CorruptIndexException(file.FileName, $"length {reader.Length} differs from the {file.Length} the commit recorded");
        }

        if (footerChecksum != file.Checksum)
        {
            throw new CorruptIndexException(file.FileName, $"checksum {footerChecksum:X8} differs from the {file.Checksum:X8} the commit recorded");
        }
    }

    private static void CheckGeneration(string name, CommitInfo commit)
    {
        if (!CommitFileNames.TryParse(name, out long generation) || generation != commit.Generation)
        {
            throw new CorruptIndexException(name, $"file name and recorded generation {commit.Generation} disagree");
        }
    }

    internal static long LatestGeneration(IReadOnlyList<string> names)
    {
        long latest = 0;
        foreach (string name in names)
        {
            if (CommitFileNames.TryParse(name, out long g) && g > latest)
            {
                latest = g;
            }
        }

        return latest;
    }

    internal static bool TryFindLatest(IReadOnlyList<string> names, [NotNullWhen(true)] out string? latestName)
    {
        long latest = LatestGeneration(names);
        latestName = latest == 0 ? null : CommitFileNames.For(latest);
        return latest != 0;
    }
}
