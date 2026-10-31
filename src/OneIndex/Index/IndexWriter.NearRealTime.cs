namespace OneIndex.Index;

public sealed partial class IndexWriter
{
    /// <summary>
    /// Opens a reader that shows this writer's flushed and deleted-but-uncommitted changes. It is for search-as-you-write inside the
    /// writer's process and is <b>not durable</b>: what it shows is lost if the process stops before <see cref="Commit(CancellationToken)"/>, and
    /// nothing outside this process can see it. Each refresh flushes buffered documents into a new small segment (merges tidy them
    /// at commit), so refresh as often as the application needs freshness and no more.
    /// </summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>A reader whose <see cref="IndexReader.IsDurable"/> is <see langword="false"/>. Dispose it before the writer.</returns>
    public IndexReader OpenNearRealTimeReader(CancellationToken cancellationToken = default)
    {
        IndexSnapshot first = OpenNearRealTimeSnapshot(cancellationToken);
        return IndexReader.Create(first, (current, ct) =>
        {
            lock (_gate)
            {
                ThrowIfUnusable();
                if (current.Stamp == _changeStamp && _bufferLive == 0)
                {
                    return null;
                }
            }

            return OpenNearRealTimeSnapshot(ct);
        });
    }

    /// <summary>One near-real-time snapshot (see <see cref="OpenNearRealTimeReader"/>); its files stay until it is disposed.</summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>The snapshot; <see cref="IndexSnapshot.IsDurable"/> is <see langword="false"/>.</returns>
    public IndexSnapshot OpenNearRealTimeSnapshot(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            FlushLocked(cancellationToken);
            var infos = new List<SegmentInfo>(_segments.Count);
            var live = new LiveDocs?[_segments.Count];
            var names = new List<string>();
            for (int i = 0; i < _segments.Count; i++)
            {
                WorkSegment s = _segments[i];
                SegmentInfo info = s.Info;
                if (s.Words is not null)
                {
                    // Copy the bitmap: the writer keeps changing its own.
                    live[i] = LiveDocs.FromWords(info.DocCount, (ulong[])s.Words.Clone());
                    info = new SegmentInfo(info.Name, info.DocCount, live[i]!.DeletedCount, info.DeleteGeneration, info.Files);
                }
                else
                {
                    live[i] = SegmentReader.LoadLiveDocs(_storage, info, _limits, cancellationToken);
                }

                infos.Add(info);
                foreach (SegmentFile f in info.Files)
                {
                    names.Add(f.FileName);
                }
            }

            ReaderLease? lease = IndexLocks.AcquireViewLease(_storage, _current?.Generation ?? 0, cancellationToken);
            var view = CommitInfo.NearRealTimeView(_current?.Generation ?? 0, _nextSegmentId, infos, _schema.Serialize(), _appliedPosition, _userData);
            _refs.Acquire(names);
            return new IndexSnapshot(_storage, view, _schema, _codecs, _limits, () =>
            {
                lease?.Dispose();
                _refs.Release(names);
            }, live, _changeStamp);
        }
    }
}
