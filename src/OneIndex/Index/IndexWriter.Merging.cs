using OneIndex.Storage;

namespace OneIndex.Index;

public sealed partial class IndexWriter
{
    private void MaybeMergeLocked()
    {
        var candidates = new List<MergeCandidate>();
        foreach (WorkSegment s in _segments)
        {
            if (!_busy.Contains(s.Info.Name))
            {
                candidates.Add(new MergeCandidate(s.Info.Name, s.Info.DocCount - s.DeletedCount, s.DeletedCount));
            }
        }

        foreach (IReadOnlyList<string> plan in _options.MergePolicy.Select(candidates))
        {
            var group = new List<WorkSegment>(plan.Count);
            foreach (string name in plan)
            {
                group.Add(_segments.First(s => string.Equals(s.Info.Name, name, StringComparison.Ordinal)));
            }

            StartMergeLocked(group);
        }
    }

    /// <summary>Marks the sources busy, captures their live documents and queues the merge. Called with the gate held.</summary>
    private void StartMergeLocked(List<WorkSegment> sources)
    {
        string name = NewSegmentName();
        var held = new List<string>();
        var infos = new List<SegmentInfo>(sources.Count);
        var captured = new List<LiveDocs>(sources.Count);
        foreach (WorkSegment s in sources)
        {
            _busy.Add(s.Info.Name);
            infos.Add(s.Info);
            foreach (SegmentFile f in s.Info.Files)
            {
                held.Add(f.FileName);
            }

            captured.Add(s.Words is not null
                ? LiveDocs.FromWords(s.Info.DocCount, (ulong[])s.Words.Clone())
                : SegmentReader.LoadLiveDocs(_storage, s.Info, _limits));
        }

        _refs.Acquire(held);
        _scheduler.Enqueue(ct => RunMerge(name, infos, captured, held, ct));
    }

    private void RunMerge(string name, List<SegmentInfo> infos, List<LiveDocs> captured, List<string> held, CancellationToken cancellationToken)
    {
        var readers = new List<SegmentReader>(infos.Count);
        SegmentFileSink? sink = null;
        try
        {
            for (int i = 0; i < infos.Count; i++)
            {
                readers.Add(SegmentReader.Open(_storage, _schema, infos[i], _codecs, _limits, captured[i], cancellationToken));
            }

            var maps = new DocMap[infos.Count];
            int total = 0;
            for (int i = 0; i < infos.Count; i++)
            {
                maps[i] = new DocMap(captured[i], infos[i].DocCount, total);
                total += maps[i].LiveCount;
            }

            SegmentInfo? result = null;
            if (total > 0)
            {
                sink = new SegmentFileSink(_storage, name, MakeThrottle(cancellationToken));
                foreach (ISegmentCodec codec in _codecs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sources = new List<MergeSource>(infos.Count);
                    for (int i = 0; i < infos.Count; i++)
                    {
                        sources.Add(new MergeSource(infos[i], readers[i].GetPartOf(codec), maps[i], captured[i]));
                    }

                    codec.Merge(new SegmentMergeContext(_schema, name, sources, total, _limits), sink, cancellationToken);
                }

                if (_schema.IdField is not null)
                {
                    var entries = new List<KeyValuePair<byte[], int>>(total);
                    for (int i = 0; i < readers.Count; i++)
                    {
                        IdsReader ids = readers[i].Ids ?? throw new CorruptIndexException(infos[i].Name, "the segment has no ids file");
                        foreach (KeyValuePair<byte[], int> e in ids.ReadAll(cancellationToken))
                        {
                            int mapped = maps[i][e.Value];
                            if (mapped >= 0)
                            {
                                entries.Add(new KeyValuePair<byte[], int>(e.Key, mapped));
                            }
                        }
                    }

                    IdsFormat.Write(sink, entries, cancellationToken);
                }

                result = new SegmentInfo(name, total, 0, 0, sink.Files);
            }

            lock (_gate)
            {
                if (_disposed || _failed is not null)
                {
                    DeleteQuietly(sink);
                }
                else
                {
                    if (result is not null)
                    {
                        List<string> names = result.Files.Select(f => f.FileName).ToList();
                        foreach (string n in names)
                        {
                            _pending.Add(n);
                        }

                        _refs.Acquire(names);
                    }

                    _completedMerges.Add(new MergeResult(name, infos.Select(i => i.Name).ToList(), result, maps, captured));
                }

                _refs.Release(held);
            }
        }
        catch (Exception e)
        {
            DeleteQuietly(sink);
            lock (_gate)
            {
                foreach (SegmentInfo i in infos)
                {
                    _busy.Remove(i.Name);
                }

                if (e is not OperationCanceledException)
                {
                    _mergeError = e;
                }

                _refs.Release(held);
            }
        }
        finally
        {
            foreach (SegmentReader r in readers)
            {
                r.Dispose();
            }
        }
    }

    private void DeleteQuietly(SegmentFileSink? sink)
    {
        if (sink is null)
        {
            return;
        }

        foreach (string n in sink.CreatedNames)
        {
            _refs.TryDelete(n);
        }
    }

    private Action<long>? MakeThrottle(CancellationToken cancellationToken)
    {
        long bytesPerSecond = _options.MaxMergeBytesPerSecond;
        if (bytesPerSecond <= 0)
        {
            return null;
        }

        TimeProvider clock = _options.TimeProvider;
        long start = clock.GetTimestamp();
        long total = 0;
        return bytes =>
        {
            total += bytes;
            TimeSpan due = TimeSpan.FromSeconds((double)total / bytesPerSecond);
            TimeSpan elapsed = clock.GetElapsedTime(start);
            if (due > elapsed)
            {
                _scheduler.Pause(due - elapsed, cancellationToken);
            }
        };
    }

    /// <summary>Replaces merged sources with their result in the working set. Called with the gate held.</summary>
    private void InstallMergesLocked()
    {
        if (_completedMerges.Count == 0)
        {
            return;
        }

        _completedMerges.Sort(static (a, b) => string.CompareOrdinal(a.NewName, b.NewName));
        foreach (MergeResult m in _completedMerges)
        {
            var sources = new List<WorkSegment>(m.SourceNames.Count);
            foreach (string n in m.SourceNames)
            {
                sources.Add(_segments.First(s => string.Equals(s.Info.Name, n, StringComparison.Ordinal)));
            }

            int position = sources.Min(s => _segments.IndexOf(s));
            WorkSegment? merged = null;
            if (m.NewInfo is not null)
            {
                merged = new WorkSegment(m.NewInfo) { IsNew = true };
                // Documents deleted from a source after the merge began were copied; delete them again in the result.
                for (int i = 0; i < sources.Count; i++)
                {
                    ulong[]? now = sources[i].Words;
                    if (now is null)
                    {
                        continue;
                    }

                    for (int d = 0; d < m.Maps[i].MaxDoc; d++)
                    {
                        int to = m.Maps[i][d];
                        if (to >= 0 && (now[d >> 6] >> (d & 63) & 1) != 0 && !m.Captured[i].IsDeleted(d))
                        {
                            MarkDeleted(merged, to);
                        }
                    }
                }
            }

            foreach (WorkSegment s in sources)
            {
                RemoveSegment(_segments.IndexOf(s));
                _busy.Remove(s.Info.Name);
            }

            if (merged is not null)
            {
                _segments.Insert(Math.Min(position, _segments.Count), merged);
            }
        }

        _completedMerges.Clear();
        _changeStamp++;
    }

    private sealed class MergeResult(string newName, List<string> sourceNames, SegmentInfo? newInfo, DocMap[] maps, List<LiveDocs> captured)
    {
        public string NewName { get; } = newName;

        public List<string> SourceNames { get; } = sourceNames;

        public SegmentInfo? NewInfo { get; } = newInfo;

        public DocMap[] Maps { get; } = maps;

        public List<LiveDocs> Captured { get; } = captured;
    }
}
