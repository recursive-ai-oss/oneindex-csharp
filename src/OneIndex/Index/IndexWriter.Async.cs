namespace OneIndex.Index;

// Asynchronous twins of the operations that can touch storage. As with CommitAsync they run on the calling thread and complete
// synchronously on local and memory storage; they exist so that code written against async storage reads the same at every layer.
public sealed partial class IndexWriter
{
    /// <summary>Adds or replaces a document (see <see cref="Upsert(Document, CancellationToken)"/>).</summary>
    /// <param name="document">The document.</param>
    /// <param name="cancellationToken">Cancels a flush this call triggers.</param>
    /// <returns>A task that completes when the document is buffered.</returns>
    public ValueTask UpsertAsync(Document document, CancellationToken cancellationToken = default)
    {
        Upsert(document, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>Adds a document that must not replace one with the same identifier (see <see cref="AddUnique(Document, CancellationToken)"/>).</summary>
    /// <param name="document">The document.</param>
    /// <param name="cancellationToken">Cancels a flush this call triggers.</param>
    /// <returns>A task that completes when the document is buffered.</returns>
    public ValueTask AddUniqueAsync(Document document, CancellationToken cancellationToken = default)
    {
        AddUnique(document, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>Deletes the documents with a term (see <see cref="DeleteByTerm(string, string, CancellationToken)"/>).</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The term.</param>
    /// <param name="cancellationToken">Cancels the search for the documents.</param>
    /// <returns>How many documents were deleted.</returns>
    public ValueTask<int> DeleteByTermAsync(string field, string value, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(DeleteByTerm(field, value, cancellationToken));

    /// <summary>Merges segments until at most the given number remain (see <see cref="ForceMerge(int, CancellationToken)"/>).</summary>
    /// <param name="maxSegments">The most segments left, at least 1. Default 1.</param>
    /// <param name="cancellationToken">Cancels between merges.</param>
    /// <returns>A task that completes when the merges finish.</returns>
    public ValueTask ForceMergeAsync(int maxSegments = 1, CancellationToken cancellationToken = default)
    {
        ForceMerge(maxSegments, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>Opens a reader that sees uncommitted changes (see <see cref="OpenNearRealTimeReader(CancellationToken)"/>).</summary>
    /// <param name="cancellationToken">Cancels the flush the reader needs.</param>
    /// <returns>The reader.</returns>
    public ValueTask<IndexReader> OpenNearRealTimeReaderAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(OpenNearRealTimeReader(cancellationToken));

    /// <summary>Opens a snapshot that sees uncommitted changes (see <see cref="OpenNearRealTimeSnapshot(CancellationToken)"/>).</summary>
    /// <param name="cancellationToken">Cancels the flush the snapshot needs.</param>
    /// <returns>The snapshot.</returns>
    public ValueTask<IndexSnapshot> OpenNearRealTimeSnapshotAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(OpenNearRealTimeSnapshot(cancellationToken));
}
