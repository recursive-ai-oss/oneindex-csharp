namespace OneIndex.Storage;

/// <summary>
/// A reversible, length-preserving transform applied to file bytes by position, the slot for encryption at rest. The same
/// bytes at the same file offset always transform the same way, so a range can be read without the rest of the file. The
/// transform must not depend on the file's name (files are renamed into place).
/// </summary>
public interface IBlockTransform
{
    /// <summary>The unit the transform works in, in bytes. Callers of <see cref="TransformingStorage"/> see byte-granular access regardless.</summary>
    int BlockSize { get; }

    /// <summary>Turns plain bytes into stored bytes, in place.</summary>
    /// <param name="fileOffset">The offset in the file of <c>data[0]</c>.</param>
    /// <param name="data">The bytes to transform.</param>
    void Encode(long fileOffset, Span<byte> data);

    /// <summary>Turns stored bytes back into plain bytes, in place.</summary>
    /// <param name="fileOffset">The offset in the file of <c>data[0]</c>.</param>
    /// <param name="data">The bytes to transform.</param>
    void Decode(long fileOffset, Span<byte> data);
}

/// <summary>The transform that changes nothing.</summary>
public sealed class IdentityBlockTransform : IBlockTransform
{
    /// <summary>The shared instance.</summary>
    public static IdentityBlockTransform Instance { get; } = new();

    /// <inheritdoc/>
    public int BlockSize => 1;

    /// <inheritdoc/>
    public void Encode(long fileOffset, Span<byte> data)
    {
    }

    /// <inheritdoc/>
    public void Decode(long fileOffset, Span<byte> data)
    {
    }
}
