using System.Buffers.Binary;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Fuzz;

/// <summary>
/// Feeds arbitrary bytes to the commit-file reader and the schema reader (see Run for how the first byte picks the surface). Only <see cref="CorruptIndexException"/> may come out; a
/// commit that parses must serialize back to the same bytes (the format is canonical), and the same for a schema.
/// </summary>
internal static class CommitTarget
{
    private static readonly IndexLimits Limits = new() { MaxCommitFileBytes = 1 << 20, MaxSegments = 1_000, MaxSchemaBytes = 1 << 16 };

    // The first input byte picks the surface so that one corpus serves all of them:
    //   0  the rest is a commit file exactly as given (mostly rejected by the checksum, which is the point)
    //   1  the rest is a commit file whose magic, version-independent footer and checksum are repaired first, so mutations reach the body parser
    //   2  the rest is a serialized schema
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        byte mode = data[0];
        ReadOnlySpan<byte> body = data[1..];
        switch (mode % 3)
        {
            case 0:
                CheckCommit(body);
                break;
            case 1:
                CheckCommit(Repair(body));
                break;
            default:
                CheckSchema(body);
                break;
        }
    }

    private static byte[] Repair(ReadOnlySpan<byte> file)
    {
        byte[] fixedUp = file.ToArray();
        if (fixedUp.Length >= IndexFile.HeaderLength + IndexFile.FooterLength)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fixedUp, CommitFileFormat.Magic);
            Span<byte> footer = fixedUp.AsSpan(fixedUp.Length - IndexFile.FooterLength);
            BinaryPrimitives.WriteUInt32LittleEndian(footer, IndexFile.FooterMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], IndexFile.AlgorithmCrc32C);
            BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], Crc32C.Compute(fixedUp.AsSpan(0, fixedUp.Length - 4)));
        }

        return fixedUp;
    }

    private static void CheckCommit(ReadOnlySpan<byte> data)
    {
        if (!CommitFileFormat.TryParse(data, "fuzz", Limits, out CommitInfo? commit, out _))
        {
            return;
        }

        byte[] again = CommitFileFormat.Serialize(commit, Limits);
        if (!again.AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException("a commit that parsed did not serialize back to the same bytes");
        }

        if (commit.SchemaBytes.Length > 0)
        {
            CheckSchema(commit.SchemaBytes.Span);
        }
    }

    private static void CheckSchema(ReadOnlySpan<byte> data)
    {
        if (Schema.TryDeserialize(data, Limits, out Schema? schema, out _) && !schema.Serialize().AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException("a schema that parsed did not serialize back to the same bytes");
        }
    }

    public static void WriteSeeds(string directory)
    {
        Schema schema = new SchemaBuilder().Id("id").Text("body").Integral("year").Vector("embedding", 4).Build();
        var file = new SegmentFile(SegmentParts.Postings, "s0.postings", new CodecRef(SegmentParts.Postings, 1), 100, 0xDEADBEEF);
        var commit = new CommitInfo(
            3,
            5,
            [new SegmentInfo("s0", 10, 2, 1, [file, new SegmentFile(SegmentParts.Deletes, "s0.del1", new CodecRef(SegmentParts.Deletes, 1), 40, 7)]), new SegmentInfo("s1", 1, 0, 0, [])],
            schema.Serialize(),
            new byte[] { 9, 8, 7 },
            new Dictionary<string, string> { ["a"] = "1", ["b"] = "two" });
        File.WriteAllBytes(Path.Combine(directory, "commit-full"), [1, .. CommitFileFormat.Serialize(commit, Limits)]);
        File.WriteAllBytes(Path.Combine(directory, "commit-empty"), [1, .. CommitFileFormat.Serialize(new CommitInfo(1, 0, []), Limits)]);
        File.WriteAllBytes(Path.Combine(directory, "schema"), [2, .. schema.Serialize()]);
        File.WriteAllBytes(Path.Combine(directory, "commit-raw"), [0, .. CommitFileFormat.Serialize(commit, Limits)]);
    }
}
