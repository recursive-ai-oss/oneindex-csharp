namespace OneIndex.Codecs;

/// <summary>
/// The key of a geo point in the point index: latitude and longitude each quantized to 31 bits and their bits interleaved
/// (Morton or Z-order, Morton 1966), so points that are near in the plane are mostly near in the key and a run of 512 keys covers a small
/// box. Latitude takes the even bits, longitude the odd ones. Quantization steps are 180/(2^31-1) and 360/(2^31-1) degrees, about 8 and 17 mm,
/// so the key is only a filter: a query confirms each candidate against the exact coordinates in the column.
/// </summary>
internal static class GeoKey
{
    public const int Bits = 31;
    public const long MaxQuantum = (1L << Bits) - 1;

    /// <summary>The leaf box is kept at 16 bits per side: the top 16 of the 31 bits.</summary>
    public const int CellShift = 15;

    public static long QuantizeLatitude(double latitude) => Quantize((latitude + 90.0) / 180.0);

    public static long QuantizeLongitude(double longitude) => Quantize((longitude + 180.0) / 360.0);

    // Rounds to the nearest step; monotonic in the input, so a range of coordinates maps to a range of keys.
    private static long Quantize(double unit)
    {
        double q = Math.Round(unit * MaxQuantum);
        return q <= 0 ? 0 : q >= MaxQuantum ? MaxQuantum : (long)q;
    }

    public static long Interleave(long latitude, long longitude) => (long)(Spread((ulong)latitude) | (Spread((ulong)longitude) << 1));

    public static void Deinterleave(long key, out long latitude, out long longitude)
    {
        latitude = (long)Compact((ulong)key);
        longitude = (long)Compact((ulong)key >> 1);
    }

    // Spreads the low 32 bits so that bit i moves to bit 2i (Stocco and Schrack's magic-number bit dilation).
    private static ulong Spread(ulong x)
    {
        x &= 0xFFFFFFFFUL;
        x = (x | (x << 16)) & 0x0000FFFF0000FFFFUL;
        x = (x | (x << 8)) & 0x00FF00FF00FF00FFUL;
        x = (x | (x << 4)) & 0x0F0F0F0F0F0F0F0FUL;
        x = (x | (x << 2)) & 0x3333333333333333UL;
        x = (x | (x << 1)) & 0x5555555555555555UL;
        return x;
    }

    private static ulong Compact(ulong x)
    {
        x &= 0x5555555555555555UL;
        x = (x | (x >> 1)) & 0x3333333333333333UL;
        x = (x | (x >> 2)) & 0x0F0F0F0F0F0F0F0FUL;
        x = (x | (x >> 4)) & 0x00FF00FF00FF00FFUL;
        x = (x | (x >> 8)) & 0x0000FFFF0000FFFFUL;
        x = (x | (x >> 16)) & 0x00000000FFFFFFFFUL;
        return x;
    }
}

/// <summary>A rectangle of quantized coordinates, edges included (see <see cref="GeoKey"/>).</summary>
/// <param name="LatLo">Lowest latitude quantum.</param>
/// <param name="LatHi">Highest latitude quantum.</param>
/// <param name="LngLo">Lowest longitude quantum.</param>
/// <param name="LngHi">Highest longitude quantum.</param>
internal readonly record struct GeoBox(long LatLo, long LatHi, long LngLo, long LngHi)
{
    public bool Contains(long lat, long lng) => lat >= LatLo && lat <= LatHi && lng >= LngLo && lng <= LngHi;

    /// <summary>Builds the boxes (one, or two when it crosses the antimeridian) that cover a degree range, widened by one quantum so rounding never loses a point.</summary>
    public static void FromDegrees(double south, double north, double west, double east, List<GeoBox> into)
    {
        long latLo = Math.Max(0, GeoKey.QuantizeLatitude(south) - 1);
        long latHi = Math.Min(GeoKey.MaxQuantum, GeoKey.QuantizeLatitude(north) + 1);
        if (west <= east)
        {
            into.Add(new GeoBox(latLo, latHi, Math.Max(0, GeoKey.QuantizeLongitude(west) - 1), Math.Min(GeoKey.MaxQuantum, GeoKey.QuantizeLongitude(east) + 1)));
            return;
        }

        into.Add(new GeoBox(latLo, latHi, Math.Max(0, GeoKey.QuantizeLongitude(west) - 1), GeoKey.MaxQuantum));
        into.Add(new GeoBox(latLo, latHi, 0, Math.Min(GeoKey.MaxQuantum, GeoKey.QuantizeLongitude(east) + 1)));
    }
}
