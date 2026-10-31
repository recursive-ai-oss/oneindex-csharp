using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>A codec identity as recorded in a commit: a name and the format version the file was written with.</summary>
/// <param name="Name">The codec name, for example <c>"postings"</c>.</param>
/// <param name="Version">The codec's format version, at least 1.</param>
public readonly record struct CodecRef(string Name, int Version);

/// <summary>The names of the parts a segment is made of. A segment lists one file per part it has.</summary>
public static class SegmentParts
{
    /// <summary>The term dictionary.</summary>
    public const string Terms = "terms";

    /// <summary>Postings: documents, frequencies, positions.</summary>
    public const string Postings = "postings";

    /// <summary>Per-document field lengths for scoring.</summary>
    public const string Lengths = "lengths";

    /// <summary>Per-document column values for filter, sort and facets.</summary>
    public const string Columns = "columns";

    /// <summary>The point index for numerics and geo.</summary>
    public const string Points = "points";

    /// <summary>Stored source documents.</summary>
    public const string Stored = "stored";

    /// <summary>The delete bitmap of one generation.</summary>
    public const string Deletes = "deletes";

    /// <summary>The map from document identifier to document id, written by the index itself for every segment of a schema with an identifier.</summary>
    public const string Ids = "ids";
}

/// <summary>
/// The codecs this build can read, written out explicitly (no discovery by reflection, so trimming and AOT see every one). Each
/// entry is the newest format version of that codec this build reads and writes; reading an older version stays supported until
/// a release says otherwise. A codec's owner raises its number here when its format changes. A file naming an unknown codec, or
/// a newer version than listed, is refused with <see cref="UnsupportedFormatException"/> rather than misread.
/// </summary>
public static class CodecRegistry
{
    private static readonly CodecRef[] Known =
    [
        new(SegmentParts.Terms, 1),

        new(SegmentParts.Postings, 1),

        new(SegmentParts.Lengths, 1),

        new(SegmentParts.Columns, 1),

        new(SegmentParts.Points, 1),

        new(SegmentParts.Stored, 1),

        new(SegmentParts.Deletes, 1),

        new(SegmentParts.Ids, 1),
    ];

    /// <summary>Every registered codec with its newest version, in a fixed order.</summary>
    public static IReadOnlyList<CodecRef> All => Known;

    /// <summary>The newest version of a codec, or 0 if the codec is not registered.</summary>
    /// <param name="name">The codec name.</param>
    /// <returns>The newest version this build reads.</returns>
    public static int NewestVersion(string name)
    {
        foreach (CodecRef known in Known)
        {
            if (string.Equals(known.Name, name, StringComparison.Ordinal))
            {
                return known.Version;
            }
        }

        return 0;
    }

    /// <summary>Whether this build can read a file written with <paramref name="codec"/>.</summary>
    /// <param name="codec">The codec as recorded in the commit.</param>
    /// <returns><see langword="true"/> when the codec is registered and the version is between 1 and the newest.</returns>
    public static bool IsSupported(CodecRef codec) => codec.Version >= 1 && codec.Version <= NewestVersion(codec.Name);

    /// <summary>Throws unless <paramref name="codec"/> is supported.</summary>
    /// <param name="fileName">The file that names the codec, for the exception.</param>
    /// <param name="codec">The codec as recorded in the commit.</param>
    /// <exception cref="UnsupportedFormatException">The codec is unknown or newer than this build.</exception>
    public static void EnsureSupported(string fileName, CodecRef codec)
    {
        if (!IsSupported(codec))
        {
            throw new UnsupportedFormatException(fileName, $"codec '{codec.Name}' version {codec.Version} is not supported (newest known: {NewestVersion(codec.Name)})");
        }
    }
}
