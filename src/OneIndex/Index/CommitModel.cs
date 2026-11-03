using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>One file of a segment, as a commit records it.</summary>
/// <param name="Part">The part it implements (see <see cref="SegmentParts"/>).</param>
/// <param name="FileName">The storage file name.</param>
/// <param name="Codec">The codec and version it was written with.</param>
/// <param name="Length">Its length in bytes, footer included.</param>
/// <param name="Checksum">The CRC-32C in its footer, which identifies the exact bytes that were committed.</param>
/// <param name="Offset">0 when the file holds the part alone. Otherwise <paramref name="FileName"/> is a pack file holding several
/// parts, and the part's own framed bytes (header to footer, <paramref name="Length"/> of them) start at this offset in it.</param>
public sealed record SegmentFile(string Part, string FileName, CodecRef Codec, long Length, uint Checksum, long Offset = 0);

/// <summary>An immutable segment: a name, its document counts, the delete generation and the files that make it up.</summary>
public sealed class SegmentInfo
{
    /// <summary>Creates segment metadata.</summary>
    /// <param name="name">The segment name, a valid storage name.</param>
    /// <param name="docCount">Documents written to the segment, deleted ones included.</param>
    /// <param name="deletedCount">Documents currently deleted; at most <paramref name="docCount"/>.</param>
    /// <param name="deleteGeneration">The generation of the delete bitmap file, 0 when the segment has none.</param>
    /// <param name="files">The segment's files, one per part (the delete bitmap included when the generation is not 0).</param>
    /// <param name="schemaEpoch">The schema epoch the segment was written under (see <see cref="CommitInfo.SchemaEpoch"/>); 0 for an index whose schema never needed a rebuild.</param>
    public SegmentInfo(string name, int docCount, int deletedCount, long deleteGeneration, IReadOnlyList<SegmentFile> files, long schemaEpoch = 0)
    {
        StorageNames.Validate(name);
        ArgumentOutOfRangeException.ThrowIfNegative(docCount);
        ArgumentOutOfRangeException.ThrowIfNegative(deletedCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deletedCount, docCount);
        ArgumentOutOfRangeException.ThrowIfNegative(deleteGeneration);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfNegative(schemaEpoch);
        SchemaEpoch = schemaEpoch;
        Name = name;
        DocCount = docCount;
        DeletedCount = deletedCount;
        DeleteGeneration = deleteGeneration;
        Files = [.. files];
    }

    /// <summary>The segment name.</summary>
    public string Name { get; }

    /// <summary>Documents written to the segment, deleted ones included.</summary>
    public int DocCount { get; }

    /// <summary>Documents currently deleted.</summary>
    public int DeletedCount { get; }

    /// <summary>Documents that can be returned: <see cref="DocCount"/> minus <see cref="DeletedCount"/>.</summary>
    public int LiveCount => DocCount - DeletedCount;

    /// <summary>The generation of the delete bitmap file; 0 when none.</summary>
    public long DeleteGeneration { get; }

    /// <summary>The schema epoch the segment was written under. A segment older than the commit's epoch still needs its rebuild (see docs/schema-evolution.md).</summary>
    public long SchemaEpoch { get; }

    /// <summary>The files of the segment in a fixed order.</summary>
    public IReadOnlyList<SegmentFile> Files { get; }

    /// <summary>The names of the storage files the segment occupies, each once, in listing order. Parts packed together share one name.</summary>
    public IReadOnlyList<string> FileNames
    {
        get
        {
            var names = new List<string>(Files.Count);
            foreach (SegmentFile file in Files)
            {
                if (!names.Contains(file.FileName))
                {
                    names.Add(file.FileName);
                }
            }

            return names;
        }
    }

    /// <summary>Finds the file of a part.</summary>
    /// <param name="part">The part name.</param>
    /// <returns>The file, or <see langword="null"/> if the segment has no such part.</returns>
    public SegmentFile? FindPart(string part)
    {
        foreach (SegmentFile file in Files)
        {
            if (string.Equals(file.Part, part, StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
    }
}

/// <summary>
/// Everything a commit says: the generation, the segments, the schema, the applied position of an external change log, user data.
/// A commit is immutable. Storage holds it as one checksummed file written by <see cref="CommitProtocol"/>.
/// </summary>
public sealed class CommitInfo
{
    /// <summary>Creates a commit description.</summary>
    /// <param name="generation">The commit's number, at least 1 and larger than every earlier commit's.</param>
    /// <param name="nextSegmentId">The number the writer will use for the next segment name.</param>
    /// <param name="segments">The live segments in a fixed order.</param>
    /// <param name="schema">The serialized schema (see <see cref="Schema.Serialize"/>); empty when none.</param>
    /// <param name="appliedPosition">An opaque value the application stores with the commit, such as the position reached in an external change log; empty when none.</param>
    /// <param name="userData">Application strings stored with the commit; written in key order.</param>
    /// <param name="schemaEpoch">The schema epoch: 0 until a schema change needs existing segments rebuilt, then raised by one for each such change.</param>
    /// <param name="pending">A schema change whose rebuilt segments are written but not yet served; <see langword="null"/> when none.</param>
    public CommitInfo(
        long generation,
        ulong nextSegmentId,
        IReadOnlyList<SegmentInfo> segments,
        ReadOnlyMemory<byte> schema = default,
        ReadOnlyMemory<byte> appliedPosition = default,
        IReadOnlyDictionary<string, string>? userData = null,
        long schemaEpoch = 0,
        PendingEvolution? pending = null)
        : this(generation, nextSegmentId, segments, schema, appliedPosition, userData, schemaEpoch, pending, minimumGeneration: 1)
    {
    }

    private CommitInfo(
        long generation,
        ulong nextSegmentId,
        IReadOnlyList<SegmentInfo> segments,
        ReadOnlyMemory<byte> schema,
        ReadOnlyMemory<byte> appliedPosition,
        IReadOnlyDictionary<string, string>? userData,
        long schemaEpoch,
        PendingEvolution? pending,
        long minimumGeneration)
    {
        Pending = pending;
        ArgumentOutOfRangeException.ThrowIfNegative(schemaEpoch);
        SchemaEpoch = schemaEpoch;
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, minimumGeneration);
        ArgumentNullException.ThrowIfNull(segments);
        Generation = generation;
        NextSegmentId = nextSegmentId;
        Segments = [.. segments];
        SchemaBytes = schema.ToArray();
        AppliedPosition = appliedPosition.ToArray();
        UserData = userData is null
            ? new SortedDictionary<string, string>(StringComparer.Ordinal)
            : new SortedDictionary<string, string>(userData.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
    }

    /// <summary>The commit's number.</summary>
    public long Generation { get; }

    /// <summary>The schema epoch. Segments written under an older epoch are rebuilt in the background (docs/schema-evolution.md).</summary>
    public long SchemaEpoch { get; }

    /// <summary>The rebuild in progress after a schema change, or <see langword="null"/>. Its segments are not part of <see cref="Segments"/> and are never read until a later commit swaps them in (docs/schema-evolution.md).</summary>
    public PendingEvolution? Pending { get; }

    /// <summary>The number the writer will use for the next segment name.</summary>
    public ulong NextSegmentId { get; }

    /// <summary>The live segments.</summary>
    public IReadOnlyList<SegmentInfo> Segments { get; }

    /// <summary>The serialized schema; empty when none.</summary>
    public ReadOnlyMemory<byte> SchemaBytes { get; }

    /// <summary>The opaque applied position; empty when none.</summary>
    public ReadOnlyMemory<byte> AppliedPosition { get; }

    /// <summary>
    /// The description of a near-real-time view: the segments as the writer holds them, extending the commit of the given
    /// generation (0 before the first commit). Never written to storage.
    /// </summary>
    internal static CommitInfo NearRealTimeView(long extendedGeneration, ulong nextSegmentId, IReadOnlyList<SegmentInfo> segments, ReadOnlyMemory<byte> schema, ReadOnlyMemory<byte> appliedPosition, IReadOnlyDictionary<string, string> userData, long schemaEpoch = 0)
        => new(extendedGeneration, nextSegmentId, segments, schema, appliedPosition, userData, schemaEpoch, null, minimumGeneration: 0);

    /// <summary>The application strings stored with the commit, in key order.</summary>
    public IReadOnlyDictionary<string, string> UserData { get; }

    /// <summary>Documents that can be returned across all segments.</summary>
    public long LiveDocCount => Segments.Sum(s => (long)s.LiveCount);
}

/// <summary>A segment rebuilt for a pending schema, written ahead of the swap.</summary>
/// <param name="Info">The rebuilt segment; its epoch is the commit's plus one.</param>
/// <param name="Source">The name of the served segment it was rebuilt from.</param>
/// <param name="SourceDeleted">How many documents of the source were deleted when it was rebuilt; the rebuilt segment is only good while the source still has exactly this many.</param>
public sealed record RebuiltSegment(SegmentInfo Info, string Source, int SourceDeleted);

/// <summary>A schema change the index is rebuilding for: the schema it will serve after the swap and the segments rebuilt so far.</summary>
public sealed class PendingEvolution
{
    /// <summary>Creates the description.</summary>
    /// <param name="schemaBytes">The serialized schema served after the swap.</param>
    /// <param name="rebuilt">The segments rebuilt so far, in a fixed order.</param>
    public PendingEvolution(ReadOnlyMemory<byte> schemaBytes, IReadOnlyList<RebuiltSegment> rebuilt)
    {
        ArgumentNullException.ThrowIfNull(rebuilt);
        SchemaBytes = schemaBytes.ToArray();
        Rebuilt = [.. rebuilt];
    }

    /// <summary>The serialized schema served after the swap.</summary>
    public ReadOnlyMemory<byte> SchemaBytes { get; }

    /// <summary>The rebuilt segments written so far.</summary>
    public IReadOnlyList<RebuiltSegment> Rebuilt { get; }
}

/// <summary>Names of commit files: <c>commit-</c>, the generation as 16 lowercase hex digits, then <c>.oic</c> (or <c>.tmp</c> while being written).</summary>
public static class CommitFileNames
{
    private const string Prefix = "commit-";
    private const string Extension = ".oic";
    private const string TempExtension = ".tmp";

    /// <summary>The published name of a generation's commit file.</summary>
    /// <param name="generation">The generation.</param>
    /// <returns>The file name.</returns>
    public static string For(long generation) => $"{Prefix}{generation:x16}{Extension}";

    /// <summary>The name a commit file has while it is written, before the atomic rename.</summary>
    /// <param name="generation">The generation.</param>
    /// <returns>The temporary file name.</returns>
    public static string TempFor(long generation) => $"{Prefix}{generation:x16}{TempExtension}";

    /// <summary>Recognizes a published commit file name.</summary>
    /// <param name="name">A storage file name.</param>
    /// <param name="generation">The generation in the name.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is exactly a published commit file name.</returns>
    public static bool TryParse(string name, out long generation)
    {
        generation = 0;
        if (name.Length != Prefix.Length + 16 + Extension.Length
            || !name.StartsWith(Prefix, StringComparison.Ordinal)
            || !name.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> hex = name.AsSpan(Prefix.Length, 16);
        foreach (char c in hex)
        {
            if (!(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return long.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out generation) && generation >= 1;
    }
}
