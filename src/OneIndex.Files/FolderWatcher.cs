namespace OneIndex.Files;

/// <summary>
/// Keeps a passage index current while the folder changes. It indexes once when started, then reruns the indexer a moment after the
/// file system reports a change (changes arriving close together run once), and every <c>rescanEvery</c> regardless, because a file system
/// watcher can miss events (queue overflow, network shares, the platform's watch limits). A rerun only reads files whose size or time
/// changed, so an idle folder costs one directory walk.
/// </summary>
/// <remarks>
/// The work runs on one background thread at below-normal priority and is the only writer of the index while the watcher is alive: do not
/// write to the index yourself in the meantime, and dispose the watcher before the index. Searches from other threads are fine; they see each
/// commit as it lands.
/// </remarks>
public sealed class FolderWatcher : IDisposable
{
    private readonly SearchIndex<FilePassage> _index;
    private readonly string _root;
    private readonly FolderIndexOptions _options;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _rescanEvery;
    private readonly string[] _excluded;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0);
    private FileSystemWatcher? _watcher;
    private Task? _loop;
    private long _requested;
    private long _completed;
    private long _runs;
    private int _running;

    /// <summary>Creates a watcher; nothing happens until <see cref="Start"/>.</summary>
    /// <param name="index">The passage index; the watcher becomes its only writer.</param>
    /// <param name="root">The folder.</param>
    /// <param name="options">Indexing settings; null for the defaults.</param>
    /// <param name="debounce">How long to wait after a change for more changes. Default 2 seconds.</param>
    /// <param name="rescanEvery">The interval of the safety rescan. Default 10 minutes.</param>
    public FolderWatcher(SearchIndex<FilePassage> index, string root, FolderIndexOptions? options = null, TimeSpan? debounce = null, TimeSpan? rescanEvery = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(root);
        _index = index;
        _root = Path.GetFullPath(root);
        _options = options ?? new FolderIndexOptions();
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _rescanEvery = rescanEvery ?? TimeSpan.FromMinutes(10);
        _excluded = [.. _options.ExcludeDirectories.Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))];
    }

    /// <summary>Raised on the watcher's thread after each run, with what it changed.</summary>
    public event Action<FolderIndexResult>? Scanned;

    /// <summary>Raised on the watcher's thread when a run fails or the file system watcher reports an error; the watcher keeps going and retries at the next change or rescan.</summary>
    public event Action<Exception>? Failed;

    /// <summary>How many runs have finished.</summary>
    public long Runs => Interlocked.Read(ref _runs);

    /// <summary>Starts the first run and the watching.</summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The watcher is already started.");
        }

        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += OnEvent;
        _watcher.Changed += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += (_, e) => OnPath(e.FullPath);
        _watcher.Error += (_, e) =>
        {
            Failed?.Invoke(e.GetException());
            Request(); // events may have been lost: rescan
        };
        _watcher.EnableRaisingEvents = true;
        Request();
        _loop = Task.Factory.StartNew(() => LoopAsync(_stop.Token), _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Completes when every change seen so far has been indexed and committed.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _running) != 0 || Interlocked.Read(ref _completed) < Interlocked.Read(ref _requested))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Asks for a run now, without waiting for the file system or the interval.</summary>
    public void Rescan() => Request();

    private void OnEvent(object? sender, FileSystemEventArgs e) => OnPath(e.FullPath);

    private void OnPath(string path)
    {
        foreach (string x in _excluded)
        {
            if (path.StartsWith(x, StringComparison.Ordinal) && (path.Length == x.Length || path[x.Length] == Path.DirectorySeparatorChar))
            {
                return;
            }
        }

        Request();
    }

    private void Request()
    {
        Interlocked.Increment(ref _requested);
        _signal.Release();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException)
        {
            // Not every platform lets a thread lower its own priority; the work then runs at normal priority.
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Task signalled = _signal.WaitAsync(ct);
                Task timer = Task.Delay(_rescanEvery, _options.TimeProvider, ct);
                Task first = await Task.WhenAny(signalled, timer).ConfigureAwait(false);
                if (first == timer)
                {
                    Interlocked.Increment(ref _requested); // the periodic safety rescan
                }

                // Let a burst of changes settle, then take everything that arrived.
                await Task.Delay(_debounce, _options.TimeProvider, ct).ConfigureAwait(false);
                while (_signal.Wait(0, ct))
                {
                }

                long upTo = Interlocked.Read(ref _requested);
                Volatile.Write(ref _running, 1);
                try
                {
                    FolderIndexResult r = FolderIndexer.Run(_index, _root, _options, null, ct);
                    Interlocked.Increment(ref _runs);
                    Scanned?.Invoke(r);
                }
                finally
                {
                    Volatile.Write(ref _running, 0);
                }

                Interlocked.Exchange(ref _completed, upTo);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Failed?.Invoke(e);
                Interlocked.Exchange(ref _completed, Interlocked.Read(ref _requested));
            }
        }
    }

    /// <summary>Stops watching and waits for a run in progress to finish its current file and commit.</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _stop.Cancel();
        try
        {
            _loop?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _signal.Dispose();
    }
}
