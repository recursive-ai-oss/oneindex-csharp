using System.Text.Json;
using OneIndex.Index;
using OneIndex.Storage;

namespace OneIndex.StaticIndexes;

/// <summary>One file of a static index.</summary>
/// <param name="Name">The file's name in the index.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="ChunkCrcs">The CRC-32C of each chunk, in order. A file of at most one chunk is stored under its own name; a longer file is stored as <c>name.c00000</c>, <c>name.c00001</c>, ...</param>
public sealed record StaticFile(string Name, long Size, IReadOnlyList<uint> ChunkCrcs);

/// <summary>
/// The manifest of a static index (<c>static.json</c>): the chunk size, the commit to open, and every file with the checksums of its chunks.
/// The format is in docs/static-index.md.
/// </summary>
public sealed class StaticManifest
{
    /// <summary>The name of the manifest file in a static index.</summary>
    public const string FileName = "static.json";

    /// <summary>The format version this build reads and writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Creates a manifest.</summary>
    /// <param name="chunkBytes">The chunk size.</param>
    /// <param name="commit">The name of the commit file to open.</param>
    /// <param name="files">The files, by name.</param>
    public StaticManifest(int chunkBytes, string commit, IReadOnlyList<StaticFile> files)
    {
        ChunkBytes = chunkBytes;
        Commit = commit;
        Files = files;
    }

    /// <summary>The chunk size in bytes.</summary>
    public int ChunkBytes { get; }

    /// <summary>The name of the commit file to open.</summary>
    public string Commit { get; }

    /// <summary>The files.</summary>
    public IReadOnlyList<StaticFile> Files { get; }

    /// <summary>The name a chunk of a file is stored under.</summary>
    /// <param name="file">The file.</param>
    /// <param name="chunk">The chunk number, from 0.</param>
    /// <returns>The object name.</returns>
    public static string ChunkName(StaticFile file, int chunk)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.ChunkCrcs.Count <= 1 ? file.Name : $"{file.Name}.c{chunk:D5}";
    }

    internal static string ChunkName(string file, long chunkCount, int chunk) => chunkCount <= 1 ? file : $"{file}.c{chunk:D5}";

    internal byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", CurrentVersion);
            w.WriteNumber("chunkBytes", ChunkBytes);
            w.WriteString("commit", Commit);
            w.WriteStartArray("files");
            foreach (StaticFile f in Files)
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name);
                w.WriteNumber("size", f.Size);
                w.WriteStartArray("crc");
                foreach (uint c in f.ChunkCrcs)
                {
                    w.WriteNumberValue(c);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Reads a manifest.</summary>
    /// <param name="json">The bytes of <c>static.json</c>.</param>
    /// <param name="limits">Bounds on sizes and counts; null for the defaults.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="CorruptIndexException">The JSON is malformed, a field is missing or of the wrong kind, a size or count is out of range, or the version is unknown.</exception>
    public static StaticManifest Parse(ReadOnlySpan<byte> json, IndexLimits? limits = null)
    {
        limits ??= IndexLimits.Default;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw Bad("the manifest is not a JSON object");
            }

            int version = 0;
            int chunk = 0;
            string? commit = null;
            List<StaticFile>? files = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString()!;
                reader.Read();
                switch (key)
                {
                    case "v":
                        version = reader.GetInt32();
                        break;
                    case "chunkBytes":
                        chunk = reader.GetInt32();
                        break;
                    case "commit":
                        commit = reader.GetString();
                        break;
                    case "files":
                        files = ReadFiles(ref reader, limits, chunk);
                        break;
                    default:
                        throw Bad($"unknown field '{key}'");
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                throw Bad("the manifest is truncated");
            }

            if (version != CurrentVersion)
            {
                throw new UnsupportedFormatException(FileName, $"static index format {version} is not supported");
            }

            if (chunk is < 1024 or > (1 << 26) || commit is null || !CommitFileNames.TryParse(commit, out _) || files is null)
            {
                throw Bad("a field is missing or out of range");
            }

            // chunkBytes must come before files in the writer's order; check sizes against it now.
            foreach (StaticFile f in files)
            {
                long expected = f.Size == 0 ? 0 : ((f.Size - 1) / chunk) + 1;
                if (f.ChunkCrcs.Count != expected)
                {
                    throw Bad($"file '{f.Name}' lists {f.ChunkCrcs.Count} chunks, expected {expected}");
                }
            }

            return new StaticManifest(chunk, commit, files);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw Bad(e.Message);
        }
    }

    private static List<StaticFile> ReadFiles(ref Utf8JsonReader reader, IndexLimits limits, int chunk)
    {
        if (chunk == 0)
        {
            throw Bad("chunkBytes must come before files");
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw Bad("files is not an array");
        }

        var files = new List<StaticFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string? name = null;
            long size = -1;
            var crcs = new List<uint>();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString()!;
                reader.Read();
                switch (key)
                {
                    case "name":
                        name = reader.GetString();
                        break;
                    case "size":
                        size = reader.GetInt64();
                        break;
                    case "crc":
                        if (reader.TokenType != JsonTokenType.StartArray)
                        {
                            throw Bad("crc is not an array");
                        }

                        while (reader.Read() && reader.TokenType == JsonTokenType.Number)
                        {
                            if (crcs.Count >= 1 << 22)
                            {
                                throw Bad("a file has too many chunks");
                            }

                            crcs.Add(reader.GetUInt32());
                        }

                        break;
                    default:
                        throw Bad($"unknown file field '{key}'");
                }
            }

            if (name is null || size < 0 || size > 1L << 40 || !seen.Add(name))
            {
                throw Bad("a file entry is incomplete, repeated or out of range");
            }

            StorageNames.Validate(name);
            files.Add(new StaticFile(name, size, crcs));
            if (files.Count > limits.MaxFields * 64)
            {
                throw Bad("too many files");
            }
        }

        return files;
    }

    private static CorruptIndexException Bad(string why) => new(FileName, why);
}
