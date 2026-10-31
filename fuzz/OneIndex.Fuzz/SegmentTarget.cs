using System.Buffers.Binary;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.Fuzz;

/// <summary>
/// Feeds arbitrary bytes to the readers of the files the index writes itself: the delete file and the identifier map. Only
/// <see cref="CorruptIndexException"/> may come out. A delete file that parses must agree with itself (its deleted count equals the
/// number of ids it reports deleted); an identifier map that validates must answer a lookup for every identifier it holds.
/// </summary>
internal static class SegmentTarget
{
    private static readonly IndexLimits Limits = new() { MaxDocsPerSegment = 1 << 16, MaxStringBytes = 1_024 };

    // The first input byte picks the surface:
    //   0  a delete file as given            1  a delete file with its frame repaired, so mutations reach the body parser
    //   2  an identifier map as given        3  an identifier map with its frame repaired, then looked up through the reader
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        byte mode = (byte)(data[0] % 4);
        int maxDoc = 1 + (data[1] * 257 % 4000);
        ReadOnlySpan<byte> body = data[2..];
        switch (mode)
        {
            case 0:
                CheckDeletes(body, maxDoc);
                break;
            case 1:
                CheckDeletes(Repair(body, DeleteFileFormat.Magic, DeleteFileFormat.FormatVersion), maxDoc);
                break;
            case 2:
                CheckIds(body, maxDoc);
                break;
            default:
                CheckIds(Repair(body, IdsFormat.Magic, IdsFormat.FormatVersion), maxDoc);
                break;
        }
    }

    private static byte[] Repair(ReadOnlySpan<byte> file, uint magic, int version)
    {
        byte[] fixedUp = file.ToArray();
        if (fixedUp.Length >= IndexFile.HeaderLength + IndexFile.FooterLength)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fixedUp, magic);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedUp.AsSpan(4), (uint)version);
            Span<byte> footer = fixedUp.AsSpan(fixedUp.Length - IndexFile.FooterLength);
            BinaryPrimitives.WriteUInt32LittleEndian(footer, IndexFile.FooterMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], IndexFile.AlgorithmCrc32C);
            BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], Crc32C.Compute(fixedUp.AsSpan(0, fixedUp.Length - 4)));
        }

        return fixedUp;
    }

    private static void CheckDeletes(ReadOnlySpan<byte> file, int maxDoc)
    {
        // Ask for the file's own sizes first (-1), then for a possibly different expectation: both must stay typed.
        if (DeleteFileFormat.TryParse("fuzz", file, -1, -1, Limits, out LiveDocs? live, out _))
        {
            int counted = 0;
            for (int d = 0; d < live!.MaxDoc; d++)
            {
                if (live.IsDeleted(d))
                {
                    counted++;
                }
            }

            if (counted != live.DeletedCount)
            {
                throw new InvalidOperationException("a delete file that parsed reports a different deleted count than it holds");
            }

            ulong[] words = live.ToWords();
            if (!DeleteFileFormat.TryParse("fuzz", BuildFile(DeleteFileFormat.SerializeBody(live.MaxDoc, words), DeleteFileFormat.Magic, DeleteFileFormat.FormatVersion), live.MaxDoc, live.DeletedCount, Limits, out _, out _))
            {
                throw new InvalidOperationException("the writer's output for a parsed delete set was rejected");
            }
        }

        _ = DeleteFileFormat.TryParse("fuzz", file, maxDoc, 0, Limits, out _, out _);
    }

    private static void CheckIds(ReadOnlySpan<byte> file, int maxDoc)
    {
        if (!IdsFormat.TryValidate("fuzz", file, maxDoc, Limits, out _))
        {
            // A reader opened over an unvalidated file may complain only with the typed exception.
            Probe(file, maxDoc);
            return;
        }

        using IdsReader reader = Open(file, maxDoc);
        foreach (KeyValuePair<byte[], int> e in reader.ReadAll())
        {
            if (!reader.TryFind(e.Key, out int doc) || doc != e.Value)
            {
                throw new InvalidOperationException("an identifier map that validated cannot find one of its own identifiers");
            }
        }
    }

    private static void Probe(ReadOnlySpan<byte> file, int maxDoc)
    {
        try
        {
            using IdsReader reader = Open(file, maxDoc);
            _ = reader.TryFind("probe"u8, out _);
            _ = reader.TryFind([], out _);
        }
        catch (CorruptIndexException)
        {
        }
    }

    private static IdsReader Open(ReadOnlySpan<byte> file, int maxDoc)
    {
        var storage = new MemoryStorage();
        using (IStorageWriter w = storage.Create("ids"))
        {
            w.Write(file);
        }

        return new IdsReader(storage.OpenRead("ids"), maxDoc, Limits);
    }

    private static byte[] BuildFile(byte[] body, uint magic, int version)
    {
        var file = new byte[IndexFile.HeaderLength + body.Length + IndexFile.FooterLength];
        body.CopyTo(file.AsSpan(IndexFile.HeaderLength));
        return Repair(file, magic, version);
    }

    public static void WriteSeeds(string directory)
    {
        var words = new ulong[3];
        foreach (int d in new[] { 1, 5, 70, 129 })
        {
            words[d >> 6] |= 1UL << (d & 63);
        }

        File.WriteAllBytes(Path.Combine(directory, "deletes-sparse"), [1, 1, .. BuildFile(DeleteFileFormat.SerializeBody(130, words), DeleteFileFormat.Magic, DeleteFileFormat.FormatVersion)]);
        var dense = new ulong[2] { 0xAAAA_AAAA_AAAA_AAAA, 0x5555_5555_5555_5555 };
        File.WriteAllBytes(Path.Combine(directory, "deletes-dense"), [1, 1, .. BuildFile(DeleteFileFormat.SerializeBody(128, dense), DeleteFileFormat.Magic, DeleteFileFormat.FormatVersion)]);

        var storage = new MemoryStorage();
        var sink = new SegmentFileSink(storage, "s0000000000000001");
        var entries = new List<KeyValuePair<byte[], int>>();
        string[] ids = ["alpha", "beta", "gamma", "delta", "ünï"];
        for (int i = 0; i < ids.Length; i++)
        {
            entries.Add(new KeyValuePair<byte[], int>(System.Text.Encoding.UTF8.GetBytes(ids[i]), i));
        }

        IdsFormat.Write(sink, entries);
        using IRangeReader r = storage.OpenRead("s0000000000000001.ids");
        var bytes = new byte[r.Length];
        r.ReadExactly(0, bytes);
        File.WriteAllBytes(Path.Combine(directory, "ids"), [3, 1, .. bytes]);
        File.WriteAllBytes(Path.Combine(directory, "ids-raw"), [2, 1, .. bytes]);
    }
}
