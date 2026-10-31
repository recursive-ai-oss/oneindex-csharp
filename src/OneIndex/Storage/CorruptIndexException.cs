namespace OneIndex.Storage;

/// <summary>
/// An index file is damaged: a checksum does not match, a length or offset points outside the file, a required file is missing
/// or truncated, or a structure is malformed. The message names the file. Corruption is never repaired silently.
/// </summary>
public class CorruptIndexException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="fileName">The name of the damaged file, as the storage knows it.</param>
    /// <param name="message">What is wrong, without the file name (it is added).</param>
    /// <param name="innerException">The cause, if any.</param>
    public CorruptIndexException(string fileName, string message, Exception? innerException = null)
        : base($"Corrupt index file '{fileName}': {message}", innerException)
    {
        FileName = fileName;
    }

    /// <summary>The name of the damaged file.</summary>
    public string FileName { get; }
}

/// <summary>
/// An index file is intact but was written by a newer format than this library reads (or by a codec it does not know).
/// </summary>
public sealed class UnsupportedFormatException : CorruptIndexException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="message">What is not supported.</param>
    public UnsupportedFormatException(string fileName, string message)
        : base(fileName, message)
    {
    }
}
