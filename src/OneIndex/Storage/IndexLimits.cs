namespace OneIndex.Storage;

/// <summary>
/// Ceilings that every reader enforces before it trusts a length, count or depth read from a file or an input. One instance is
/// passed down through the readers; <see cref="Default"/> is on unless a caller supplies another. A value read from disk that
/// exceeds a ceiling raises <see cref="CorruptIndexException"/> instead of allocating.
/// </summary>
public sealed class IndexLimits
{
    /// <summary>The limits used when none are given.</summary>
    public static IndexLimits Default { get; } = new();

    /// <summary>The longest file name accepted by storage, in characters. Default 255.</summary>
    public int MaxFileNameLength { get; init; } = 255;

    /// <summary>The largest commit file read, in bytes. Default 64 MiB.</summary>
    public int MaxCommitFileBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>The most segments one commit may list. Default 100,000.</summary>
    public int MaxSegments { get; init; } = 100_000;

    /// <summary>The most files one segment may list. Default 1,024.</summary>
    public int MaxFilesPerSegment { get; init; } = 1_024;

    /// <summary>The most documents one segment may hold. Default 2,000,000,000.</summary>
    public int MaxDocsPerSegment { get; init; } = 2_000_000_000;

    /// <summary>The longest name or text value read from an index file, in UTF-8 bytes. Default 4,096.</summary>
    public int MaxStringBytes { get; init; } = 4_096;

    /// <summary>The most user-data entries a commit may carry. Default 1,024.</summary>
    public int MaxUserDataEntries { get; init; } = 1_024;

    /// <summary>The largest serialized schema, in bytes. Default 4 MiB.</summary>
    public int MaxSchemaBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>The most fields a schema may declare. Default 4,096.</summary>
    public int MaxFields { get; init; } = 4_096;

    /// <summary>The largest applied-position value in a commit, in bytes. Default 1,024.</summary>
    public int MaxAppliedPositionBytes { get; init; } = 1_024;

    /// <summary>The largest vector dimension a schema may declare. Default 65,536.</summary>
    public int MaxVectorDimensions { get; init; } = 65_536;
}
