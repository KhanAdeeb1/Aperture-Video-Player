using System.Diagnostics;
using Aperture.Core.Casting;
using Aperture.Core.Library;
using Aperture.Core.Media;
using Aperture.Core.Server;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Casting;

/// <summary>
/// One cast: sends the file to the TV, watches it once a second, and is the only thing that
/// records this file's position while it lasts (the desktop player pauses and stops saving).
/// A TV that stops answering is retried for a while without touching the saved position; a TV
/// that can't play the original gets a converted stream once; positions the TV reports are only
/// saved when <see cref="CastPositionTracker"/> trusts them.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "A finished session may still be asked for its status or get a late command; its semaphore and token source hold no native handles (no wait handle or timer is used), so they are left to the collector.")]
internal sealed class CastSession
{
    private static readonly TimeSpan CommandSettle = TimeSpan.FromSeconds(5);

    private readonly CastManager _manager;
    private readonly CastStartRequest _request;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly CastPositionTracker _tracker;

    private Task _loop = Task.CompletedTask;
    private CastStream? _stream;
    private SourceMedia? _source;
    private RendererFormats _formats = RendererFormats.Unknown;
    private bool _canConvert;
    private bool _triedConversion;
    private bool _started;
    private bool _finished;
    private double? _pendingSeek;
    private double? _firstReading;
    private TimeSpan? _playingSince;
    private TimeSpan _playSentAt;
    private TimeSpan _commandAt = TimeSpan.MinValue;
    private TimeSpan _lastSaveAt;
    private double? _lastSaved;
    private int _failures;
    private TimeSpan? _unansweredSince;
    private CastState _state = CastState.Connecting;
    private string? _message;

    public CastSession(CastManager manager, UpnpRenderer renderer, CastStartRequest request)
    {
        _manager = manager;
        Renderer = renderer;
        _request = request;
        _tracker = new CastPositionTracker(Math.Max(0, request.StartSeconds), request.DurationSeconds);
        Id = Guid.NewGuid().ToString("N");
        Publish();
    }

    public string Id { get; }

    public UpnpRenderer Renderer { get; }

    public CastStatus Status { get; private set; } = CastStatus.None;

    public bool IsActive => CastStatus.IsActiveState(Status.State);

    private CastTimings Timings => _manager.Timings;

    private ILogger Log => _manager.Log;

    private CancellationToken Stopping => _stop.Token;

    /// <summary>Starts the cast in the background; <see cref="Status"/> follows it.</summary>
    public void Begin() => _loop = Task.Run(RunAsync);

    public async Task<bool> PlayAsync() => await CommandAsync(async ct =>
    {
        await Renderer.PlayAsync(ct).ConfigureAwait(false);
        return true;
    }).ConfigureAwait(false);

    public async Task<bool> PauseAsync() => await CommandAsync(async ct =>
    {
        await Renderer.PauseAsync(ct).ConfigureAwait(false);
        await SaveAsync(final: false).ConfigureAwait(false);
        return true;
    }).ConfigureAwait(false);

    /// <summary>Moves to <paramref name="seconds"/>: a seek on the TV for the original file, a new stream for a converted one.</summary>
    public async Task<bool> SeekAsync(double seconds) => await CommandAsync(async ct =>
    {
        if (!double.IsFinite(seconds) || _stream is not { } stream)
        {
            return false;
        }

        var target = Math.Max(0, _tracker.DurationSeconds > 0 ? Math.Min(seconds, _tracker.DurationSeconds - 1) : seconds);
        if (stream.Plan.IsDirect)
        {
            await Renderer.SeekAsync(target, ct).ConfigureAwait(false);
            _tracker.Restart(target, 0);
        }
        else
        {
            await LoadAsync(stream.Plan, target, ct).ConfigureAwait(false);
        }

        return true;
    }).ConfigureAwait(false);

    /// <summary>Ends the cast: saves the last trusted position, optionally stops the TV, and retires the media addresses.</summary>
    public async Task StopAsync(CastState state, string? message, bool sendStop)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await FinishAsync(state, message, sendStop).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loop ends by cancellation.
        }
    }

    private async Task<bool> CommandAsync(Func<CancellationToken, Task<bool>> action)
    {
        if (_finished)
        {
            return false;
        }

        try
        {
            await _gate.WaitAsync(Stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // the cast is ending
        }

        try
        {
            if (_finished)
            {
                return false;
            }

            _commandAt = _clock.Elapsed;
            var done = await action(Stopping).ConfigureAwait(false);
            Publish();
            return done;
        }
        catch (Exception ex) when (ex is UpnpFaultException or RendererUnreachableException)
        {
            Log.LogWarning("The TV refused a command: {Message}", ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await _gate.WaitAsync(Stopping).ConfigureAwait(false);
            try
            {
                if (!await PrepareAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }

            while (!Stopping.IsCancellationRequested)
            {
                await Task.Delay(_state == CastState.Reconnecting ? Timings.Poll * 3 : Timings.Poll, Stopping).ConfigureAwait(false);
                await _gate.WaitAsync(Stopping).ConfigureAwait(false);
                try
                {
                    if (_finished || !await PollAsync().ConfigureAwait(false))
                    {
                        return;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (Stopping.IsCancellationRequested)
        {
            // Stopped from Aperture.
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Casting stopped because of an unexpected error");
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await FinishAsync(CastState.Failed, "Casting stopped because of an error. Details are in the media server's log.", sendStop: true).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>Decides how to send the file and sends it. False when the cast ended here.</summary>
    private async Task<bool> PrepareAsync()
    {
        try
        {
            _formats = await Renderer.GetFormatsAsync(Stopping).ConfigureAwait(false);
        }
        catch (RendererUnreachableException)
        {
            await FinishAsync(CastState.Failed, $"{Renderer.Info.Name} didn't answer. Check that it's switched on and on the same network.", sendStop: false).ConfigureAwait(false);
            return false;
        }

        var settings = await _manager.Server.SettingsAsync(Stopping).ConfigureAwait(false);
        var (install, _) = await _manager.Server.Ffmpeg.FindAsync(settings.FfmpegPath, Stopping).ConfigureAwait(false);
        _canConvert = install is not null;
        _source = install is null ? null : await _manager.Server.Ffmpeg.InspectAsync(install, _request.Path, Stopping).ConfigureAwait(false);
        if (_request.DurationSeconds <= 0 && _source?.Duration is { } length)
        {
            _tracker.OfferDuration(length.TotalSeconds);
        }

        var plan = TvStreamPlanner.Decide(_source, Path.GetExtension(_request.Path), _formats, _canConvert);
        if (plan.Plan.Mode == StreamMode.Unavailable)
        {
            await FinishAsync(CastState.Failed, plan.Plan.Reason, sendStop: false).ConfigureAwait(false);
            return false;
        }

        try
        {
            await LoadAsync(plan, Math.Max(0, _request.StartSeconds), Stopping).ConfigureAwait(false);
        }
        catch (RendererUnreachableException)
        {
            await FinishAsync(CastState.Failed, $"{Renderer.Info.Name} stopped answering.", sendStop: false).ConfigureAwait(false);
            return false;
        }
        catch (UpnpFaultException fault)
        {
            Log.LogWarning("The TV refused the video: {Message}", fault.Message);
            if (!await FallBackAsync().ConfigureAwait(false))
            {
                await FinishAsync(CastState.Failed, CantPlay(), sendStop: false).ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    /// <summary>Gives the TV a new address for the file, starting at <paramref name="at"/>, and presses Play.</summary>
    private async Task LoadAsync(TvPlan plan, double at, CancellationToken cancellationToken)
    {
        var offset = plan.IsDirect ? 0 : at;
        var previous = _stream;
        var stream = _manager.AddStream(this, plan, offset);
        _tokens.Add(stream.Token);
        _stream = stream;
        if (previous is not null)
        {
            _manager.RemoveStream(previous.Token);
        }

        var local = CastManager.LocalAddressFor(stream.Renderer);
        var host = local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{local}]" : local.ToString();
        var name = plan.IsDirect ? "media" + Path.GetExtension(_request.Path).ToLowerInvariant() : "stream.ts";
        var media = new Uri($"http://{host}:{_manager.Server.SetupPort}/cast/{stream.Token}/{name}");
        var video = _source?.VideoCodec is not null || (_source is null && MediaFileTypes.IsVideoFile(_request.Path));
        var metadata = UpnpRenderer.Metadata(_request.Title, media, plan.Mime, plan.IsDirect ? CastManager.DirectFeatures : CastManager.StreamFeatures, _tracker.DurationSeconds - offset, video);

        try
        {
            await Renderer.SetUriAsync(media, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (UpnpFaultException)
        {
            // Some TVs only take a new video once stopped.
            await Renderer.StopAsync(cancellationToken).ConfigureAwait(false);
            await Renderer.SetUriAsync(media, metadata, cancellationToken).ConfigureAwait(false);
        }

        await Renderer.PlayAsync(cancellationToken).ConfigureAwait(false);
        _playSentAt = _clock.Elapsed;
        _started = false;
        _firstReading = null;
        _playingSince = null;
        _pendingSeek = plan.IsDirect && at >= 1 ? at : null;
        _tracker.Restart(at, offset);
        _state = CastState.Connecting;
        _message = null;
        Log.LogInformation("Sent {Mode} to the TV from {Start:0}s", plan.Plan.Mode, at);
        Publish();
    }

    /// <summary>One look at the TV. False when the cast has ended.</summary>
    private async Task<bool> PollAsync()
    {
        TransportInfo transport;
        PositionInfo position;
        try
        {
            transport = await Renderer.GetTransportInfoAsync(Stopping).ConfigureAwait(false);
            try
            {
                position = await Renderer.GetPositionInfoAsync(Stopping).ConfigureAwait(false);
            }
            catch (UpnpFaultException)
            {
                position = new PositionInfo(null, null, null); // some TVs refuse while loading
            }
        }
        catch (RendererUnreachableException)
        {
            return await NotAnsweringAsync().ConfigureAwait(false);
        }
        catch (UpnpFaultException fault)
        {
            Log.LogWarning("The TV refused a status request: {Message}", fault.Message);
            return await NotAnsweringAsync().ConfigureAwait(false);
        }

        if (_unansweredSince is not null)
        {
            Log.LogInformation("The TV is answering again");
        }

        _failures = 0;
        _unansweredSince = null;
        var now = _clock.Elapsed;
        var state = transport.ToCastState();

        // Someone else (the TV's own menu, a phone app) is playing something on it now.
        if (position.TrackUri is { Length: > 0 } uri && state != CastState.Stopped && !_tokens.Any(t => uri.Contains(t, StringComparison.Ordinal)))
        {
            await FinishAsync(CastState.Stopped, $"{Renderer.Info.Name} started playing something else, so casting ended. Your place is saved.", sendStop: false).ConfigureAwait(false);
            return false;
        }

        // A TV still reporting the stream it played before a seek isn't reporting this one.
        var reading = position.TrackUri is { Length: > 0 } track && _stream is { } current && !track.Contains(current.Token, StringComparison.Ordinal)
            ? null
            : position.RelativeSeconds;

        if (!_started)
        {
            if (state is CastState.Playing or CastState.Paused && _pendingSeek is { } at)
            {
                _pendingSeek = null;
                try
                {
                    await Renderer.SeekAsync(at, Stopping).ConfigureAwait(false);
                    _commandAt = _clock.Elapsed;
                }
                catch (UpnpFaultException fault)
                {
                    Log.LogInformation("The TV didn't accept a starting point: {Message}", fault.Message);
                    _message = $"{Renderer.Info.Name} doesn't accept a starting point, so it started from the beginning.";
                }
            }

            // Started means really playing: the position moves (a TV can say Playing while it
            // still has nothing to show), or the TV gives no position at all for a while.
            if (state == CastState.Playing)
            {
                _playingSince ??= now;
                if (reading is { } r && _firstReading is { } first && Math.Abs(r - first) >= 0.5)
                {
                    _started = true;
                }

                _firstReading ??= reading;
                if (reading is null && position.RelativeSeconds is null && now - _playingSince.Value > Timings.StartGrace)
                {
                    _started = true;
                }
            }
            else if (state == CastState.Paused)
            {
                _started = true; // paused on the TV itself
            }

            if (!_started)
            {
                if (transport.HasError
                    || (state == CastState.Stopped && now - _playSentAt > Timings.StartGrace)
                    || now - _playSentAt > Timings.StartTimeout)
                {
                    if (await FallBackAsync().ConfigureAwait(false))
                    {
                        return true;
                    }

                    await FinishAsync(CastState.Failed, CantPlay(), sendStop: true).ConfigureAwait(false);
                    return false;
                }

                _state = state == CastState.Stopped ? CastState.Connecting : CastState.Buffering;
                Publish();
                return true;
            }
        }

        // Stopped after it had started: the end, the TV's own Stop, or an error.
        if (state == CastState.Stopped)
        {
            if (now - _commandAt < CommandSettle)
            {
                state = CastState.Buffering; // some TVs pass through Stopped while seeking
            }
            else if (_tracker.NearEnd)
            {
                await FinishAsync(CastState.Ended, null, sendStop: false).ConfigureAwait(false);
                return false;
            }
            else
            {
                await FinishAsync(
                    CastState.Stopped,
                    transport.HasError ? $"{Renderer.Info.Name} stopped with an error. Your place is saved." : $"Stopped on {Renderer.Info.Name}. Your place is saved.",
                    sendStop: false).ConfigureAwait(false);
                return false;
            }
        }

        _tracker.OfferDuration(position.DurationSeconds);
        _tracker.Observe(state, reading, now.TotalSeconds);
        var paused = state == CastState.Paused && _state != CastState.Paused;
        _state = state;
        if (state is CastState.Playing or CastState.Buffering)
        {
            _manager.Server.Activity.Record(CastManager.ActivityId);
        }

        if (paused || now - _lastSaveAt >= Timings.SaveEvery)
        {
            await SaveAsync(final: false).ConfigureAwait(false);
        }

        Publish();
        return true;
    }

    private async Task<bool> NotAnsweringAsync()
    {
        _failures++;
        var now = _clock.Elapsed;
        if (_failures < 3)
        {
            return true;
        }

        if (_unansweredSince is null)
        {
            _unansweredSince = now;
            _state = CastState.Reconnecting;
            _message = $"{Renderer.Info.Name} stopped answering. Aperture keeps trying, and your place is saved.";
            Log.LogWarning("The TV stopped answering; keeping the last position and trying again");
            await SaveAsync(final: false).ConfigureAwait(false);
            Publish();
            return true;
        }

        if (now - _unansweredSince.Value > Timings.GiveUp)
        {
            await FinishAsync(CastState.Disconnected, $"{Renderer.Info.Name} stopped answering, so casting ended. Your place is saved.", sendStop: false).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>The TV couldn't play the original: try once more with a converted stream, when FFmpeg is there.</summary>
    private async Task<bool> FallBackAsync()
    {
        if (_triedConversion || !_canConvert || _stream is not { } stream || !stream.Plan.IsDirect)
        {
            return false;
        }

        _triedConversion = true;
        var plan = TvStreamPlanner.Decide(_source, Path.GetExtension(_request.Path), _formats, _canConvert, avoidDirect: true);
        if (plan.Plan.Mode == StreamMode.Unavailable)
        {
            return false;
        }

        Log.LogInformation("The TV couldn't play the original file; sending a converted stream");
        try
        {
            await LoadAsync(plan, _tracker.Trusted ?? Math.Max(0, _request.StartSeconds), Stopping).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is UpnpFaultException or RendererUnreachableException)
        {
            Log.LogWarning("The TV refused the converted stream too: {Message}", ex.Message);
            return false;
        }
    }

    private string CantPlay() => _canConvert
        ? $"{Renderer.Info.Name} couldn't play this file, even converted."
        : $"{Renderer.Info.Name} couldn't play this file. With FFmpeg installed, Aperture can convert it for the TV.";

    private async Task FinishAsync(CastState state, string? message, bool sendStop)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (state == CastState.Ended && _tracker.DurationSeconds > 0)
        {
            await SaveAsync(final: true, at: _tracker.DurationSeconds).ConfigureAwait(false);
        }
        else if (_started)
        {
            await SaveAsync(final: true).ConfigureAwait(false);
        }

        if (sendStop)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await Renderer.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UpnpFaultException or RendererUnreachableException or OperationCanceledException)
            {
                // It may already be off; the cast ends here either way.
            }
        }

        foreach (var token in _tokens)
        {
            _manager.RemoveStream(token);
        }

        _state = state;
        _message = message;
        Log.LogInformation("Casting ended: {State}", state);
        Publish();
    }

    /// <summary>Writes the trusted position (or <paramref name="at"/>) to the same history the desktop uses, unless history is off.</summary>
    private async Task SaveAsync(bool final, double? at = null)
    {
        _lastSaveAt = _clock.Elapsed;
        var position = at ?? _tracker.Trusted;
        var duration = _tracker.DurationSeconds;
        if (position is not { } p || duration <= 0 || (p == _lastSaved && !final))
        {
            return;
        }

        try
        {
            var app = await _manager.Server.AppSettings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (!app.RememberHistory)
            {
                return;
            }

            var entry = new HistoryEntry(_request.Path, MediaFileTypes.TitleFromPath(_request.Path), TimeSpan.FromSeconds(Math.Min(p, duration)), TimeSpan.FromSeconds(duration), _manager.Server.Clock.GetUtcNow());
            await _manager.Server.History.SaveAsync(entry, CancellationToken.None).ConfigureAwait(false);
            _manager.Server.Library.Invalidate();
            _lastSaved = p;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Log.LogWarning("Couldn't save the cast's position: {Message}", ex.Message);
        }
    }

    private void Publish() => Status = new CastStatus(
        Id,
        _state,
        Renderer.Info.Name,
        _request.Path,
        _request.Title,
        CastStatus.IsActiveState(_state) ? _tracker.Latest ?? _tracker.Trusted ?? 0 : _tracker.Trusted ?? 0,
        _tracker.DurationSeconds,
        _stream?.Plan.Plan.Reason,
        _message);
}
