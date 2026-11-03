using OneIndex.StaticIndexes;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Fuzz;

/// <summary>
/// Arbitrary bytes in place of a static index's manifest (selector 0), an IVF index file (1, the checksum checked or not so that mutations reach the
/// structure parsers) or a packed multi-vector (2, unpacked flat and as rows). Only <see cref="CorruptIndexException"/> (and <see cref="FormatException"/> for the multi-vector) may
/// come out; a crash, a hang or an allocation past the limits is a finding. A file that opens is searched.
/// </summary>
internal static class StaticTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte selector = data[0];
        ReadOnlySpan<byte> body = data[1..];
        try
        {
            switch (selector % 3)
            {
                case 0:
                    _ = StaticManifest.Parse(body);
                    break;
                case 1:
                    var storage = new MemoryStorage();
                    using (IStorageWriter w = storage.Create("x.ivf"))
                    {
                        w.Write(body);
                    }

                    using (IvfIndex index = IvfIndex.Open(storage.OpenRead("x.ivf"), verifyChecksum: (selector & 4) != 0))
                    {
                        var q = new float[index.Dimensions];
                        q[0] = 1;
                        _ = index.Search(q, 5, 3);
                        _ = index.Search(q, 5, index.Lists, static id => (id & 1) == 0);
                    }

                    break;
                default:
                    // Unpack and ToRows share the header check; ToRows also allocates one array per token, so a header that claims
                    // many tokens of no values must be refused before that allocation.
                    _ = MultiVector.Unpack(body, out _, out _);
                    _ = MultiVector.ToRows(body);
                    break;
            }
        }
        catch (Exception e) when (e is CorruptIndexException or FormatException)
        {
            // The typed answer to damaged input.
        }
    }

    public static void WriteSeeds(string directory)
    {
        var rng = new Random(3);
        var data = new float[40 * 4];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(rng.NextDouble() - 0.5);
        }

        File.WriteAllBytes(Path.Combine(directory, "ivf-f32"), [1, .. IvfBuilder.Build(data, 4, new IvfOptions { Lists = 5 })]);
        File.WriteAllBytes(Path.Combine(directory, "ivf-i8"), [1, .. IvfBuilder.Build(data, 4, new IvfOptions { Lists = 5, Storage = IvfStorage.EightBit })]);
        File.WriteAllBytes(Path.Combine(directory, "multivector"), [2, .. MultiVector.Pack([[1f, 2f], [3f, 4f]])]);
        string json = "{\"v\":1,\"chunkBytes\":4096,\"commit\":\"commit-0000000000000001.oic\",\"files\":[{\"name\":\"a.bin\",\"size\":5000,\"crc\":[1,2]},{\"name\":\"b\",\"size\":0,\"crc\":[]}]}";
        File.WriteAllBytes(Path.Combine(directory, "manifest"), [0, .. System.Text.Encoding.UTF8.GetBytes(json)]);
    }
}
