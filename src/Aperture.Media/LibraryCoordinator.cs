using Aperture.Core.Library;

namespace Aperture.Media;

/// <summary>Everything the library screens need, read once and queried in memory.</summary>
/// <param name="Folders">The library folders.</param>
/// <param name="Groups">Present items, with duplicate copies grouped under the best one.</param>
/// <param name="History">Playback history by path (case-insensitive).</param>
/// <param name="WatchedOverrides">The user's watched marks by path (case-insensitive).</param>
public sealed record LibrarySnapshot(
    IReadOnlyList<LibraryFolder> Folders,
    IReadOnlyList<CopyGroup> Groups,
    IReadOnlyDictionary<string, HistoryEntry> History,
    IReadOnlyDictionary<string, bool> WatchedOverrides)
{
    public static LibrarySnapshot Empty { get; } = new([], [], new Dictionary<string, HistoryEntry>(), new Dictionary<string, bool>());

    /// <summary>True when no folder has produced any item yet.</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>Items in a category (or all), filtered, ordered and searched.</summary>
    public IReadOnlyList<LibraryEntry> Query(MediaCategory? category, WatchFilter filter, LibrarySort sort, string? search) =>
        LibraryQuery.Run(Groups, History, WatchedOverrides, category, filter, sort, search);

    /// <summary>How many present items each category has.</summary>
    public int Count(MediaCategory category) => Groups.Count(g => g.Best.Category == category);

    private Dictionary<string, CopyGroup>? _byPath;

    /// <summary>The group that contains <paramref name="path"/>, if any.</summary>
    public CopyGroup? GroupOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _byPath ??= Groups.SelectMany(g => g.All.Select(i => (i.Path, Group: g)))
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key, p => p.First().Group, StringComparer.OrdinalIgnoreCase);
        return _byPath.TryGetValue(path, out var group) ? group : null;
    }
}

/// <summary>
/// Runs library scans one at a time in the background, then the optional online lookup.
/// Scans are queued (asking twice does not scan twice at once), can be cancelled at any
/// point, and report progress. Events are raised on a background thread.
/// </summary>
public sealed class LibraryCoordinator(
    ILibraryStore store,
    IItemStateStore itemState,
    IPlaybackHistory history,
    Func<IMediaProbe?> probeFactory,
    Func<LibraryLookup?> lookupFactory,
    TimeProvider clock,
    Action<string, string> log) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<long> _pendingFolders = [];
    private bool _pendingAll;
    private CancellationTokenSource? _cancel;
    private Task _worker = Task.CompletedTask;
    private long? _scanningFolder;
    private IMediaProbe? _probe;
    private bool _probeCreated;
    private bool _disposed;

    /// <summary>Progress of the running scan, or null when the library is idle.</summary>
    public event EventHandler<ScanProgress?>? ProgressChanged;

    /// <summary>The library's contents changed (a scan saved new items, or details were looked up).</summary>
    public event EventHandler? Changed;

    /// <summary>Something the user should hear about (a folder that is unplugged, a refused key).</summary>
    public event EventHandler<string>? Notice;

    /// <summary>True while a scan or lookup is running.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return !_worker.IsCompleted;
            }
        }
    }

    /// <summary>The last progress reported, or null when idle.</summary>
    public ScanProgress? Progress { get; private set; }

    /// <summary>Completes when the current queue of scans has finished (for tests and shutdown).</summary>
    public Task WhenIdle()
    {
        lock (_gate)
        {
            return _worker;
        }
    }

    /// <summary>The library folders, with their item counts and last scans.</summary>
    public Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        store.GetFoldersAsync(cancellationToken);

    /// <summary>Adds a folder and starts scanning it.</summary>
    public async Task<LibraryFolder> AddFolderAsync(string path, LibraryFolderKind kind, CancellationToken cancellationToken = default)
    {
        var folder = await store.AddFolderAsync(path, kind, cancellationToken).ConfigureAwait(false);
        RequestScan(folder.Id);
        return folder;
    }

    /// <summary>Forgets a folder and its items (stopping its scan first). Files are not touched.</summary>
    public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default)
    {
        Task running;
        lock (_gate)
        {
            _pendingFolders.Remove(folderId);
            running = _worker;
            if (_scanningFolder == folderId)
            {
                _cancel?.Cancel();
            }
        }

        if (_scanningFolder == folderId)
        {
            await running.ConfigureAwait(false);
        }

        await store.RemoveFolderAsync(folderId, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Queues a scan of one folder, or every folder when <paramref name="folderId"/> is null.</summary>
    public void RequestScan(long? folderId = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (folderId is { } id)
            {
                _pendingFolders.Add(id);
            }
            else
            {
                _pendingAll = true;
            }

            if (_worker.IsCompleted)
            {
                _cancel?.Dispose();
                _cancel = new CancellationTokenSource();
                var token = _cancel.Token;
                _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
        }
    }

    /// <summary>Stops the running scan and drops queued ones. Everything found so far is kept.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _pendingFolders.Clear();
            _pendingAll = false;
            _cancel?.Cancel();
        }
    }

    /// <summary>Reads the library, history and watched marks, and groups copies.</summary>
    public async Task<LibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var folders = await store.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        var items = await store.GetItemsAsync(cancellationToken).ConfigureAwait(false);
        var played = await history.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var marks = await itemState.GetWatchedOverridesAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                var byPath = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in played)
                {
                    byPath.TryAdd(entry.Path, entry);
                }

                var overrides = new Dictionary<string, bool>(marks, StringComparer.OrdinalIgnoreCase);
                return new LibrarySnapshot(folders, CopyGroup.Build(items), byPath, overrides);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks every copy of an item watched (true), unwatched (false), or back to automatic (null).</summary>
    public async Task SetWatchedAsync(IEnumerable<string> paths, bool? watched, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            await itemState.SetWatchedAsync(path, watched, cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves the user's corrections to an item's details.</summary>
    public async Task SaveDetailsAsync(LibraryItem item, ManualDetails details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await store.SaveManualDetailsAsync(item.Id, details, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops corrections and online details and reads the file name again.</summary>
    public async Task ResetDetailsAsync(LibraryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var folders = await store.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        var folder = folders.FirstOrDefault(f => f.Id == item.FolderId);
        var parsed = MediaNameParser.Parse(item.Path, folder?.Path ?? Path.GetDirectoryName(item.Path) ?? item.Path, folder?.Kind ?? LibraryFolderKind.Mixed);
        await store.ResetDetailsAsync(item.Id, parsed, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the online match that was only suggested.</summary>
    public async Task AcceptSuggestionAsync(LibraryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (lookupFactory() is { } lookup)
        {
            await lookup.AcceptSuggestionAsync(item, cancellationToken).ConfigureAwait(false);
        }
        else if (item.SuggestedMatch is { } match)
        {
            // Online details were turned off since: apply the words, without fetching artwork.
            await store.SaveOnlineMatchAsync(item.Id, match with { Confidence = MatchConfidence.Certain }, apply: true, null, cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingFolders.Clear();
            _pendingAll = false;
            _cancel?.Cancel();
            worker = _worker;
        }

        try
        {
            worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        (_probe as IDisposable)?.Dispose();
        _cancel?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        try
        {
            while (true)
            {
                bool all;
                long[] wanted;
                lock (_gate)
                {
                    if (!_pendingAll && _pendingFolders.Count == 0)
                    {
                        break;
                    }

                    all = _pendingAll;
                    wanted = [.. _pendingFolders];
                    _pendingAll = false;
                    _pendingFolders.Clear();
                }

                var folders = (await store.GetFoldersAsync(cancellationToken).ConfigureAwait(false))
                    .Where(f => all || wanted.Contains(f.Id))
                    .ToList();
                foreach (var folder in folders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    changed |= await ScanFolderAsync(folder, cancellationToken).ConfigureAwait(false);
                }
            }

            if (lookupFactory() is { } lookup)
            {
                var result = await lookup.RunAsync(new Reporter(this), cancellationToken).ConfigureAwait(false);
                changed |= result.Applied + result.Suggested + result.NotFound > 0;
                if (result.KeyRejected)
                {
                    Notice?.Invoke(this, "TMDB didn’t accept the API key, so artwork wasn’t looked up. Check the key in Settings.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            log("info", "Library scan cancelled; what was found so far is kept.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            log("error", $"Library scan failed: {error.Message}");
            Notice?.Invoke(this, "The library scan stopped because of a problem. Details are in Aperture’s log.");
        }
        finally
        {
            lock (_gate)
            {
                _scanningFolder = null;
            }

            Progress = null;
            ProgressChanged?.Invoke(this, null);
            if (changed || cancellationToken.IsCancellationRequested)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task<bool> ScanFolderAsync(LibraryFolder folder, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _scanningFolder = folder.Id;
        }

        Report(new ScanProgress(folder.Id, ScanPhase.Finding, 0, null, 0));
        var scanner = new LibraryScanner(store, Probe(), clock, log);
        var summary = await scanner.ScanAsync(folder, new Reporter(this), cancellationToken).ConfigureAwait(false);
        if (summary.FolderUnavailable)
        {
            Notice?.Invoke(this, $"“{Path.GetFileName(folder.Path.TrimEnd('\\', '/'))}” isn’t available right now, so it wasn’t scanned. Its videos stay in your library.");
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return summary.Added + summary.Updated + summary.Missing > 0;
    }

    private IMediaProbe? Probe()
    {
        if (!_probeCreated)
        {
            _probeCreated = true;
            _probe = probeFactory();
        }

        return _probe;
    }

    private void Report(ScanProgress progress)
    {
        Progress = progress;
        ProgressChanged?.Invoke(this, progress);
    }

    private sealed class Reporter(LibraryCoordinator owner) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => owner.Report(value);
    }
}
