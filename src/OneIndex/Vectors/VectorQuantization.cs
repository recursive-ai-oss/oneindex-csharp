namespace OneIndex.Vectors;

/// <summary>
/// The compressed copy of a vector field that graph search runs on. The segment always keeps the full-precision vectors too: the
/// compressed distances choose candidates and the full-precision distances order them, so the result of a query is the best
/// of the candidates by true distance, never an estimate.
/// </summary>
public enum VectorQuantization : byte
{
    /// <summary>No compressed copy: the graph is searched on the full-precision vectors.</summary>
    None = 0,

    /// <summary>One signed byte per dimension (4 times smaller), with a per-vector interval chosen by optimized scalar quantization.</summary>
    Scalar8 = 1,

    /// <summary>Four bits per dimension (8 times smaller), with a per-vector interval chosen by optimized scalar quantization.</summary>
    Scalar4 = 2,

    /// <summary>
    /// One bit per dimension (32 times smaller): the sign of each component, compared by Hamming distance. It needs the cosine or
    /// dot-product metric and a larger oversample than the other codecs, which is why its default is larger.
    /// </summary>
    OneBit = 3,
}

/// <summary>Facts about the quantization codecs.</summary>
public static class VectorQuantizer
{
    /// <summary>
    /// How many candidates, as a multiple of the number of results asked for, a quantized search keeps before the full-precision
    /// vectors rerank them: 2 for <see cref="VectorQuantization.Scalar8"/>, 3 for <see cref="VectorQuantization.Scalar4"/>, 5 for
    /// <see cref="VectorQuantization.OneBit"/>; 1 for <see cref="VectorQuantization.None"/>.
    /// </summary>
    /// <param name="quantization">The codec.</param>
    /// <returns>The default oversample.</returns>
    public static float DefaultOversample(VectorQuantization quantization) => quantization switch
    {
        VectorQuantization.Scalar8 => 2f,
        VectorQuantization.Scalar4 => 3f,
        VectorQuantization.OneBit => 5f,
        _ => 1f,
    };

    /// <summary>The bytes one compressed vector takes in the segment, including its correction factors.</summary>
    /// <param name="quantization">The codec.</param>
    /// <param name="dimensions">The vector length.</param>
    /// <returns>The record size; 0 for <see cref="VectorQuantization.None"/>.</returns>
    public static int RecordBytes(VectorQuantization quantization, int dimensions) => quantization switch
    {
        VectorQuantization.Scalar8 => dimensions + Quantizers.CorrectionBytes,
        VectorQuantization.Scalar4 => ((dimensions + 1) / 2) + Quantizers.CorrectionBytes,
        VectorQuantization.OneBit => (dimensions + 63) / 64 * 8,
        _ => 0,
    };
}
