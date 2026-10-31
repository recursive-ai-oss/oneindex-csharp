using System.Diagnostics.CodeAnalysis;
using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// The bytes of a commit file (see docs/commit-format.md). Writing is deterministic: the same <see cref="CommitInfo"/> always gives the
/// same bytes. Reading validates the frame, every length against <see cref="IndexLimits"/> and every name before building anything,
/// and rejects trailing bytes and non-canonical ordering.
/// </summary>
public static class CommitFileFormat
{
    /// <summary>
    /// The newest commit format version this build reads. A commit is written as version 1 unless it carries schema evolution
    /// state (a schema epoch above 0), in which case it is written as version 2; both are read.
    /// </summary>
    public const int FormatVersion = 2;

    /// <summary>The file-type magic of a commit file ("OIXC").</summary>
    public static readonly uint Magic = IndexFile.Magic("OIXC");

    /// <summary>Serializes a commit, frame included.</summary>
    /// <param name="commit">The commit.</param>
    /// <param name="limits">The limits the result must respect so that it can be read back; default <see cref="IndexLimits.Default"/>.</param>
    /// <returns>The complete file contents.</returns>
    /// <exception cref="ArgumentException">The commit exceeds a limit or names an invalid file.</exception>
    public static byte[] Serialize(CommitInfo commit, IndexLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        limits ??= IndexLimits.Default;
        Check(commit.Segments.Count <= limits.MaxSegments, "segment count");
        Check(commit.SchemaBytes.Length <= limits.MaxSchemaBytes, "schema size");
        Check(commit.AppliedPosition.Length <= limits.MaxAppliedPositionBytes, "applied position size");
        Check(commit.UserData.Count <= limits.MaxUserDataEntries, "user data entry count");

        var sink = new ByteSink();
        bool evolved = commit.SchemaEpoch != 0 || commit.Segments.Any(static s => s.SchemaEpoch != 0);
        sink.WriteUInt32(Magic);
        sink.WriteUInt32(evolved ? 2u : 1u);
        sink.WriteUInt64((ulong)commit.Generation);
        sink.WriteUInt64(commit.NextSegmentId);
        sink.WriteBytes(commit.AppliedPosition.Span);
        sink.WriteBytes(commit.SchemaBytes.Span);
        sink.WriteVarUInt((ulong)commit.UserData.Count);
        foreach (KeyValuePair<string, string> pair in commit.UserData)
        {
            sink.WriteString(pair.Key);
            sink.WriteString(pair.Value);
        }

        if (evolved)
        {
            sink.WriteVarUInt((ulong)commit.SchemaEpoch);
        }

        sink.WriteVarUInt((ulong)commit.Segments.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SegmentInfo segment in commit.Segments)
        {
            Check(names.Add(segment.Name), "segment names being unique");
            Check(segment.Files.Count <= limits.MaxFilesPerSegment, "files per segment");
            Check(segment.DocCount <= limits.MaxDocsPerSegment, "documents per segment");
            sink.WriteString(segment.Name);
            sink.WriteVarUInt((ulong)segment.DocCount);
            sink.WriteVarUInt((ulong)segment.DeletedCount);
            sink.WriteVarUInt((ulong)segment.DeleteGeneration);
            sink.WriteVarUInt((ulong)segment.Files.Count);
            foreach (SegmentFile file in segment.Files)
            {
                StorageNames.Validate(file.FileName);
                Check(names.Add(file.FileName), "file names being unique");
                CodecRegistry.EnsureSupported(file.FileName, file.Codec);
                sink.WriteString(file.Part);
                sink.WriteString(file.FileName);
                sink.WriteString(file.Codec.Name);
                sink.WriteVarUInt((ulong)file.Codec.Version);
                sink.WriteVarUInt((ulong)file.Length);
                sink.WriteUInt32(file.Checksum);
            }

            if (evolved)
            {
                sink.WriteVarUInt((ulong)segment.SchemaEpoch);
            }
        }

        // The footer: magic, algorithm, and the CRC-32C of everything before it.
        sink.WriteUInt32(IndexFile.FooterMagic);
        sink.WriteUInt32(IndexFile.AlgorithmCrc32C);
        sink.WriteUInt32(Crc32C.Compute(sink.WrittenSpan));
        return sink.ToArray();

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new ArgumentException($"The commit's {what} exceeds the limits.");
            }
        }
    }

    /// <summary>Parses a commit file held in memory.</summary>
    /// <param name="file">The complete file contents.</param>
    /// <param name="fileName">The file name, for exceptions.</param>
    /// <param name="limits">The limits to enforce; default <see cref="IndexLimits.Default"/>.</param>
    /// <returns>The commit.</returns>
    /// <exception cref="CorruptIndexException">The bytes are not a valid commit file.</exception>
    /// <exception cref="UnsupportedFormatException">The file is intact but newer than this build understands.</exception>
    public static CommitInfo Parse(ReadOnlySpan<byte> file, string fileName, IndexLimits? limits = null)
    {
        limits ??= IndexLimits.Default;
        if (file.Length > limits.MaxCommitFileBytes)
        {
            throw new CorruptIndexException(fileName, $"{file.Length} bytes exceeds the commit file limit of {limits.MaxCommitFileBytes}");
        }

        int version = IndexFile.Validate(fileName, file, Magic, FormatVersion);
        try
        {
            return ParseBody(file[IndexFile.HeaderLength..^IndexFile.FooterLength], version, fileName, limits);
        }
        catch (MalformedInputException e)
        {
            throw new CorruptIndexException(fileName, e.Message, e);
        }
        catch (ArgumentException e)
        {
            // A model constructor refused a value the reader's own checks let through.
            throw new CorruptIndexException(fileName, e.Message, e);
        }
    }

    /// <summary>Parses a commit file without throwing for malformed input.</summary>
    /// <param name="file">The complete file contents.</param>
    /// <param name="fileName">The file name, for the error text.</param>
    /// <param name="limits">The limits to enforce; <see langword="null"/> for the defaults.</param>
    /// <param name="commit">The commit when the method returns <see langword="true"/>.</param>
    /// <param name="error">The reason when it returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the bytes are a valid, supported commit file.</returns>
    public static bool TryParse(ReadOnlySpan<byte> file, string fileName, IndexLimits? limits, [NotNullWhen(true)] out CommitInfo? commit, [NotNullWhen(false)] out string? error)
    {
        try
        {
            commit = Parse(file, fileName, limits);
            error = null;
            return true;
        }
        catch (CorruptIndexException e)
        {
            commit = null;
            error = e.Message;
            return false;
        }
    }

    private static CommitInfo ParseBody(ReadOnlySpan<byte> body, int version, string fileName, IndexLimits limits)
    {
        bool evolved = version >= 2;
        var src = new ByteSource(body);
        ulong generation = src.ReadUInt64();
        if (generation is 0 or > long.MaxValue)
        {
            throw new MalformedInputException("generation is out of range");
        }

        ulong nextSegmentId = src.ReadUInt64();
        byte[] applied = src.ReadBytes(limits.MaxAppliedPositionBytes, "applied position").ToArray();
        byte[] schema = src.ReadBytes(limits.MaxSchemaBytes, "schema").ToArray();

        int userCount = src.ReadCount(limits.MaxUserDataEntries, "user data entry count");
        var userData = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? previousKey = null;
        for (int i = 0; i < userCount; i++)
        {
            string key = src.ReadString(limits.MaxStringBytes, "user data key");
            if (previousKey is not null && string.CompareOrdinal(previousKey, key) >= 0)
            {
                throw new MalformedInputException("user data keys are not in strictly ascending order");
            }

            userData.Add(key, src.ReadString(limits.MaxStringBytes, "user data value"));
            previousKey = key;
        }

        ulong commitEpoch = 0;
        if (evolved)
        {
            commitEpoch = src.ReadVarUInt();
            if (commitEpoch > long.MaxValue)
            {
                throw new MalformedInputException("schema epoch is out of range");
            }
        }

        int segmentCount = src.ReadCount(limits.MaxSegments, "segment count");
        bool anyEpoch = commitEpoch != 0;
        var segments = new List<SegmentInfo>(Math.Min(segmentCount, 1024));
        var seenSegments = new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        for (int s = 0; s < segmentCount; s++)
        {
            string name = src.ReadString(limits.MaxFileNameLength, "segment name");
            RequireName(name, "segment name");
            if (!seenSegments.Add(name))
            {
                throw new MalformedInputException($"segment '{name}' is listed twice");
            }

            int docCount = src.ReadCount(limits.MaxDocsPerSegment, "document count");
            int deleted = src.ReadCount(docCount, "deleted count");
            ulong deleteGeneration = src.ReadVarUInt();
            if (deleteGeneration > long.MaxValue)
            {
                throw new MalformedInputException("delete generation is out of range");
            }

            int fileCount = src.ReadCount(limits.MaxFilesPerSegment, "file count");
            var files = new List<SegmentFile>(Math.Min(fileCount, 64));
            for (int f = 0; f < fileCount; f++)
            {
                string part = src.ReadString(limits.MaxStringBytes, "part name");
                string fileNameInCommit = src.ReadString(limits.MaxFileNameLength, "file name");
                RequireName(fileNameInCommit, "file name");
                if (!seenFiles.Add(fileNameInCommit))
                {
                    throw new MalformedInputException($"file '{fileNameInCommit}' is listed twice");
                }

                string codecName = src.ReadString(limits.MaxStringBytes, "codec name");
                ulong codecVersion = src.ReadVarUInt();
                ulong length = src.ReadVarUInt();
                uint checksum = src.ReadUInt32();
                if (codecVersion > int.MaxValue || length > long.MaxValue)
                {
                    throw new MalformedInputException("codec version or file length is out of range");
                }

                var codec = new CodecRef(codecName, (int)codecVersion);
                if (!CodecRegistry.IsSupported(codec))
                {
                    throw new UnsupportedFormatException(fileName, $"file '{fileNameInCommit}' uses codec '{codecName}' version {codecVersion}, which this build does not read");
                }

                files.Add(new SegmentFile(part, fileNameInCommit, codec, (long)length, checksum));
            }

            ulong segmentEpoch = 0;
            if (evolved)
            {
                segmentEpoch = src.ReadVarUInt();
                if (segmentEpoch > commitEpoch)
                {
                    throw new MalformedInputException($"segment '{name}' is newer than the commit's schema epoch");
                }

                anyEpoch |= segmentEpoch != 0;
            }

            segments.Add(new SegmentInfo(name, docCount, deleted, (long)deleteGeneration, files, (long)segmentEpoch));
        }

        if (src.Remaining != 0)
        {
            throw new MalformedInputException($"{src.Remaining} unexpected bytes follow the last segment");
        }

        if (evolved && !anyEpoch)
        {
            // Version 2 exists to carry epochs; without any, the canonical form is version 1.
            throw new MalformedInputException("a version 2 commit carries no schema epoch");
        }

        return new CommitInfo((long)generation, nextSegmentId, segments, schema, applied, userData, (long)commitEpoch);

        static void RequireName(string name, string what)
        {
            if (!StorageNames.IsValid(name))
            {
                throw new MalformedInputException($"{what} '{name}' is not a valid name");
            }
        }
    }
}
