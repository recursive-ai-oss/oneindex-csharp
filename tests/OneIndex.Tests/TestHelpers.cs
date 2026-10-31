using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Tests;

internal static class TestHelpers
{
    public static readonly uint TestMagic = IndexFile.Magic("TEST");

    public static string NewTempDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "oneindex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static byte[] ReadAll(IStorage storage, string name)
    {
        using IRangeReader reader = storage.OpenRead(name);
        var bytes = new byte[reader.Length];
        reader.ReadExactly(0, bytes);
        return bytes;
    }

    public static void WriteAll(IStorage storage, string name, ReadOnlySpan<byte> data, bool sync = true)
    {
        using IStorageWriter writer = storage.Create(name);
        writer.Write(data);
        if (sync)
        {
            writer.Sync();
        }
    }

    /// <summary>Writes a small framed segment file the way a codec would: header, body in two pieces, footer, fsync.</summary>
    public static SegmentFile WriteSegmentFile(IStorage storage, string part, string fileName, int seed)
    {
        var body = new byte[200 + seed];
        new Random(seed).NextBytes(body);
        using var writer = new IndexFileWriter(storage.Create(fileName), TestMagic, 1);
        writer.Write(body.AsSpan(0, 100));
        writer.Write(body.AsSpan(100));
        IndexFileSummary summary = writer.Finish();
        return new SegmentFile(part, fileName, new CodecRef(part, 1), summary.Length, summary.Checksum);
    }

    public static CommitInfo MakeCommit(long generation, IReadOnlyList<SegmentInfo> segments)
        => new(generation, (ulong)generation + 10, segments, new SchemaBuilder().Id("id").Text("body").Build().Serialize(), BitConverter.GetBytes(generation * 7), new Dictionary<string, string> { ["gen"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture) });
}
