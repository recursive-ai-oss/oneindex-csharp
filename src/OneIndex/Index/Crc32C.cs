using System.Buffers.Binary;
using System.Numerics;

namespace OneIndex.Index;

/// <summary>
/// CRC-32C (Castagnoli, polynomial 0x1EDC6F41, reflected), the checksum of every index file. Uses the CPU's CRC instruction where
/// there is one and a portable fallback otherwise; both give identical values. The value of "123456789" is 0xE3069283.
/// </summary>
public static class Crc32C
{
    /// <summary>Computes the checksum of <paramref name="data"/>.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The CRC-32C.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>Continues a checksum: <c>Update(Compute(a), b)</c> equals <c>Compute(a + b)</c>.</summary>
    /// <param name="checksum">The checksum of the bytes so far (0 for none).</param>
    /// <param name="data">The next bytes.</param>
    /// <returns>The checksum of all bytes so far.</returns>
    public static uint Update(uint checksum, ReadOnlySpan<byte> data)
    {
        uint crc = ~checksum;
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }

        return ~crc;
    }
}
