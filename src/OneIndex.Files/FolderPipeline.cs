using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;

namespace OneIndex.Files;

/// <summary>Reads one file (and, for an archive, the files inside it) into the documents that represent it in the index.</summary>
internal sealed class FolderPipeline
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".tar", ".tgz", ".tbz2", ".txz", ".tzst", ".gz", ".bz2", ".xz", ".zst", ".7z", ".cpio", ".iso", ".cab", ".rar",
    };

    private readonly FolderIndexOptions _options;
    private readonly IFileExtractor[] _extractors;
    private readonly TextExtractor _text = new();

    internal FolderPipeline(FolderIndexOptions options)
    {
        options.Validate();
        _options = options;
        _extractors = options.Extractors is { } given ? [.. given] : [_text, new HtmlExtractor(), new PdfExtractor()];
        var fp = new StringBuilder("folder-1");
        foreach (IFileExtractor e in _extractors)
        {
            fp.Append(';').Append(e.Name);
        }

        ChunkOptions c = options.Chunking;
        fp.Append(CultureInfo.InvariantCulture, $";{c.MinTokens}/{c.TargetTokens}/{c.MaxTokens}");
        Fingerprint = fp.ToString();
    }

    /// <summary>Identifies the extractors and chunk sizes; a file indexed under another value is read again.</summary>
    internal string Fingerprint { get; }

    internal static bool IsArchiveName(string name) => ArchiveExtensions.Contains(Path.GetExtension(name));

    /// <summary>The identifier of a file below the root: <c>/dir/name</c>.</summary>
    internal static string FileIdOf(string relativePath) => "/" + relativePath.Replace('\\', '/').TrimStart('/');

    internal static string HashOf(Stream content)
    {
        long position = content.Position;
        byte[] hash = SHA256.HashData(content);
        content.Position = position;
        return Convert.ToHexStringLower(hash);
    }

    // Reads a top-level file and everything inside it into sink. A bad file becomes an error document instead of an exception.
    internal void Ingest(List<FilePassage> sink, string fileId, string name, Stream content, long size, long modifiedTicks, string hash, CancellationToken ct)
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { fileId };
        long budget = _options.MaxArchiveBytes;
        IngestOne(sink, used, fileId, fileId, name, content, size, modifiedTicks, hash, 0, ref budget, ct);
    }

    internal FilePassage ErrorRecord(string fileId, string container, string name, long size, long modifiedTicks, string hash, string message) => new()
    {
        Id = "file:" + fileId,
        Kind = FilePassage.KindError,
        FileId = fileId,
        Container = container,
        FileName = name,
        Format = "error",
        Size = size,
        ModifiedTicks = modifiedTicks,
        Hash = hash,
        Fingerprint = Fingerprint,
        Error = Truncate(message, 600),
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private void IngestOne(List<FilePassage> sink, HashSet<string> used, string fileId, string container, string name, Stream content, long size, long modifiedTicks,
        string hash, int depth, ref long budget, CancellationToken ct)
    {
        int mark = sink.Count;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (IsArchiveName(name))
            {
                if (depth >= _options.MaxArchiveDepth)
                {
                    throw new InvalidDataException($"Archives nested deeper than {_options.MaxArchiveDepth} levels are not opened.");
                }

                IngestArchive(sink, used, fileId, container, name, content, size, modifiedTicks, hash, depth, ref budget, ct);
                return;
            }

            IFileExtractor? extractor = null;
            foreach (IFileExtractor e in _extractors)
            {
                if (e.Accepts(name))
                {
                    extractor = e;
                    break;
                }
            }

            if (extractor is null && TextExtractor.LooksLikeText(content))
            {
                extractor = _text;
            }

            if (extractor is null)
            {
                sink.Add(Record(fileId, container, name, "binary", size, modifiedTicks, hash, 0));
                return;
            }

            ExtractedFile extracted = extractor.Extract(content, name, ct);
            AddPassages(sink, fileId, container, name, extracted, size, modifiedTicks, hash, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
        {
            sink.RemoveRange(mark, sink.Count - mark);
            sink.Add(ErrorRecord(fileId, container, name, size, modifiedTicks, hash, $"{e.GetType().Name}: {e.Message}"));
        }
    }

    private void IngestArchive(List<FilePassage> sink, HashSet<string> used, string fileId, string container, string name, Stream content, long size, long modifiedTicks,
        string hash, int depth, ref long budget, CancellationToken ct)
    {
        var limits = new ArchiveLimits { MaxTotalSize = _options.MaxArchiveBytes, MaxEntryCount = _options.MaxArchiveEntries };
        string? damage = null;
        int entries = 0;
        var children = new List<FilePassage>();
        try
        {
            using Archive archive = Archive.Open(content, new ArchiveOpenOptions { FileName = name, LeaveOpen = true, Limits = limits });
            using ArchiveReader reader = archive.OpenReader();
            while (reader.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Kind != ArchiveEntryKind.File)
                {
                    continue;
                }

                entries++;
                string? path = CleanEntryName(entry.Name);
                if (path is null)
                {
                    continue;
                }

                string childId = fileId + "!/" + path;
                for (int n = 2; !used.Add(childId); n++)
                {
                    childId = fileId + "!/" + path + "~" + n.ToString(CultureInfo.InvariantCulture);
                }

                string leaf = path[(path.LastIndexOf('/') + 1)..];
                long mtime = entry.ModifiedTime?.UtcTicks ?? 0;
                if (entry.Size > _options.MaxArchiveEntryBytes)
                {
                    children.Add(ErrorRecord(childId, container, leaf, entry.Size, mtime, string.Empty, $"The entry has {entry.Size} bytes, more than the {_options.MaxArchiveEntryBytes} byte limit."));
                    continue;
                }

                using var data = new MemoryStream();
                using (Stream s = reader.OpenEntryStream())
                {
                    CopyLimited(s, data, _options.MaxArchiveEntryBytes, ct);
                }

                budget -= data.Length;
                if (budget < 0)
                {
                    throw new InvalidDataException($"The archive holds more than {_options.MaxArchiveBytes} bytes of files.");
                }

                data.Position = 0;
                string childHash = HashOf(data);
                IngestOne(children, used, childId, container, leaf, data, data.Length, mtime, childHash, depth + 1, ref budget, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            damage = $"{e.GetType().Name}: {e.Message} (after {entries} entries)";
        }

        if (damage is not null && children.Count == 0)
        {
            // Nothing could be read: the archive itself is the error document.
            sink.Add(ErrorRecord(fileId, container, name, size, modifiedTicks, hash, damage));
            return;
        }

        FilePassage record = Record(fileId, container, name, "archive", size, modifiedTicks, hash, 0);
        if (damage is not null)
        {
            record.Kind = FilePassage.KindError;
            record.Error = Truncate("Only part of the archive could be read. " + damage, 600);
        }

        sink.Add(record);
        sink.AddRange(children);
    }

    private static void CopyLimited(Stream from, Stream to, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > limit)
            {
                throw new InvalidDataException($"An archive entry is larger than the {limit} byte limit.");
            }

            to.Write(buffer, 0, n);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Normalises an entry name to a relative path with <c>/</c>; null for a name that is empty or climbs out with <c>..</c>.</summary>
    internal static string? CleanEntryName(string name)
    {
        string p = name.Replace('\\', '/');
        var parts = new List<string>();
        foreach (string seg in p.Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                return null;
            }

            parts.Add(seg);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private FilePassage Record(string fileId, string container, string name, string format, long size, long modifiedTicks, string hash, int passages) => new()
    {
        Id = "file:" + fileId,
        Kind = FilePassage.KindFile,
        FileId = fileId,
        Container = container,
        FileName = name,
        Format = format,
        Size = size,
        ModifiedTicks = modifiedTicks,
        Hash = hash,
        PassageCount = passages,
        Fingerprint = Fingerprint,
    };

    private void AddPassages(List<FilePassage> sink, string fileId, string container, string name, ExtractedFile extracted, long size, long modifiedTicks, string hash, CancellationToken ct)
    {
        int ordinal = 0;
        var passages = new List<FilePassage>();
        foreach (ExtractedUnit unit in extracted.Units)
        {
            foreach (Chunk chunk in Chunker.Cut(unit, _options.Chunking))
            {
                ct.ThrowIfCancellationRequested();
                passages.Add(new FilePassage
                {
                    Id = fileId + "#" + ordinal.ToString(CultureInfo.InvariantCulture),
                    Kind = FilePassage.KindPassage,
                    FileId = fileId,
                    Container = container,
                    FileName = name,
                    Heading = chunk.Heading,
                    Text = unit.Text.Substring(chunk.Start, chunk.End - chunk.Start),
                    Format = extracted.Format,
                    Page = unit.Page,
                    Ordinal = ordinal,
                    Start = chunk.Start,
                    End = chunk.End,
                    Boxes = BoxesFor(unit, chunk.Start, chunk.End),
                    BoxUnits = unit.BoxUnits,
                    PageWidth = unit.Width,
                    PageHeight = unit.Height,
                });
                ordinal++;
            }
        }

        sink.Add(Record(fileId, container, name, extracted.Format, size, modifiedTicks, hash, ordinal));
        sink.AddRange(passages);
    }

    /// <summary>One box per line of the page that the range touches: the union of the boxes of the words inside it.</summary>
    internal static string BoxesFor(ExtractedUnit unit, int start, int end)
    {
        IReadOnlyList<WordSpan> words = unit.Words;
        if (words.Count == 0)
        {
            return string.Empty;
        }

        int lo = 0;
        int hi = words.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (words[mid].End <= start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var sb = new StringBuilder();
        int line = -1;
        SourceBox box = default;
        for (int i = lo; i < words.Count && words[i].Start < end; i++)
        {
            WordSpan w = words[i];
            if (w.Line != line)
            {
                if (line >= 0)
                {
                    AppendBox(sb, box);
                }

                line = w.Line;
                box = w.Box;
            }
            else
            {
                box = SourceBox.Union(box, w.Box);
            }
        }

        if (line >= 0)
        {
            AppendBox(sb, box);
        }

        return sb.ToString();
    }

    private static void AppendBox(StringBuilder sb, SourceBox b)
    {
        if (sb.Length > 0)
        {
            sb.Append(';');
        }

        sb.Append(CultureInfo.InvariantCulture, $"{b.Left:0.##},{b.Top:0.##},{b.Right:0.##},{b.Bottom:0.##}");
    }

    /// <summary>Re-reads one unit of a file the way the indexer read it.</summary>
    internal ExtractedUnit? ReadUnit(Stream content, string name, int page, CancellationToken ct)
    {
        IFileExtractor? extractor = null;
        foreach (IFileExtractor e in _extractors)
        {
            if (e.Accepts(name))
            {
                extractor = e;
                break;
            }
        }

        if (extractor is null && TextExtractor.LooksLikeText(content))
        {
            extractor = _text;
        }

        if (extractor is null)
        {
            return null;
        }

        foreach (ExtractedUnit u in extractor.Extract(content, name, ct).Units)
        {
            if (u.Page == page)
            {
                return u;
            }
        }

        return null;
    }

    /// <summary>Opens the entry <c>inner</c> (a path that may itself go through archives) of an archive stream.</summary>
    internal Stream OpenEntry(Stream archiveStream, string archiveName, string inner, CancellationToken ct)
    {
        int cut = inner.IndexOf("!/", StringComparison.Ordinal);
        string head = cut < 0 ? inner : inner[..cut];
        string rest = cut < 0 ? string.Empty : inner[(cut + 2)..];
        string dupSuffix = string.Empty;
        int tilde = head.LastIndexOf('~');
        if (tilde > 0 && tilde < head.Length - 1 && head[(tilde + 1)..].All(char.IsAsciiDigit) && cut < 0)
        {
            // A repeated name inside the archive was numbered when it was indexed; take the nth match.
            dupSuffix = head[(tilde + 1)..];
        }

        int wantedOccurrence = dupSuffix.Length > 0 ? int.Parse(dupSuffix, CultureInfo.InvariantCulture) : 1;
        string wantedName = dupSuffix.Length > 0 ? head[..tilde] : head;
        using Archive archive = Archive.Open(archiveStream, new ArchiveOpenOptions { FileName = archiveName, LeaveOpen = true });
        using ArchiveReader reader = archive.OpenReader();
        int seen = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Kind != ArchiveEntryKind.File || CleanEntryName(entry.Name) != wantedName)
            {
                continue;
            }

            if (++seen < wantedOccurrence)
            {
                continue;
            }

            var data = new MemoryStream();
            using (Stream s = reader.OpenEntryStream())
            {
                CopyLimited(s, data, _options.MaxArchiveEntryBytes, ct);
            }

            data.Position = 0;
            if (rest.Length == 0)
            {
                return data;
            }

            using (data)
            {
                return OpenEntry(data, wantedName[(wantedName.LastIndexOf('/') + 1)..], rest, ct);
            }
        }

        throw new FileNotFoundException($"The archive has no entry '{head}'.");
    }
}
