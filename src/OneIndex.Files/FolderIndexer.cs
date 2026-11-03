using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.Files;

/// <summary>
/// Indexes a folder into a <c>SearchIndex&lt;FilePassage&gt;</c> and keeps it current. A run walks the folder, compares every file with its record
/// in the index (size and modification time first, then the SHA-256 of the bytes), reads only the new and changed ones, moves renamed ones,
/// removes the vanished ones, and commits as it goes. Running it again after any interruption continues where the last commit left off.
/// </summary>
/// <remarks>
/// <para><b>Guarantees.</b> Everything of one file (its passages, its record and, for an archive, those of its entries) is written between two
/// commits as a unit, so after a crash or a kill every file is either complete or absent, never half there, and a file's passages never appear twice.
/// Identifiers do not depend on the order or timing of the run, so an interrupted and resumed run ends with the same passage identifiers and text as
/// an uninterrupted one. Files are visited in path order.</para>
/// <para><b>Local only.</b> Nothing here uses the network. The index holds the extracted text of your files and is as sensitive as they are.</para>
/// <para>After an exception from a run, dispose the index: uncommitted changes are then discarded and the last commit stays whole.</para>
/// </remarks>
public static class FolderIndexer
{
    /// <summary>Opens or creates a passage index in a directory.</summary>
    /// <param name="indexPath">The index directory.</param>
    /// <param name="embedder">Embeds passages for hybrid search; null for text search only.</param>
    /// <returns>The index.</returns>
    public static SearchIndex<FilePassage> OpenIndex(string indexPath, IEmbedder? embedder = null) => embedder is null ? SearchIndex.Open<FilePassage>(indexPath, null) : SearchIndex.OpenSizedToEmbedder<FilePassage>(indexPath, embedder);

    /// <summary>Indexes or updates a folder.</summary>
    /// <param name="index">The passage index; the caller owns it and keeps it open.</param>
    /// <param name="root">The folder.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <param name="progress">Told after every file.</param>
    /// <param name="cancellationToken">Cancels the run; work finished so far is committed first.</param>
    /// <returns>What the run changed.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static FolderIndexResult Run(SearchIndex<FilePassage> index, string root, FolderIndexOptions? options = null, IProgress<FolderProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(root);
        options ??= new FolderIndexOptions();
        return new Scan(index, Path.GetFullPath(root), options, progress).Execute(cancellationToken);
    }

    /// <summary>The asynchronous twin of <see cref="Run"/>; the work runs on a pool thread and gives the same index.</summary>
    /// <param name="index">The passage index.</param>
    /// <param name="root">The folder.</param>
    /// <param name="options">Settings; null for the defaults.</param>
    /// <param name="progress">Told after every file.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the run changed.</returns>
    public static Task<FolderIndexResult> RunAsync(SearchIndex<FilePassage> index, string root, FolderIndexOptions? options = null, IProgress<FolderProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(index, root, options, progress, cancellationToken), cancellationToken);

    private sealed class Discovered(string fileId, string fullPath, long size, long modifiedTicks)
    {
        public string FileId { get; } = fileId;

        public string FullPath { get; } = fullPath;

        public long Size { get; } = size;

        public long ModifiedTicks { get; } = modifiedTicks;
    }

    private sealed class Work(Discovered file, FilePassage? old)
    {
        public Discovered File { get; } = file;

        public FilePassage? Old { get; } = old;

        public List<FilePassage>? Docs { get; set; }

        public long BytesRead { get; set; }
    }

    private sealed class Scan
    {
        private readonly SearchIndex<FilePassage> _index;
        private readonly string _root;
        private readonly FolderIndexOptions _o;
        private readonly IProgress<FolderProgress>? _progress;
        private readonly FolderPipeline _pipe;
        private readonly Dictionary<string, FilePassage> _records = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FilePassage>> _byContainer = new(StringComparer.Ordinal);
        private long _seen;
        private long _toIndex;
        private long _indexed;
        private long _unchanged;
        private long _failed;
        private long _passages;
        private long _bytes;
        private long _commits;
        private long _added;
        private long _updated;
        private long _renamed;
        private long _removed;
        private int _pendingFiles;
        private long _pendingBytes;

        public Scan(SearchIndex<FilePassage> index, string root, FolderIndexOptions options, IProgress<FolderProgress>? progress)
        {
            _index = index;
            _root = root;
            _o = options;
            _progress = progress;
            _pipe = new FolderPipeline(options);
        }

        public FolderIndexResult Execute(CancellationToken ct)
        {
            if (!Directory.Exists(_root))
            {
                throw new DirectoryNotFoundException($"The folder '{_root}' does not exist.");
            }

            long start = _o.TimeProvider.GetTimestamp();
            try
            {
                Core(ct);
            }
            catch (OperationCanceledException)
            {
                // Units finished before the cancellation are whole; keep them.
                Commit();
                throw;
            }

            Commit();
            return new FolderIndexResult
            {
                FilesSeen = _seen,
                Added = _added,
                Updated = _updated,
                Renamed = _renamed,
                Removed = _removed,
                Unchanged = _unchanged,
                Failed = _failed,
                Passages = _passages,
                BytesRead = _bytes,
                Commits = _commits,
                Elapsed = _o.TimeProvider.GetElapsedTime(start),
            };
        }

        private void Core(CancellationToken ct)
        {
            LoadRecords(ct);
            List<Discovered> files = Walk(ct);
            _seen = files.Count;

            // Phase 1: stat-only comparison with the records.
            var changed = new List<Work>();
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (Discovered d in files)
            {
                present.Add(d.FileId);
                _records.TryGetValue(d.FileId, out FilePassage? old);
                if (old is not null && old.Container != old.FileId)
                {
                    old = null; // an entry of an archive that used to be a file at this path
                }

                if (old is not null && old.Fingerprint == _pipe.Fingerprint && old.Size == d.Size && old.ModifiedTicks == d.ModifiedTicks)
                {
                    _unchanged++;
                    continue;
                }

                changed.Add(new Work(d, old));
            }

            var missing = new List<FilePassage>();
            foreach (FilePassage r in _records.Values)
            {
                if (r.Container == r.FileId && !present.Contains(r.FileId))
                {
                    missing.Add(r);
                }
            }

            missing.Sort((a, b) => string.CompareOrdinal(a.FileId, b.FileId));

            // Phase 2: a changed file whose bytes are the same only moved its time; a new file whose bytes match a missing one is a rename.
            var renames = new List<(Discovered To, FilePassage From)>();
            var touches = new List<(Discovered File, FilePassage Old)>();
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var toIngest = new List<Work>();
            foreach (Work w in changed)
            {
                ct.ThrowIfCancellationRequested();
                if (w.Old is not null)
                {
                    if (w.Old.Fingerprint == _pipe.Fingerprint && w.Old.Kind == FilePassage.KindFile && w.Old.Hash.Length > 0 && w.Old.Size == w.File.Size
                        && TryHash(w.File, out string h) && h == w.Old.Hash)
                    {
                        touches.Add((w.File, w.Old));
                        continue;
                    }

                    toIngest.Add(w);
                    continue;
                }

                FilePassage? from = null;
                string? hashed = null;
                foreach (FilePassage m in missing)
                {
                    if (m.Kind != FilePassage.KindFile || m.Format == "archive" || m.Fingerprint != _pipe.Fingerprint || m.Size != w.File.Size || m.Hash.Length == 0 || claimed.Contains(m.FileId))
                    {
                        continue;
                    }

                    if (hashed is null && !TryHash(w.File, out hashed))
                    {
                        break;
                    }

                    if (hashed == m.Hash)
                    {
                        from = m;
                        break;
                    }
                }

                if (from is not null)
                {
                    claimed.Add(from.FileId);
                    renames.Add((w.File, from));
                }
                else
                {
                    toIngest.Add(w);
                }
            }

            _toIndex = toIngest.Count;
            Report(null);

            foreach ((Discovered file, FilePassage old) in touches)
            {
                FilePassage copy = Clone(old);
                copy.Size = file.Size;
                copy.ModifiedTicks = file.ModifiedTicks;
                _index.Upsert(copy, ct);
                _records[copy.FileId] = copy;
                Replace(copy.FileId, [copy]);
                _unchanged++;
                Applied(0);
            }

            foreach ((Discovered to, FilePassage from) in renames)
            {
                ct.ThrowIfCancellationRequested();
                if (!TryRename(to, from, ct))
                {
                    toIngest.Add(new Work(to, null));
                    _toIndex++;
                    claimed.Remove(from.FileId);
                }
            }

            foreach (FilePassage gone in missing)
            {
                if (claimed.Contains(gone.FileId))
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                DeleteContainer(gone.FileId, null);
                _removed++;
                Applied(0);
            }

            toIngest.Sort((a, b) => string.CompareOrdinal(a.File.FileId, b.File.FileId));
            int width = _o.MaxDegreeOfParallelism;
            for (int at = 0; at < toIngest.Count; at += width)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(width, toIngest.Count - at);
                if (n == 1)
                {
                    Read(toIngest[at], ct);
                }
                else
                {
                    var tasks = new Task[n];
                    for (int i = 0; i < n; i++)
                    {
                        Work w = toIngest[at + i];
                        tasks[i] = Task.Run(() => Read(w, ct), ct);
                    }

                    try
                    {
                        Task.WaitAll(tasks, ct);
                    }
                    catch (AggregateException e) when (e.InnerExceptions.Count > 0)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    Write(toIngest[at + i], ct);
                }
            }
        }

        private void Report(string? current) =>
            _progress?.Report(new FolderProgress(_seen, _toIndex, _indexed, _unchanged, _failed, _passages, _bytes, _commits, current));

        private bool TryHash(Discovered d, out string hash)
        {
            try
            {
                using FileStream fs = Open(d.FullPath);
                hash = FolderPipeline.HashOf(fs);
                _bytes += fs.Length;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                hash = string.Empty;
                return false;
            }
        }

        private static FileStream Open(string path) =>
            new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

        private static FilePassage Clone(FilePassage p) => new()
        {
            Id = p.Id,
            Kind = p.Kind,
            FileId = p.FileId,
            Container = p.Container,
            FileName = p.FileName,
            Heading = p.Heading,
            Text = p.Text,
            Format = p.Format,
            Page = p.Page,
            Ordinal = p.Ordinal,
            Start = p.Start,
            End = p.End,
            Boxes = p.Boxes,
            BoxUnits = p.BoxUnits,
            PageWidth = p.PageWidth,
            PageHeight = p.PageHeight,
            Size = p.Size,
            ModifiedTicks = p.ModifiedTicks,
            Hash = p.Hash,
            PassageCount = p.PassageCount,
            Fingerprint = p.Fingerprint,
            Error = p.Error,
            Embedding = p.Embedding,
        };

        // Reads, extracts and chunks one file. Runs on a pool thread when files are read in parallel; touches no shared state.
        private void Read(Work w, CancellationToken ct)
        {
            Discovered d = w.File;
            string name = Path.GetFileName(d.FullPath);
            var docs = new List<FilePassage>();
            using var timeout = new CancellationTokenSource(_o.FileTimeout, _o.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                using FileStream fs = Open(d.FullPath);
                if (fs.Length > _o.MaxFileBytes)
                {
                    docs.Add(_pipe.ErrorRecord(d.FileId, d.FileId, name, d.Size, d.ModifiedTicks, string.Empty, $"The file has {fs.Length} bytes, more than the {_o.MaxFileBytes} byte limit."));
                }
                else
                {
                    string hash = FolderPipeline.HashOf(fs);
                    _pipe.Ingest(docs, d.FileId, name, fs, d.Size, d.ModifiedTicks, hash, linked.Token);
                    w.BytesRead = fs.Length;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                docs.Clear();
                docs.Add(_pipe.ErrorRecord(d.FileId, d.FileId, name, d.Size, d.ModifiedTicks, string.Empty, $"Reading took longer than the time limit of {_o.FileTimeout}."));
            }
            catch (FileNotFoundException)
            {
                w.Docs = null; // vanished since the walk; the next run sees it gone
                return;
            }
            catch (DirectoryNotFoundException)
            {
                w.Docs = null;
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                docs.Clear();
                docs.Add(_pipe.ErrorRecord(d.FileId, d.FileId, name, d.Size, d.ModifiedTicks, string.Empty, $"{e.GetType().Name}: {e.Message}"));
            }

            w.Docs = docs;
        }

        private void Write(Work w, CancellationToken ct)
        {
            if (w.Docs is null)
            {
                return;
            }

            Report(w.File.FileId);
            var newIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (FilePassage p in w.Docs)
            {
                newIds.Add(p.Id);
            }

            if (_byContainer.ContainsKey(w.File.FileId))
            {
                DeleteContainer(w.File.FileId, newIds);
            }

            _index.Upsert(w.Docs, ct);
            Replace(w.File.FileId, w.Docs.Where(p => p.Kind != FilePassage.KindPassage).ToList());
            int passages = w.Docs.Count(p => p.Kind == FilePassage.KindPassage);
            _passages += passages;
            _failed += w.Docs.Count(p => p.Kind == FilePassage.KindError && p.Container == p.FileId);
            if (w.Old is null)
            {
                _added++;
            }
            else
            {
                _updated++;
            }

            _indexed++;
            Applied(w.BytesRead);
        }

        // Moves the record and passages of a file to a new path without reading it again.
        private bool TryRename(Discovered to, FilePassage from, CancellationToken ct)
        {
            if (from.PassageCount > SearchQuery<FilePassage>.MaxResultWindow)
            {
                return false;
            }

            var moved = new List<FilePassage>(from.PassageCount + 1);
            if (from.PassageCount > 0)
            {
                string old = from.FileId;
                SearchResults<FilePassage> found = _index.Search().Where(p => p.FileId == old && p.Kind == "passage").OrderBy(p => p.Kind).Take(from.PassageCount).Execute(ct);
                if (found.Count != from.PassageCount)
                {
                    return false;
                }

                string name = Path.GetFileName(to.FullPath);
                foreach (SearchHit<FilePassage> hit in found)
                {
                    FilePassage p = Clone(hit.Document);
                    p.Id = to.FileId + "#" + p.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    p.FileId = to.FileId;
                    p.Container = to.FileId;
                    p.FileName = name;
                    moved.Add(p);
                }

                moved.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
                for (int i = 0; i < moved.Count; i++)
                {
                    if (moved[i].Ordinal != i)
                    {
                        return false;
                    }
                }
            }

            FilePassage record = Clone(from);
            record.Id = "file:" + to.FileId;
            record.FileId = to.FileId;
            record.Container = to.FileId;
            record.FileName = Path.GetFileName(to.FullPath);
            record.Size = to.Size;
            record.ModifiedTicks = to.ModifiedTicks;
            moved.Insert(0, record);
            DeleteContainer(from.FileId, null);
            _index.Upsert(moved, ct);
            Replace(to.FileId, [record]);
            _renamed++;
            Applied(0);
            return true;
        }

        // Deletes every document a container (a file and, for an archive, its entries) has in the index, except the ids in keep.
        private void DeleteContainer(string container, HashSet<string>? keep)
        {
            if (!_byContainer.TryGetValue(container, out List<FilePassage>? group))
            {
                return;
            }

            foreach (FilePassage r in group)
            {
                if (keep is null || !keep.Contains(r.Id))
                {
                    _index.Delete(r.Id);
                }

                for (int i = 0; i < r.PassageCount; i++)
                {
                    string id = r.FileId + "#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (keep is null || !keep.Contains(id))
                    {
                        _index.Delete(id);
                    }
                }

                _records.Remove(r.FileId);
            }

            _byContainer.Remove(container);
        }

        private void Replace(string container, List<FilePassage> records)
        {
            if (_byContainer.TryGetValue(container, out List<FilePassage>? old))
            {
                foreach (FilePassage r in old)
                {
                    _records.Remove(r.FileId);
                }
            }

            _byContainer[container] = records;
            foreach (FilePassage r in records)
            {
                _records[r.FileId] = r;
            }
        }

        private void Applied(long bytes)
        {
            _bytes += bytes;
            _pendingFiles++;
            _pendingBytes += bytes;
            if (_pendingFiles >= _o.CommitEveryFiles || _pendingBytes >= _o.CommitEveryBytes)
            {
                Commit();
            }

            Report(null);
        }

        private void Commit()
        {
            if (_pendingFiles > 0)
            {
                _index.Commit();
                _commits++;
            }

            _pendingFiles = 0;
            _pendingBytes = 0;
        }

        private void LoadRecords(CancellationToken ct)
        {
            foreach (string kind in new[] { FilePassage.KindFile, FilePassage.KindError })
            {
                SortCursor? cursor = null;
                while (true)
                {
                    SearchQuery<FilePassage> q = kind == FilePassage.KindFile
                        ? _index.Search().Where(p => p.Kind == "file").OrderBy(p => p.Kind)
                        : _index.Search().Where(p => p.Kind == "error").OrderBy(p => p.Kind);
                    if (cursor is not null)
                    {
                        q = q.SearchAfter(cursor);
                    }

                    SearchResults<FilePassage> page = q.Take(1000).Execute(ct);
                    foreach (SearchHit<FilePassage> hit in page)
                    {
                        FilePassage r = hit.Document;
                        r.Embedding = null;
                        _records[r.FileId] = r;
                        if (!_byContainer.TryGetValue(r.Container, out List<FilePassage>? group))
                        {
                            _byContainer[r.Container] = group = [];
                        }

                        group.Add(r);
                    }

                    if (page.Count < 1000 || page.NextCursor is null)
                    {
                        break;
                    }

                    cursor = page.NextCursor;
                }
            }
        }

        private List<Discovered> Walk(CancellationToken ct)
        {
            var result = new List<Discovered>();
            var excluded = new HashSet<string>(_o.ExcludeDirectories.Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d))), StringComparer.Ordinal);
            IgnoreRules? extra = _o.IgnorePatterns.Count > 0 ? IgnoreRules.Parse(string.Join('\n', _o.IgnorePatterns)) : null;
            var stack = new Stack<(DirectoryInfo Dir, string Rel, List<(string Base, IgnoreRules Rules)> Local)>();
            stack.Push((new DirectoryInfo(_root), string.Empty, []));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                (DirectoryInfo dir, string rel, List<(string Base, IgnoreRules Rules)> inherited) = stack.Pop();
                var local = inherited;
                if (_o.UseIgnoreFiles)
                {
                    local = [.. inherited];
                    foreach (string rulesName in new[] { ".gitignore", ".oneindexignore" })
                    {
                        string rulesPath = Path.Combine(dir.FullName, rulesName);
                        if (File.Exists(rulesPath))
                        {
                            try
                            {
                                local.Add((rel, IgnoreRules.Parse(TextExtractor.Decode(File.OpenRead(rulesPath), 1 << 20, ct))));
                            }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                            {
                                // An unreadable rules file ignores nothing.
                            }
                        }
                    }
                }

                FileSystemInfo[] entries;
                try
                {
                    entries = dir.GetFileSystemInfos();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (FileSystemInfo e in entries)
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue; // links are not followed
                    }

                    bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
                    string childRel = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
                    if (IsIgnored(childRel, isDir, local, extra))
                    {
                        continue;
                    }

                    if (isDir)
                    {
                        if (!excluded.Contains(Path.TrimEndingDirectorySeparator(e.FullName)))
                        {
                            stack.Push(((DirectoryInfo)e, childRel, local));
                        }
                    }
                    else if (e is FileInfo fi)
                    {
                        result.Add(new Discovered(FolderPipeline.FileIdOf(childRel), fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks));
                    }
                }
            }

            result.Sort((a, b) => string.CompareOrdinal(a.FileId, b.FileId));
            return result;
        }

        private bool IsIgnored(string rel, bool isDir, List<(string Base, IgnoreRules Rules)> local, IgnoreRules? extra)
        {
            bool? verdict = null;
            if (_o.UseDefaultIgnores)
            {
                verdict = IgnoreRules.Defaults.Evaluate(rel, isDir, verdict);
            }

            if (extra is not null)
            {
                verdict = extra.Evaluate(rel, isDir, verdict);
            }

            foreach ((string b, IgnoreRules rules) in local)
            {
                string within = b.Length == 0 ? rel : rel[(b.Length + 1)..];
                verdict = rules.Evaluate(within, isDir, verdict);
            }

            return verdict == true;
        }
    }
}
