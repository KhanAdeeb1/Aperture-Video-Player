using Aperture.Core.Library;
using Aperture.Core.Series;

namespace Aperture.Media.Analysis;

/// <summary>Background analysis progress.</summary>
/// <param name="Done">Files finished in this run.</param>
/// <param name="Total">Files in this run.</param>
/// <param name="Current">The episode being analysed, as the user knows it.</param>
public sealed record AnalysisProgress(int Done, int Total, string? Current);

/// <summary>
/// Finds intros, credits and recaps one file at a time in the background. Each episode's
/// measurements are decoded once and kept in <see cref="AnalysisCache"/>; detection then
/// compares them with the nearest other episodes and saves the result by content fingerprint.
/// The episode being watched (and the one after it) goes first. With background analysis
/// turned off, only episodes the user opens are analysed. Media files are only read.
/// Events are raised on a background thread.
/// </summary>
public sealed class EpisodeAnalysisCoordinator(
    ISeriesStore store,
    Func<IMediaSignals?> signalsFactory,
    AnalysisCache cache,
    TimeProvider clock,
    Action<string, string> log) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<string> _priority = [];
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);
    private SeriesCatalog _catalog = SeriesCatalog.Empty;
    private bool _background = true;
    private bool _replan;
    private bool _running;
    private CancellationTokenSource _cancel = new();
    private Task _worker = Task.CompletedTask;
    private IMediaSignals? _signals;
    private bool _signalsCreated;
    private bool _disposed;

    /// <summary>A file's detected segments were saved (the argument is its fingerprint).</summary>
    public event EventHandler<string>? SegmentsChanged;

    /// <summary>Progress of the current run, or null when idle.</summary>
    public event EventHandler<AnalysisProgress?>? ProgressChanged;

    /// <summary>The last progress reported, or null when idle.</summary>
    public AnalysisProgress? Progress { get; private set; }

    /// <summary>Completes when the queue is empty (for tests and shutdown).</summary>
    public Task WhenIdle()
    {
        lock (_gate)
        {
            return _worker;
        }
    }

    /// <summary>
    /// Takes the library's current shows and queues whatever needs analysing. With
    /// <paramref name="background"/> false, only files passed to <see cref="Prioritize"/> are analysed.
    /// </summary>
    public void Update(SeriesCatalog catalog, bool background)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _catalog = catalog;
            _background = background;
            _replan = true;
            StartWorker();
        }
    }

    /// <summary>Moves a file (and the episode after it) to the front of the queue.</summary>
    public void Prioritize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _priority.Remove(path);
            _priority.Insert(0, path);
            if (_catalog.Next(path) is { } next)
            {
                var following = next.PreferredCopy(path).Path;
                _priority.Remove(following);
                _priority.Insert(1, following);
            }

            _replan = true;
            StartWorker();
        }
    }

    /// <summary>Stops the running analysis and drops the queue. Saved results are kept.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _priority.Clear();
            _background = false;
            _cancel.Cancel();
        }
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
            _priority.Clear();
            _cancel.Cancel();
            worker = _worker;
        }

        try
        {
            worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        (_signals as IDisposable)?.Dispose();
        _cancel.Dispose();
    }

    private void StartWorker()
    {
        // _running (not the task's state) decides, so a request made while the worker is
        // finishing either is seen by it or starts a new one.
        if (_running)
        {
            return;
        }

        _running = true;

        if (_cancel.IsCancellationRequested)
        {
            _cancel.Dispose();
            _cancel = new CancellationTokenSource();
        }

        var token = _cancel.Token;
        _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var queue = new List<AnalysisJob>();
        var done = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SeriesCatalog catalog;
                bool replan;
                string[] priority;
                bool background;
                lock (_gate)
                {
                    catalog = _catalog;
                    replan = _replan;
                    _replan = false;
                    priority = [.. _priority];
                    background = _background;
                }

                if (replan)
                {
                    var records = await store.GetAnalysesAsync(cancellationToken).ConfigureAwait(false);
                    var all = EpisodeAnalysisPlan.Build(catalog, records);
                    var wanted = priority.Select(p => EpisodeAnalysisPlan.For(catalog, p)).OfType<AnalysisJob>()
                        .Where(j => all.Any(a => a.Fingerprint == j.Fingerprint))
                        .ToList();
                    queue = [.. wanted, .. background ? all.Where(a => wanted.All(w => w.Fingerprint != a.Fingerprint)) : []];
                    queue.RemoveAll(j => _unreadable.Contains(j.Fingerprint));
                    lock (_gate)
                    {
                        _priority.Clear();
                    }
                }

                if (queue.Count == 0)
                {
                    lock (_gate)
                    {
                        if (!_replan)
                        {
                            _running = false;
                            break;
                        }
                    }

                    continue;
                }

                var job = queue[0];
                queue.RemoveAt(0);
                Report(new AnalysisProgress(done, done + queue.Count + 1, Describe(catalog, job.Target)));
                await AnalyseAsync(job, cancellationToken).ConfigureAwait(false);
                done++;
            }
        }
        catch (OperationCanceledException)
        {
            log("info", "Intro and credits analysis stopped; finished episodes are kept.");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            log("error", $"Intro and credits analysis failed: {error.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }

            Report(null);
        }
    }

    private async Task AnalyseAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        var target = await SignalsOfAsync(job.Target, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            _unreadable.Add(job.Fingerprint);
            await store.SaveDetectedAsync(new AnalysisRecord(job.Fingerprint, SegmentDetector.Version, job.Signature, clock.GetUtcNow(), AnalysisOutcome.Failed), [], cancellationToken).ConfigureAwait(false);
            SegmentsChanged?.Invoke(this, job.Fingerprint);
            return;
        }

        var peers = new List<EpisodeSignals>();
        foreach (var peer in job.Peers)
        {
            if (await SignalsOfAsync(peer, cancellationToken).ConfigureAwait(false) is { } signals)
            {
                peers.Add(signals);
            }
        }

        var previous = new List<EpisodeSignals>();
        foreach (var before in job.Previous)
        {
            if (await SignalsOfAsync(before, cancellationToken).ConfigureAwait(false) is { } signals)
            {
                previous.Add(signals);
            }
        }

        var segments = await Task.Run(() => SegmentDetector.Detect(target, peers, previous, cancellationToken), cancellationToken).ConfigureAwait(false);
        var outcome = target.Audio is null ? AnalysisOutcome.NoAudio : AnalysisOutcome.Done;
        await store.SaveDetectedAsync(new AnalysisRecord(job.Fingerprint, SegmentDetector.Version, job.Signature, clock.GetUtcNow(), outcome), segments, cancellationToken).ConfigureAwait(false);
        var measured = $"sound {(target.Audio is null ? "none" : "read")}, {(target.Dark is { } dark ? $"{dark.Count} dark stretches" : "no picture")}, {target.Chapters.Count} chapters, compared with {peers.Count}";
        log("info", $"Analysed {Path.GetFileName(job.Target.Path)} ({measured}): {(segments.Count == 0 ? "nothing found" : string.Join(", ", segments.Select(s => $"{s.Kind} {s.Start:hh\\:mm\\:ss}–{s.End:hh\\:mm\\:ss} ({s.Confidence:0.00})")))}");
        SegmentsChanged?.Invoke(this, job.Fingerprint);
    }

    /// <summary>A file's measurements, from the cache or decoded now; null when it cannot be read.</summary>
    private async Task<EpisodeSignals?> SignalsOfAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        var fingerprint = item.Fingerprint!;
        if (_unreadable.Contains(fingerprint))
        {
            return null;
        }

        if (cache.Load(fingerprint) is { } cached)
        {
            return cached;
        }

        if (Signals() is not { } reader)
        {
            throw new InvalidOperationException("libmpv is not available to read episodes.");
        }

        try
        {
            var sound = await reader.ReadAudioAsync(item.Path, TimeSpan.Zero, null, AudioPrint.SampleRate, cancellationToken).ConfigureAwait(false);
            var duration = sound?.Length is { } length && length > TimeSpan.Zero ? length : item.Duration ?? TimeSpan.Zero;
            var print = sound is null ? null : await Task.Run(() => AudioPrint.Compute(sound.Samples, sound.Start), cancellationToken).ConfigureAwait(false);
            var darkFrom = SegmentDetector.TailStart(duration);
            var dark = await reader.FindDarkStretchesAsync(item.Path, darkFrom, cancellationToken).ConfigureAwait(false);
            var chapters = await reader.ReadChaptersAsync(item.Path, cancellationToken).ConfigureAwait(false);
            var signals = new EpisodeSignals(fingerprint, duration, print, darkFrom, dark, chapters);
            try
            {
                cache.Save(signals);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log("warn", $"Could not keep the measurements of {Path.GetFileName(item.Path)}: {error.Message}");
            }

            return signals;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            log("warn", $"Could not analyse {Path.GetFileName(item.Path)}: {error.Message}");
            _unreadable.Add(fingerprint);
            return null;
        }
    }

    private IMediaSignals? Signals()
    {
        if (!_signalsCreated)
        {
            _signalsCreated = true;
            _signals = signalsFactory();
        }

        return _signals;
    }

    private static string Describe(SeriesCatalog catalog, LibraryItem item) =>
        catalog.EpisodeOf(item.Path) is { } episode && catalog.SeriesOf(item.Path) is { } series
            ? $"{series.Title} {episode.Label}"
            : Path.GetFileName(item.Path);

    private void Report(AnalysisProgress? progress)
    {
        Progress = progress;
        ProgressChanged?.Invoke(this, progress);
    }
}
