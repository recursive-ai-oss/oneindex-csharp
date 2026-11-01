using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Index;

public sealed partial class IndexWriter
{
    /// <summary>Segments whose rebuild one commit starts at most, so that a commit stays short and progress is recorded often.</summary>
    private const int ConversionsPerPass = 4;

    /// <summary>A segment rebuilt for the pending schema, written ahead of the swap and not served until the swap.</summary>
    private sealed class Shadow(SegmentInfo info, string source, int sourceDeleted, bool isNew)
    {
        public SegmentInfo Info { get; } = info;

        public string Source { get; } = source;

        public int SourceDeleted { get; } = sourceDeleted;

        /// <summary>Not yet listed by a commit: its files are held by the writer's pending set.</summary>
        public bool IsNew { get; set; } = isNew;
    }

    private Shadow? ShadowOf(WorkSegment segment)
        => _shadows.FirstOrDefault(x => string.Equals(x.Source, segment.Info.Name, StringComparison.Ordinal));

    /// <summary>Drops rebuilt segments whose source changed (it gained deletes, or is gone) since they were built; they are rebuilt again.</summary>
    private void PruneShadowsLocked()
    {
        for (int i = _shadows.Count - 1; i >= 0; i--)
        {
            Shadow x = _shadows[i];
            WorkSegment? source = _segments.FirstOrDefault(s => string.Equals(s.Info.Name, x.Source, StringComparison.Ordinal));
            if (source is null || source.DeletedCount != x.SourceDeleted)
            {
                DropShadowLocked(x);
            }
        }
    }

    private void DropShadowLocked(Shadow x)
    {
        _shadows.Remove(x);
        _metaDirty = true;
        if (x.IsNew)
        {
            List<string> names = x.Info.Files.Select(f => f.FileName).ToList();
            foreach (string n in names)
            {
                _pending.Remove(n);
            }

            _refs.Release(names);
        }

        // A listed shadow's files are held by the commit that lists it and go when the next commit no longer does.
    }

    /// <summary>
    /// Makes the rebuilt segments the served ones when every segment has a current rebuilt twin and nothing is in flight. The swap is
    /// part of the commit that follows, so a reader sees all of the old schema or all of the new one.
    /// </summary>
    private void TrySwapLocked()
    {
        if (_target is not { } target || _busy.Count != 0 || _completedMerges.Count != 0)
        {
            return;
        }

        var order = new List<Shadow>(_segments.Count);
        foreach (WorkSegment s in _segments)
        {
            Shadow? x = ShadowOf(s);
            if (x is null)
            {
                return;
            }

            order.Add(x);
        }

        for (int i = _segments.Count - 1; i >= 0; i--)
        {
            RemoveSegment(i);
        }

        _schemaEpoch++;
        foreach (Shadow x in order)
        {
            _segments.Add(new WorkSegment(x.Info) { IsNew = x.IsNew });
        }

        _shadows.Clear();
        _schema = target;
        _target = null;
        _metaDirty = true;
    }

    /// <summary>
    /// Rebuilds, in the background, every segment for the schema the application declared and commits as it goes. The rebuilt segments
    /// are written ahead and listed by commits so that a restart resumes, but they are not served: queries keep seeing the old schema
    /// on every segment until the commit that makes the new one visible on all of them (docs/schema-evolution.md). Returns at once when
    /// nothing is pending. With the default cooperative scheduler the work runs on the calling thread.
    /// </summary>
    /// <param name="cancellationToken">Cancels between segments; what was committed stays.</param>
    /// <exception cref="IOException">A rebuild failed; the index is unchanged by it.</exception>
    public void CompleteSchemaEvolution(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                ThrowIfUnusable();
                if (_target is null)
                {
                    return;
                }

                StartConversionsLocked(int.MaxValue);
            }

            WaitForMerges(cancellationToken);
            Commit(cancellationToken);
        }
    }

    // Runs the rebuild to its end and makes the swap in memory only: nothing is committed on the caller's behalf, so what readers see
    // changes only at the commit the application asks for. A crash before that commit leaves the last commit, with its listed twins.
    private void FinishRebuildBeforeWrite(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                ThrowIfUnusable();
                if (_target is null)
                {
                    return;
                }

                InstallMergesLocked();
                DropEmptySegmentsLocked();
                PruneShadowsLocked();
                TrySwapLocked();
                if (_target is null)
                {
                    return;
                }

                StartConversionsLocked(int.MaxValue);
            }

            WaitForMerges(cancellationToken);
        }
    }

    /// <summary>Rebuilds segments for a pending schema change (see <see cref="CompleteSchemaEvolution(CancellationToken)"/>).</summary>
    /// <param name="cancellationToken">Cancels between segments.</param>
    /// <returns>A completed task.</returns>
    public ValueTask CompleteSchemaEvolutionAsync(CancellationToken cancellationToken = default)
    {
        CompleteSchemaEvolution(cancellationToken);
        return ValueTask.CompletedTask;
    }

    private void MaybeBackfillLocked() => StartConversionsLocked(ConversionsPerPass);

    private void StartConversionsLocked(int limit)
    {
        if (_target is null)
        {
            return;
        }

        int started = 0;
        foreach (WorkSegment s in _segments.ToList())
        {
            if (started >= limit)
            {
                return;
            }

            if (!_busy.Contains(s.Info.Name) && ShadowOf(s) is null && s.Info.DocCount > s.DeletedCount)
            {
                StartConversionLocked(s);
                started++;
            }
        }
    }

    // Queues the rebuild of one segment. It reads the segment's live documents from its stored source and flushes them again under the
    // pending schema. The result is kept beside the source, not in place of it (see TrySwapLocked).
    private void StartConversionLocked(WorkSegment source)
    {
        string name = NewSegmentName();
        _busy.Add(source.Info.Name);
        var held = source.Info.Files.Select(f => f.FileName).ToList();
        LiveDocs captured = source.Words is not null
            ? LiveDocs.FromWords(source.Info.DocCount, (ulong[])source.Words.Clone())
            : SegmentReader.LoadLiveDocs(_storage, source.Info, _limits);
        SegmentInfo info = source.Info;
        _refs.Acquire(held);
        _scheduler.Enqueue(ct => RunConversion(name, info, captured, held, ct));
    }

    private void RunConversion(string name, SegmentInfo info, LiveDocs captured, List<string> held, CancellationToken cancellationToken)
    {
        SegmentReader? reader = null;
        SegmentFileSink? sink = null;
        try
        {
            reader = SegmentReader.Open(_storage, _schema, info, _codecs, _limits, captured, cancellationToken);
            var map = new DocMap(captured, info.DocCount, 0);
            int total = map.LiveCount;
            SegmentInfo? result = null;
            if (total > 0)
            {
                IStoredFieldsReader stored = reader.GetPart<IStoredFieldsSource>()?.StoredFields
                    ?? throw new InvalidOperationException($"Segment {info.Name} has no stored source, so it cannot be rebuilt for the new schema.");
                Schema target;
                lock (_gate)
                {
                    target = _target ?? _schema;
                }

                var documents = new List<Document>(total);
                for (int d = 0; d < info.DocCount; d++)
                {
                    if ((d & 255) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (captured.IsLive(d))
                    {
                        documents.Add(Project(stored.GetDocument(d), target));
                    }
                }

                IReadOnlyList<Document> expanded;
                Schema flushSchema;
                lock (_gate)
                {
                    expanded = DynamicFields.Expand(_target ?? _schema, documents, _limits, out flushSchema);
                    if (_target is not null)
                    {
                        _target = flushSchema;
                    }
                }

                sink = new SegmentFileSink(_storage, name, MakeThrottle(cancellationToken));
                var context = new SegmentFlushContext(flushSchema, name, expanded, _limits);
                foreach (ISegmentCodec codec in _codecs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    codec.Flush(context, sink, cancellationToken);
                }

                if (flushSchema.IdField is { } idField)
                {
                    var entries = new List<KeyValuePair<byte[], int>>(total);
                    for (int i = 0; i < documents.Count; i++)
                    {
                        entries.Add(new KeyValuePair<byte[], int>(System.Text.Encoding.UTF8.GetBytes(documents[i].Get(idField).AsText()), i));
                    }

                    IdsFormat.Write(sink, entries, cancellationToken);
                }

                result = new SegmentInfo(name, total, 0, 0, sink.Files, _schemaEpoch + 1);
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

                    if (result is not null)
                    {
                        _shadows.Add(new Shadow(result, info.Name, info.DocCount - total, isNew: true));
                        _metaDirty = true;
                    }

                    _busy.Remove(info.Name);
                }

                _refs.Release(held);
            }
        }
        catch (Exception e)
        {
            DeleteQuietly(sink);
            lock (_gate)
            {
                _busy.Remove(info.Name);
                if (e is not OperationCanceledException)
                {
                    _mergeError = e;
                }

                _refs.Release(held);
            }
        }
        finally
        {
            reader?.Dispose();
        }
    }

    // A stored document restricted to the fields the schema still has; values keep their kinds (a whole number is a valid float).
    private static Document Project(Document stored, Schema schema)
    {
        var projected = new Document();
        foreach (DocumentField f in stored.Fields)
        {
            if (schema.TryGetField(f.Name, out FieldDefinition? def) && def.DynamicParent is null)
            {
                projected.Add(f.Name, f.Value);
            }
        }

        return projected;
    }
}
