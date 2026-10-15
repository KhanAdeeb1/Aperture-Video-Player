using System.Globalization;
using Aperture.Core.Media;
using Aperture.Core.Playback;
using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv;

/// <summary>
/// <see cref="IPlaybackEngine"/> on libmpv. Events from libmpv's thread are posted to the
/// <see cref="SynchronizationContext"/> that was current when the engine was created (the UI
/// thread in the app), so <see cref="StatusChanged"/> and <see cref="TracksChanged"/> are
/// raised there. Without a context (tests) they are raised on the libmpv event thread.
/// </summary>
public sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private const ulong TimePosId = 1;
    private const ulong DurationId = 2;
    private const ulong PauseId = 3;
    private const ulong VolumeId = 4;
    private const ulong MuteId = 5;
    private const ulong SpeedId = 6;
    private const ulong TrackListId = 7;
    private const ulong EofId = 8;

    private readonly object _gate = new();
    private readonly SynchronizationContext? _context;
    private readonly MpvClient _client;
    private readonly Action<string, string> _log;

    private PlaybackStatus _status = PlaybackStatus.Idle;
    private PlaybackFailure? _failure;
    private IReadOnlyList<MediaTrack> _tracks = [];
    private TaskCompletionSource? _opening;
    private string? _path;
    private long _entryId = -1;
    private bool _loaded;
    private bool _paused;
    private bool _eof;
    private double _position;
    private double _duration;
    private double _volume = 1;
    private bool _muted;
    private double _rate = 1;
    private AspectMode _aspect = AspectMode.Original;
    private double _subtitlePosition = 100;
    private bool _preservePitch = true;
    private TimeSpan _subtitleDelay;
    private TimeSpan _audioDelay;
    private string? _boxStyle;
    private LoopPoints _loop = LoopPoints.None;
    private SoundOptions _sound = SoundOptions.Off;
    private bool _soundUnavailable;
    private Dictionary<string, SubtitleMatch> _foundSubtitles = [];
    private int _renderContexts;
    private bool _disposed;

    private MpvPlaybackEngine(MpvClient client, SynchronizationContext? context, Action<string, string> log)
    {
        _client = client;
        _context = context;
        _log = log;
    }

    /// <inheritdoc />
    public event EventHandler? StatusChanged;

    /// <inheritdoc />
    public event EventHandler? TracksChanged;

    /// <summary>
    /// Raised (on the same thread as the other events) for problems that do not stop
    /// playback, such as a picture that cannot be decoded while the sound still plays.
    /// </summary>
    public event EventHandler<string>? NoticeRaised;

    /// <summary>The libmpv handle, for creating a render context. Valid until <see cref="Dispose"/>.</summary>
    internal IntPtr Handle => _client.Handle;

    /// <summary>Describes the video renderer for diagnostics; set by the surface that renders this engine.</summary>
    public Func<string?> RendererDescription { get; set; } = static () => null;

    private static readonly (string Name, string Value)[] LightweightScaling =
    [
        ("scale", "bilinear"), ("cscale", "bilinear"), ("dscale", "bilinear"), ("dither-depth", "no"),
        ("correct-downscaling", "no"), ("linear-downscaling", "no"), ("sigmoid-upscaling", "no"),
        ("deband", "no"), ("hdr-compute-peak", "no"),
    ];

    public PlaybackStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public PlaybackFailure? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    public TimeSpan Position => _loaded ? TimeSpan.FromSeconds(Math.Max(0, Volatile.Read(ref _position))) : TimeSpan.Zero;

    public TimeSpan Duration => _loaded ? TimeSpan.FromSeconds(Math.Max(0, Volatile.Read(ref _duration))) : TimeSpan.Zero;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            _client.SetProperty("volume", Math.Round(_volume * 100, 1));
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            _client.SetProperty("mute", value);
        }
    }

    public double Rate
    {
        get => _rate;
        set
        {
            _rate = Math.Clamp(value, 0.25, 4);
            _client.SetProperty("speed", _rate);
        }
    }

    public bool PreservePitch
    {
        get => _preservePitch;
        set
        {
            _preservePitch = value;
            _client.SetProperty("audio-pitch-correction", value);
        }
    }

    public TimeSpan SubtitleDelay
    {
        get => _subtitleDelay;
        set
        {
            _subtitleDelay = SyncOffset.Normalize(value);
            _client.SetProperty("sub-delay", _subtitleDelay.TotalSeconds);
        }
    }

    public TimeSpan AudioDelay
    {
        get => _audioDelay;
        set
        {
            _audioDelay = SyncOffset.Normalize(value);
            _client.SetProperty("audio-delay", _audioDelay.TotalSeconds);
        }
    }

    public AspectMode Aspect
    {
        get => _aspect;
        set
        {
            _aspect = value;
            var (overrideRatio, panscan, keepAspect) = value switch
            {
                AspectMode.Fill => ("-1", 1.0, true),
                AspectMode.Stretch => ("-1", 0.0, false),
                AspectMode.Widescreen => ("16:9", 0.0, true),
                AspectMode.Standard => ("4:3", 0.0, true),
                AspectMode.Scope => ("2.39", 0.0, true),
                _ => ("-1", 0.0, true),
            };
            _client.SetProperty("video-aspect-override", overrideRatio);
            _client.SetProperty("panscan", panscan);
            _client.SetProperty("keepaspect", keepAspect);
        }
    }

    public IReadOnlyList<MediaTrack> Tracks
    {
        get
        {
            lock (_gate)
            {
                return _tracks;
            }
        }
    }

    /// <summary>
    /// Creates an engine. Throws <see cref="MpvUnavailableException"/> when libmpv is missing
    /// or cannot start; that is the only exception callers need to handle.
    /// </summary>
    public static MpvPlaybackEngine Create(MpvEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = new List<KeyValuePair<string, string>>
        {
            // Predictable behaviour: never read a user's mpv.conf, scripts or key bindings.
            new("config", "no"),
            new("load-scripts", "no"),
            new("input-default-bindings", "no"),
            new("input-vo-keyboard", "no"),
            new("input-cursor", "no"),
            new("osc", "no"),
            new("osd-level", "0"),
            new("terminal", "no"),
            // Never resolve web URLs through youtube-dl/yt-dlp (project rule: no stream extraction).
            new("ytdl", "no"),
            new("idle", "yes"),
            // At the end, pause on the last frame instead of unloading the file.
            new("keep-open", "yes"),
            new("vo", options.VideoOutput),
            new("hwdec", options.HardwareDecoding),
            // Subtitle files are found by Aperture's own rules (SubtitleFinder) and passed with each file.
            new("sub-auto", "no"),
            new("audio-file-auto", "no"),
            new("sub-font-size", "46"),
            new("sub-border-size", "2.5"),
            new("sub-shadow-offset", "1"),
            new("sub-margin-y", "40"),
            new("screenshot-directory", Path.GetTempPath()),
        };
        if (options.AudioOutput is { } audio)
        {
            settings.Add(new("ao", audio));
        }

        settings.AddRange(options.ExtraOptions);

        var client = MpvClient.Create(settings, options.LogLevel, options.Log);
        var engine = new MpvPlaybackEngine(client, SynchronizationContext.Current, options.Log);
        client.MessageReceived += engine.OnMessage;
        client.Observe(TimePosId, "time-pos", MpvFormat.Double);
        client.Observe(DurationId, "duration", MpvFormat.Double);
        client.Observe(PauseId, "pause", MpvFormat.Flag);
        client.Observe(VolumeId, "volume", MpvFormat.Double);
        client.Observe(MuteId, "mute", MpvFormat.Flag);
        client.Observe(SpeedId, "speed", MpvFormat.Double);
        client.Observe(TrackListId, "track-list", MpvFormat.String);
        client.Observe(EofId, "eof-reached", MpvFormat.Flag);

        // A box behind subtitle lines needs mpv 0.38 or later; older builds keep the outline.
        var borderStyles = client.GetString("option-info/sub-border-style/choices") ?? string.Empty;
        engine._boxStyle = borderStyles.Contains("background-box", StringComparison.Ordinal) ? "background-box"
            : borderStyles.Contains("opaque-box", StringComparison.Ordinal) ? "opaque-box"
            : null;
        return engine;
    }

    public Task OpenAsync(string path, TimeSpan startAt, CancellationToken cancellationToken = default) =>
        OpenAsync(path, startAt, startPaused: false, cancellationToken);

    /// <summary>
    /// Opens a file, optionally showing its first frame paused (used when the player asks
    /// whether to resume, so nothing plays before the user answers).
    /// </summary>
    public async Task OpenAsync(string path, TimeSpan startAt, bool startPaused, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Close();

        if (!File.Exists(path))
        {
            Fail(new PlaybackFailure(PlaybackFailureKind.NotFound));
            return;
        }

        if (!MediaFileTypes.IsPlayableFile(path))
        {
            Fail(new PlaybackFailure(PlaybackFailureKind.Unsupported, Path.GetExtension(path)));
            return;
        }

        if (CheckReadable(path) is { } unreadable)
        {
            Fail(unreadable);
            return;
        }

        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _path = path;
            _opening = opening;
            _failure = null;
        }

        SetStatus(PlaybackStatus.Opening);
        _log("info", $"Opening a {Path.GetExtension(path).ToLowerInvariant()} file");

        // Timing offsets and loops belong to one file; the player re-applies what it remembers.
        SubtitleDelay = TimeSpan.Zero;
        AudioDelay = TimeSpan.Zero;
        SetLoop(LoopPoints.None);

        // Subtitle files beside the video or in its "Subs" folder load with it, so libmpv's
        // own track choice (which prefers them) applies just as for subtitles inside the file.
        var found = await Task.Run(() => SubtitleFinder.Find(path), cancellationToken).ConfigureAwait(true);
        _foundSubtitles = found.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
        _client.Command("change-list", "sub-files", "clr", string.Empty);
        foreach (var match in found)
        {
            _client.Command("change-list", "sub-files", "append", match.Path);
        }

        if (found.Count > 0)
        {
            _log("info", $"Found {found.Count} subtitle file(s) for this video");
        }

        // "start" is a global option: it applies to the next file loaded, so it is set
        // (or cleared) before every load.
        _client.SetProperty("start", startAt > TimeSpan.Zero ? startAt.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "none");
        _client.SetProperty("pause", startPaused);
        var result = await _client.CommandAsync("loadfile", path, "replace").ConfigureAwait(true);
        if (result < 0)
        {
            Post(() =>
            {
                if (ReferenceEquals(_opening, opening))
                {
                    Fail(new PlaybackFailure(PlaybackFailureKind.Failed, $"The playback engine refused the file: {MpvNative.Describe(result)}."));
                }
            });
        }

        using (cancellationToken.Register(() => opening.TrySetCanceled(cancellationToken)))
        {
            await opening.Task.ConfigureAwait(true);
        }
    }

    public void Play()
    {
        if (!_loaded)
        {
            return;
        }

        if (Status == PlaybackStatus.Ended)
        {
            _client.Command("seek", "0", "absolute");
        }

        _client.SetProperty("pause", false);
    }

    public void Pause()
    {
        if (_loaded)
        {
            _client.SetProperty("pause", true);
        }
    }

    public void Seek(TimeSpan position)
    {
        if (!_loaded)
        {
            return;
        }

        var target = Math.Max(0, position.TotalSeconds);
        var duration = Volatile.Read(ref _duration);
        if (duration > 0)
        {
            target = Math.Min(target, duration);
        }

        Volatile.Write(ref _position, target);
        _client.Command("seek", target.ToString("0.###", CultureInfo.InvariantCulture), "absolute");
    }

    public void Close()
    {
        TaskCompletionSource? opening;
        var wasOpen = false;
        lock (_gate)
        {
            opening = _opening;
            _opening = null;
            wasOpen = _path is not null;
            _path = null;
            _loaded = false;
            _eof = false;
            _failure = null;
            _entryId = -1;
            _tracks = [];
        }

        Volatile.Write(ref _position, 0);
        Volatile.Write(ref _duration, 0);
        if (wasOpen)
        {
            _client.Command("stop");
        }

        opening?.TrySetResult();
        SetStatus(PlaybackStatus.Idle);
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAudioTrack(long id)
    {
        if (_loaded)
        {
            _client.SetProperty("aid", id.ToString(CultureInfo.InvariantCulture));
        }
    }

    public void SelectSubtitleTrack(long? id)
    {
        if (_loaded)
        {
            _client.SetProperty("sid", id is { } value ? value.ToString(CultureInfo.InvariantCulture) : "no");
        }
    }

    public async Task<string?> AddSubtitleFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!_loaded)
        {
            return "Open a video first.";
        }

        if (!File.Exists(path))
        {
            return "The subtitle file can’t be found.";
        }

        var result = await _client.CommandAsync("sub-add", path, "select").WaitAsync(cancellationToken).ConfigureAwait(true);
        if (result < 0)
        {
            _log("warn", $"A {Path.GetExtension(path).ToLowerInvariant()} subtitle file could not be loaded: {MpvNative.Describe(result)}");
            return "Aperture couldn’t read subtitles from that file.";
        }

        return null;
    }

    public void ApplySubtitleStyle(SubtitleStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _client.SetProperty("sub-scale", style.Scale);
        _client.SetProperty("sub-color", style.ColorHex);
        if (style.Background == SubtitleBackground.Box && _boxStyle is not null)
        {
            _client.SetProperty("sub-border-style", _boxStyle);
            _client.SetProperty("sub-back-color", "#B3101113");
            _client.SetProperty("sub-outline-color", "#B3101113");
        }
        else if (_boxStyle is not null)
        {
            // sub-outline-color is the 0.38+ name; older builds keep their default black outline.
            _client.SetProperty("sub-border-style", "outline-and-shadow");
            _client.SetProperty("sub-outline-color", "#FF000000");
            _client.SetProperty("sub-back-color", "#00000000");
        }
    }

    /// <summary>The A–B loop in effect for the open file.</summary>
    public LoopPoints Loop => _loop;

    /// <summary>Repeats the stretch between the loop's start and end, or stops looping.</summary>
    public void SetLoop(LoopPoints loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        _loop = loop;
        _client.SetProperty("ab-loop-a", loop.IsLooping ? Seconds(loop.Start!.Value) : "no");
        _client.SetProperty("ab-loop-b", loop.IsLooping ? Seconds(loop.End!.Value) : "no");
        if (loop.IsLooping && _loaded)
        {
            var position = Volatile.Read(ref _position);
            if (position < loop.Start!.Value.TotalSeconds || position > loop.End!.Value.TotalSeconds)
            {
                Seek(loop.Start.Value);
            }
        }
    }

    /// <summary>The sound processing in effect.</summary>
    public SoundOptions Sound => _sound;

    /// <summary>
    /// Turns clearer dialogue and even volume on or off. Returns null when applied, or a reason
    /// when this libmpv build's FFmpeg lacks the filters; the sound then stays unprocessed.
    /// </summary>
    public string? SetSound(SoundOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options == _sound && !_soundUnavailable)
        {
            return null;
        }

        var problem = _client.SetPropertyNow("af", options.FilterGraph());
        if (problem is not null)
        {
            _log("warn", $"Sound options could not be applied: {problem}");
            _client.SetPropertyNow("af", string.Empty);
            _sound = SoundOptions.Off;
            _soundUnavailable = !options.IsOff;
            return "This copy of Aperture’s playback engine can’t process sound, so it plays as it is.";
        }

        _sound = options;
        _soundUnavailable = false;
        return null;
    }

    /// <summary>True when this libmpv build can draw a box behind subtitles.</summary>
    public bool SupportsSubtitleBox => _boxStyle is not null;

    public void StepFrame(bool forward)
    {
        if (_loaded)
        {
            _client.Command(forward ? "frame-step" : "frame-back-step");
        }
    }

    public PlaybackDiagnostics GetDiagnostics() =>
        _loaded ? MpvDiagnostics.Build(_client.GetString, RendererDescription()) : PlaybackDiagnostics.Empty;

    /// <summary>
    /// Switches libmpv's GPU renderer to its cheapest scalers. Used when the only Direct3D
    /// device is the CPU rasteriser (WARP), where the default high-quality scalers take enough
    /// CPU to starve the interface.
    /// </summary>
    public void UseLightweightScaling()
    {
        foreach (var (name, value) in LightweightScaling)
        {
            _client.SetProperty(name, value);
        }
    }

    /// <summary>
    /// Raises subtitles by a fraction of the picture height (0 puts them back), so they stay
    /// clear of Aperture's controls while those are shown.
    /// </summary>
    public void LiftSubtitles(double fraction)
    {
        var position = Math.Round(Math.Clamp(100 - (fraction * 100), 50, 100));
        if (position != _subtitlePosition)
        {
            _subtitlePosition = position;
            _client.SetProperty("sub-pos", position);
        }
    }

    /// <summary>Reads a libmpv property as text, for tests and diagnostics.</summary>
    public string? ReadProperty(string name) => _client.GetString(name);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Volatile.Read(ref _renderContexts) > 0)
        {
            _log("error", "The playback engine was disposed while a render context was still alive; dispose the video surface first.");
        }

        _client.MessageReceived -= OnMessage;
        _client.Dispose();
        lock (_gate)
        {
            _opening?.TrySetResult();
            _opening = null;
        }
    }

    internal void RenderContextCreated() => Interlocked.Increment(ref _renderContexts);

    internal void RenderContextFreed() => Interlocked.Decrement(ref _renderContexts);

    // ===== libmpv events (event thread) =====

    private void OnMessage(MpvMessage message)
    {
        switch (message.Id)
        {
            case MpvEventId.PropertyChange when message.ReplyUserData == TimePosId:
                // Frequent (every frame): stored directly, not posted to the UI thread.
                if (!message.IsEmpty)
                {
                    Volatile.Write(ref _position, message.DoubleValue);
                }

                return;
            case MpvEventId.PropertyChange when message.ReplyUserData == DurationId:
                Volatile.Write(ref _duration, message.IsEmpty ? 0 : message.DoubleValue);
                return;
            case MpvEventId.LogMessage:
                OnLogMessage(message);
                return;
        }

        Post(() => Apply(message));
    }

    private void Apply(MpvMessage message)
    {
        if (_disposed)
        {
            return;
        }

        switch (message.Id)
        {
            case MpvEventId.StartFile:
                lock (_gate)
                {
                    _entryId = message.PlaylistEntryId;
                }

                break;

            case MpvEventId.FileLoaded:
                TaskCompletionSource? opening;
                lock (_gate)
                {
                    if (_path is null)
                    {
                        return;
                    }

                    _loaded = true;
                    _eof = false;
                    opening = _opening;
                    _opening = null;
                }

                if (_client.GetDouble("duration") is { } duration)
                {
                    Volatile.Write(ref _duration, duration);
                }

                LogLoadedSummary();
                UpdateStatusFromFlags();
                opening?.TrySetResult();
                break;

            case MpvEventId.EndFile:
                OnEndFile(message);
                break;

            case MpvEventId.PropertyChange:
                OnPropertyChange(message);
                break;
        }
    }

    private void OnEndFile(MpvMessage message)
    {
        lock (_gate)
        {
            // Ends of a file that has since been replaced or closed are not news.
            if (_path is null || (message.PlaylistEntryId != _entryId && _entryId != -1))
            {
                return;
            }
        }

        switch (message.EndReason)
        {
            case MpvEndFileReason.Error:
                _log("warn", $"Playback ended with an error: {MpvNative.Describe(message.EndError)}");
                Fail(MpvFailures.FromEndFile(message.EndError));
                break;
            case MpvEndFileReason.Eof when !_loaded:
                // Reached the end without ever producing a frame or a sample.
                Fail(new PlaybackFailure(PlaybackFailureKind.Damaged, "The file ended before anything could be played."));
                break;
            case MpvEndFileReason.Eof:
                SetStatus(PlaybackStatus.Ended);
                break;
        }
    }

    private void OnPropertyChange(MpvMessage message)
    {
        switch (message.ReplyUserData)
        {
            case PauseId:
                _paused = message.FlagValue;
                UpdateStatusFromFlags();
                break;
            case EofId:
                _eof = message.FlagValue;
                UpdateStatusFromFlags();
                break;
            case VolumeId when !message.IsEmpty:
                _volume = Math.Clamp(message.DoubleValue / 100, 0, 1);
                break;
            case MuteId:
                _muted = message.FlagValue;
                break;
            case SpeedId when !message.IsEmpty:
                _rate = message.DoubleValue;
                break;
            case TrackListId:
                var tracks = DescribeFoundSubtitles(MpvTrackList.Parse(message.StringValue));
                lock (_gate)
                {
                    if (_path is null && tracks.Count > 0)
                    {
                        return;
                    }

                    _tracks = tracks;
                }

                TracksChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// Gives subtitle files Aperture found the language, forced and SDH marks read from their
    /// names ("2_English.srt"), and drops the file name libmpv uses as a title.
    /// </summary>
    private IReadOnlyList<MediaTrack> DescribeFoundSubtitles(IReadOnlyList<MediaTrack> tracks)
    {
        if (_foundSubtitles.Count == 0)
        {
            return tracks;
        }

        return [.. tracks.Select(track =>
            track is { Kind: TrackKind.Subtitle, IsExternal: true, ExternalFileName: { } file } && _foundSubtitles.TryGetValue(file, out var match)
                ? track with
                {
                    Language = match.Language ?? track.Language,
                    Title = match.Title,
                    IsForced = track.IsForced || match.IsForced,
                }
                : track)];
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private void UpdateStatusFromFlags()
    {
        if (!_loaded || Status is PlaybackStatus.Failed)
        {
            return;
        }

        SetStatus(_eof ? PlaybackStatus.Ended : _paused ? PlaybackStatus.Paused : PlaybackStatus.Playing);
    }

    private void OnLogMessage(MpvMessage message)
    {
        var text = message.LogText ?? string.Empty;
        var path = _path;
        if (path is not null)
        {
            text = text.Replace(path, "<file>", StringComparison.OrdinalIgnoreCase);
        }

        _log(message.LogLevel ?? "info", $"libmpv [{message.LogPrefix}] {text}");

        // A decoder or sound device that cannot start does not end playback (the other stream
        // keeps playing), so it is surfaced as a notice rather than a failure.
        if (message.LogLevel is "error" or "fatal")
        {
            var notice = (message.LogPrefix, text) switch
            {
                ("vd", var t) when t.Contains("decoder", StringComparison.OrdinalIgnoreCase) => "Aperture can’t decode the picture in this file. The sound will still play.",
                ("ad", var t) when t.Contains("decoder", StringComparison.OrdinalIgnoreCase) => "Aperture can’t decode the sound in this file. The picture will still play.",
                ("cplayer", var t) when t.Contains("audio device", StringComparison.OrdinalIgnoreCase) => "No sound device is available, so this video is playing without sound.",
                _ => null,
            };
            if (notice is not null)
            {
                Post(() => NoticeRaised?.Invoke(this, notice));
            }
        }
    }

    private void LogLoadedSummary()
    {
        var video = _client.GetString("video-format");
        var audio = _client.GetString("audio-codec-name");
        var size = $"{_client.GetString("video-params/w")}x{_client.GetString("video-params/h")}";
        _log("info", $"Loaded {_client.GetString("file-format")}: video {video ?? "none"} {size}, audio {audio ?? "none"}, hwdec {_client.GetString("hwdec-current") ?? "n/a"}");
    }

    private static PlaybackFailure? CheckReadable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            return stream.Length == 0
                ? new PlaybackFailure(PlaybackFailureKind.Damaged, "The file is empty.")
                : null;
        }
        catch (UnauthorizedAccessException)
        {
            return new PlaybackFailure(PlaybackFailureKind.Failed, "Windows denied access to this file.");
        }
        catch (FileNotFoundException)
        {
            return new PlaybackFailure(PlaybackFailureKind.NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return new PlaybackFailure(PlaybackFailureKind.NotFound);
        }
        catch (IOException error)
        {
            return new PlaybackFailure(PlaybackFailureKind.Failed, error.Message);
        }
    }

    private void Fail(PlaybackFailure failure)
    {
        TaskCompletionSource? opening;
        lock (_gate)
        {
            _failure = failure;
            _loaded = false;
            opening = _opening;
            _opening = null;
        }

        SetStatus(PlaybackStatus.Failed);
        opening?.TrySetResult();
    }

    private void SetStatus(PlaybackStatus status)
    {
        lock (_gate)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(static state => ((Action)state!)(), action);
        }
    }
}
