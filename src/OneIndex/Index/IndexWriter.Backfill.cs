using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Index;

public sealed partial class IndexWriter
{
    /// <summary>Segments whose rebuild one commit starts at most, so that a commit stays short and progress is recorded often.</summary>
    private const int ConversionsPerPass = 4;

    private bool IsStale(WorkSegment segment) => segment.Info.SchemaEpoch < _schemaEpoch;

    /// <summary>
    /// Rebuilds, in the background, every segment written before the last schema change that needed it, and commits as it goes. Each
    /// rebuilt segment replaces its original in one commit, so a crash at any point leaves an index that opens, holds every document
    /// and either has or has not yet rebuilt that segment; the next open continues (docs/schema-evolution.md). Returns at once when
    /// nothing is waiting. With the default cooperative scheduler the work runs on the calling thread.
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
                if (!_segments.Any(IsStale))
                {
                    return;
                }

                StartConversionsLocked(int.MaxValue);
            }

            WaitForMerges(cancellationToken);
            Commit(cancellationToken);
        }
    }

    /// <summary>Rebuilds segments awaiting a schema change (see <see cref="CompleteSchemaEvolution(CancellationToken)"/>).</summary>
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
        int started = 0;
        foreach (WorkSegment s in _segments.ToList())
        {
            if (started >= limit)
            {
                return;
            }

            if (IsStale(s) && !_busy.Contains(s.Info.Name))
            {
                StartConversionLocked(s);
                started++;
            }
        }
    }

    // Queues the rebuild of one segment. It reads the segment's live documents from its stored source and flushes them again under the
    // current schema; installing the result is the same step as installing a merge, so deletes made meanwhile are carried over.
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
                Schema target = _schema;
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
                    expanded = DynamicFields.Expand(_schema, documents, _limits, out flushSchema);
                    _schema = flushSchema;
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

                result = new SegmentInfo(name, total, 0, 0, sink.Files, _schemaEpoch);
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

                    _completedMerges.Add(new MergeResult(name, [info.Name], result, [map], [captured]));
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
