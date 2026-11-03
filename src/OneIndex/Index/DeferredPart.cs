namespace OneIndex.Index;

/// <summary>
/// A part reader created on first use. If creating it throws (for example because a chunk of a static index has not been fetched yet)
/// the exception propagates and nothing is remembered, so the next use creates it again.
/// </summary>
/// <typeparam name="T">The real reader.</typeparam>
internal sealed class DeferredPart<T>(Func<T?> create) : IDisposable
    where T : class, IDisposable
{
    private readonly Lock _gate = new();
    private T? _value;
    private bool _created;
    private bool _disposed;

    /// <summary>The reader, or <see langword="null"/> if the segment has none.</summary>
    public T? Value
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_created)
                {
                    _value = create();
                    _created = true;
                }

                return _value;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _value?.Dispose();
            _value = null;
        }
    }
}
