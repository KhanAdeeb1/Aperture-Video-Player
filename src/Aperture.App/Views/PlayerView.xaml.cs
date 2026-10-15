using Aperture.App.Interop;
using Aperture.App.Services;
using Aperture.App.Video;
using Aperture.Core.Library;
using Aperture.Core.Media;
using Aperture.Core.Playback;
using Aperture.Core.Settings;
using Aperture.Playback.Mpv;
using Aperture.Video;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Aperture.App.Views;

/// <summary>
/// The playback screen: the libmpv video surface, auto-hiding controls, the audio and
/// subtitle menu, playback info, and the opening and error states.
/// </summary>
public sealed partial class PlayerView : UserControl
{
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";
    private const string SpeakerGlyph = "\uE767";
    private const string MuteGlyph = "\uE74F";
    private const string EnterFullscreenGlyph = "\uE740";
    private const string ExitFullscreenGlyph = "\uE73F";
    private const string EnterMiniGlyph = "\uE8A7";
    private const string ExitMiniGlyph = "\uE740";
    private const string WarningGlyph = "\uE7BA";
    private const string ErrorGlyphCode = "\uE783";
    private const string VideoGlyph = "\uE714";

    private static readonly TimeSpan SmallStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LargeStep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResumeNoticeDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(1.8);
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(6);

    // Keys without a VirtualKey name (US layout positions; Windows reports the physical key).
    private const VirtualKey OpenBracketKey = (VirtualKey)219;
    private const VirtualKey BackslashKey = (VirtualKey)220;
    private const VirtualKey CloseBracketKey = (VirtualKey)221;
    private const VirtualKey MediaStopKey = (VirtualKey)178;
    private const VirtualKey MediaPlayPauseKey = (VirtualKey)179;

    private readonly DispatcherQueueTimer _ticker;
    private readonly DispatcherQueueTimer _resumeNoticeTimer;
    private readonly DispatcherQueueTimer _singleTapTimer;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _infoTimer;
    private readonly CursorVisibility _cursor = new();
    private readonly IdleOverlay _overlay;

    private MpvPlaybackEngine? _engine;
    private SwapChainPanelBridge? _bridge;
    private string? _engineProblem;
    private IPlaybackHistory? _history;
    private ProgressRecorder? _recorder;
    private Func<AppSettings> _settings = () => AppSettings.Default;
    private string? _path;
    private bool _updatingTimeline;
    private bool _scrubbing;
    private bool _pointerOnControls;
    private bool _keyboardInControls;
    private bool _menuOpen;
    private bool _pointerInside;
    private bool _isMini;

    public PlayerView()
    {
        InitializeComponent();

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ticker = dispatcher.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(250);
        _ticker.Tick += OnTick;

        _resumeNoticeTimer = dispatcher.CreateTimer();
        _resumeNoticeTimer.IsRepeating = false;
        _resumeNoticeTimer.Tick += (_, _) => ResumeNotice.Visibility = Visibility.Collapsed;

        _singleTapTimer = dispatcher.CreateTimer();
        _singleTapTimer.IsRepeating = false;
        _singleTapTimer.Interval = DoubleClickWindow;
        _singleTapTimer.Tick += (_, _) => TogglePlayPause();

        _toastTimer = dispatcher.CreateTimer();
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.Opacity = 0;

        _infoTimer = dispatcher.CreateTimer();
        _infoTimer.Interval = TimeSpan.FromSeconds(1);
        _infoTimer.Tick += (_, _) => RefreshInfo();

        BuildSpeedMenu();
        BuildAspectMenu();

        _overlay = new IdleOverlay(dispatcher) { IsPinned = IsOverlayPinned };
        _overlay.VisibilityChanged += OnOverlayVisibilityChanged;

        Timeline.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), handledEventsToo: true);
        Timeline.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => _scrubbing = false), handledEventsToo: true);
        Timeline.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => _scrubbing = false), handledEventsToo: true);
    }

    /// <summary>The user asked for full screen to be toggled.</summary>
    public event EventHandler? FullscreenToggleRequested;

    /// <summary>The user asked for the mini-player to be toggled.</summary>
    public event EventHandler? MiniPlayerToggleRequested;

    /// <summary>The user asked to open a different file.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The user removed the missing file from history; the window should go home.</summary>
    public event EventHandler? DismissRequested;

    /// <summary>The user asked to stop playback (S or the media Stop key); the window should go home.</summary>
    public event EventHandler? StopRequested;

    /// <summary>The user asked to load a subtitle file; the window shows the picker.</summary>
    public event EventHandler? SubtitlePickRequested;

    /// <summary>Raised when the controls fade in or out, so the window can fade its title bar too.</summary>
    public event EventHandler<bool>? OverlayVisibilityChanged;

    /// <summary>Title of the open file, for the title bar.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>True while the overlay (and the title bar with it) should be visible.</summary>
    public bool IsOverlayVisible => _overlay.IsVisible;

    internal void Initialize(MpvPlaybackEngine? engine, VideoPresenter? presenter, string? engineProblem, IPlaybackHistory history, Func<AppSettings> settings)
    {
        _history = history;
        _settings = settings;
        _recorder = new ProgressRecorder(history, () => _settings().RememberHistory, TimeProvider.System);
        _engineProblem = engineProblem;
        if (engine is null || presenter is null)
        {
            // The engine or its video surface could not start: stop using either.
            if (_engine is not null)
            {
                _engine.StatusChanged -= OnEngineStatusChanged;
                _engine.TracksChanged -= OnTracksChanged;
            }

            _bridge?.Detach();
            _bridge = null;
            _engine = null;
            return;
        }

        _engine = engine;
        _bridge = new SwapChainPanelBridge(Surface, presenter);
        engine.StatusChanged += OnEngineStatusChanged;
        engine.TracksChanged += OnTracksChanged;
        engine.NoticeRaised += (_, notice) => ShowToast(notice, NoticeDuration);
        VolumeSlider.Value = engine.Volume * 100;
    }

    /// <summary>Releases the video surface before the engine and presenter are disposed.</summary>
    internal void Shutdown() => _bridge?.Detach();

    /// <summary>Loads a subtitle file into the playing video (from the picker or drag and drop).</summary>
    public async Task AddSubtitleFileAsync(string path)
    {
        if (_engine is null || _engine.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended))
        {
            ShowToast("Open a video first, then add its subtitles.", NoticeDuration);
            return;
        }

        var problem = await _engine.AddSubtitleFileAsync(path);
        ShowToast(problem ?? $"Subtitles added: {Path.GetFileName(path)}", problem is null ? ToastDuration : NoticeDuration);
    }

    /// <summary>Opens a file, resuming from the saved position when Settings allow it.</summary>
    public async Task OpenAsync(string path)
    {
        if (_history is null)
        {
            return;
        }

        if (_engine is null)
        {
            _path = path;
            Title = MediaFileTypes.TitleFromPath(path);
            ShowFailure(new PlaybackFailure(PlaybackFailureKind.Failed, _engineProblem), engineMissing: true);
            return;
        }

        await SaveProgressAsync();

        _path = path;
        Title = MediaFileTypes.TitleFromPath(path);
        OpeningText.Text = Title;
        ResumeNotice.Visibility = Visibility.Collapsed;
        ResetSpeedMenu();
        SetAspect(AspectMode.Original, announce: false);
        SetTimeline(TimeSpan.Zero, TimeSpan.Zero);
        UpdateTracksButton();

        var saved = await _history.FindAsync(path);
        var resumeAt = _settings().ResumePlayback ? WatchProgress.ResumePosition(saved) : TimeSpan.Zero;

        _overlay.Delay = TimeSpan.FromSeconds(_settings().ControlsHideDelaySeconds);
        _overlay.Poke();
        await _engine.OpenAsync(path, resumeAt);

        if (resumeAt > TimeSpan.Zero && _engine.Status is not PlaybackStatus.Failed)
        {
            ResumeText.Text = $"Resumed at {TimeFormat.Clock(resumeAt)}";
            ResumeNotice.Visibility = Visibility.Visible;
            _resumeNoticeTimer.Interval = ResumeNoticeDuration;
            _resumeNoticeTimer.Start();
        }
    }

    /// <summary>Saves the position and closes the file; called when leaving the player.</summary>
    public async Task CloseAsync()
    {
        await SaveProgressAsync();
        _ticker.Stop();
        _resumeNoticeTimer.Stop();
        _singleTapTimer.Stop();
        _toastTimer.Stop();
        Toast.Opacity = 0;
        SetInfoVisible(false);
        _overlay.Reset();
        _cursor.Show();
        _engine?.Close();
        _path = null;
    }

    /// <summary>Writes the current position to history now (also used when the window closes).</summary>
    public async Task SaveProgressAsync()
    {
        if (_engine is null || _recorder is null || _path is null)
        {
            return;
        }

        if (_engine.Status is PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended)
        {
            var position = _engine.Status == PlaybackStatus.Ended ? _engine.Duration : _engine.Position;
            await SaveSafelyAsync(() => _recorder.SaveNowAsync(_path, position, _engine.Duration));
        }
    }

    /// <summary>Updates the layout for the window mode.</summary>
    public void SetWindowMode(bool isFullscreen, bool isMini)
    {
        _isMini = isMini;
        VisualStateManager.GoToState(this, isMini ? "Mini" : "Full", false);
        if (isMini)
        {
            SetInfoVisible(false);
        }
        FullscreenGlyph.Glyph = isFullscreen ? ExitFullscreenGlyph : EnterFullscreenGlyph;
        AutomationName(FullscreenButton, isFullscreen ? "Exit full screen" : "Full screen", isFullscreen ? "Exit full screen (F or Esc)" : "Full screen (F)");
        MiniPlayerGlyph.Glyph = isMini ? ExitMiniGlyph : EnterMiniGlyph;
        AutomationName(MiniPlayerButton, isMini ? "Return to full window" : "Mini player", isMini ? "Return to full window (P or Esc)" : "Mini player (P)");
        _overlay.Poke();
    }

    /// <summary>Playback keyboard shortcuts. Returns true when the key was used.</summary>
    public bool HandleKey(VirtualKey key, bool control, bool shift)
    {
        if (control)
        {
            return false;
        }

        if (key == MediaStopKey || key == VirtualKey.S)
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (_engine is null || ErrorPanel.Visibility == Visibility.Visible)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Space:
            case VirtualKey.K:
            case MediaPlayPauseKey:
                TogglePlayPause();
                break;
            case VirtualKey.C:
                CycleSubtitles();
                break;
            case VirtualKey.A:
                CycleAudio();
                break;
            case VirtualKey.R:
                SetAspect(AspectModes.Next(_engine.Aspect), announce: true);
                break;
            case CloseBracketKey:
                SetSpeed(PlaybackSpeed.Faster(_engine.Rate), announce: true);
                break;
            case OpenBracketKey:
                SetSpeed(PlaybackSpeed.Slower(_engine.Rate), announce: true);
                break;
            case BackslashKey:
                SetSpeed(PlaybackSpeed.Normal, announce: true);
                break;
            case VirtualKey.I:
                SetInfoVisible(InfoPanel.Visibility != Visibility.Visible);
                break;
            case VirtualKey.Left:
                SeekBy(-(shift ? LargeStep * 3 : SmallStep));
                break;
            case VirtualKey.Right:
                SeekBy(shift ? LargeStep * 3 : SmallStep);
                break;
            case VirtualKey.J:
                SeekBy(-LargeStep);
                break;
            case VirtualKey.L:
                SeekBy(LargeStep);
                break;
            case VirtualKey.Home:
                _engine.Seek(TimeSpan.Zero);
                break;
            case VirtualKey.End:
                _engine.Seek(_engine.Duration - TimeSpan.FromSeconds(1));
                break;
            case VirtualKey.Up:
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                break;
            case VirtualKey.Down:
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                break;
            case VirtualKey.M:
                ToggleMute();
                break;
            case VirtualKey.F:
            case VirtualKey.F11:
                FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.P:
                MiniPlayerToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return false;
        }

        UpdateTimelineNow();
        _overlay.Poke();
        return true;
    }

    // ===== Engine =====

    private void OnEngineStatusChanged(object? sender, EventArgs e)
    {
        if (_engine is null)
        {
            return;
        }

        switch (_engine.Status)
        {
            case PlaybackStatus.Opening:
                VisualStateManager.GoToState(this, "Opening", false);
                break;
            case PlaybackStatus.Playing:
                VisualStateManager.GoToState(this, "Ready", false);
                _ticker.Start();
                break;
            case PlaybackStatus.Paused:
                VisualStateManager.GoToState(this, "Ready", false);
                _ = SaveProgressAsync();
                break;
            case PlaybackStatus.Ended:
                VisualStateManager.GoToState(this, "Ready", false);
                UpdateTimelineNow();
                _ = SaveProgressAsync();
                break;
            case PlaybackStatus.Failed:
                SetInfoVisible(false);
                ShowFailure(_engine.Failure ?? new PlaybackFailure(PlaybackFailureKind.Failed));
                break;
            case PlaybackStatus.Idle:
                _ticker.Stop();
                break;
        }

        var playing = _engine.Status == PlaybackStatus.Playing;
        PlayPauseGlyph.Glyph = playing ? PauseGlyph : PlayGlyph;
        AutomationName(PlayPauseButton, playing ? "Pause" : "Play", playing ? "Pause (Space)" : "Play (Space)");
        _overlay.Poke();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_engine is null)
        {
            return;
        }

        UpdateTimelineNow();
        if (_engine.Status == PlaybackStatus.Playing && _recorder is not null && _path is not null)
        {
            var path = _path;
            var position = _engine.Position;
            var duration = _engine.Duration;
            _ = SaveSafelyAsync(() => _recorder.TickAsync(path, position, duration));
        }
    }

    private void UpdateTimelineNow()
    {
        if (_engine is not null && !_scrubbing)
        {
            SetTimeline(_engine.Status == PlaybackStatus.Ended ? _engine.Duration : _engine.Position, _engine.Duration);
        }
    }

    private void SetTimeline(TimeSpan position, TimeSpan duration)
    {
        _updatingTimeline = true;
        Timeline.Maximum = Math.Max(duration.TotalSeconds, 1);
        Timeline.Value = Math.Clamp(position.TotalSeconds, 0, Timeline.Maximum);
        _updatingTimeline = false;
        TimeText.Text = duration > TimeSpan.Zero ? $"{TimeFormat.Clock(position)} / {TimeFormat.Clock(duration)}" : string.Empty;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(Timeline, TimeText.Text);
    }

    private void ShowFailure(PlaybackFailure failure, bool engineMissing = false)
    {
        _ticker.Stop();
        _cursor.Show();
        var fileName = _path is null ? string.Empty : Path.GetFileName(_path);
        ErrorRemoveButton.Visibility = Visibility.Collapsed;
        VisualStateManager.GoToState(this, "CriticalTone", false);

        switch (failure.Kind)
        {
            case PlaybackFailureKind.Failed when engineMissing:
                ErrorGlyph.Glyph = ErrorGlyphCode;
                ErrorTitle.Text = "Aperture’s playback engine couldn’t start";
                ErrorMessage.Text = "Reinstalling Aperture should fix this. Your videos haven’t been touched.";
                ErrorDetail.Text = failure.Detail ?? string.Empty;
                break;
            case PlaybackFailureKind.NotFound:
                ErrorGlyph.Glyph = WarningGlyph;
                ErrorTitle.Text = "This video can’t be found";
                ErrorMessage.Text = "It may have been moved, renamed or deleted, or it’s on a drive that isn’t connected right now.";
                ErrorDetail.Text = _path ?? string.Empty;
                _ = ShowRemoveIfRememberedAsync();
                break;
            case PlaybackFailureKind.Unsupported when _path is not null && !MediaFileTypes.IsVideoFile(_path):
                ErrorGlyph.Glyph = VideoGlyph;
                VisualStateManager.GoToState(this, "NeutralTone", false);
                ErrorTitle.Text = "Aperture can’t play this kind of file";
                ErrorMessage.Text = $"“{fileName}” isn’t a video. Aperture opens MP4, MKV, MOV, WebM, AVI and other common video files.";
                ErrorDetail.Text = string.Empty;
                break;
            case PlaybackFailureKind.Unsupported:
                ErrorGlyph.Glyph = VideoGlyph;
                VisualStateManager.GoToState(this, "NeutralTone", false);
                ErrorTitle.Text = "There’s nothing here Aperture can play";
                ErrorMessage.Text = $"“{fileName}” opened, but Aperture found no picture or sound in it to play.";
                ErrorDetail.Text = failure.Detail ?? string.Empty;
                break;
            case PlaybackFailureKind.Damaged:
                ErrorGlyph.Glyph = ErrorGlyphCode;
                ErrorTitle.Text = "This video can’t be read";
                ErrorMessage.Text = $"“{fileName}” seems to be damaged or incomplete. Your file has not been changed.";
                ErrorDetail.Text = failure.Detail ?? string.Empty;
                break;
            default:
                ErrorGlyph.Glyph = ErrorGlyphCode;
                ErrorTitle.Text = "This video stopped unexpectedly";
                ErrorMessage.Text = $"Something went wrong while playing “{fileName}”. Your file has not been changed.";
                ErrorDetail.Text = failure.Detail ?? string.Empty;
                break;
        }

        ErrorDetail.Visibility = !_isMini && ErrorDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        VisualStateManager.GoToState(this, "Error", false);
        _overlay.Reset();
    }

    private async Task ShowRemoveIfRememberedAsync()
    {
        if (_history is not null && _path is not null && await _history.FindAsync(_path) is not null)
        {
            ErrorRemoveButton.Visibility = Visibility.Visible;
        }
    }

    private static async Task SaveSafelyAsync(Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            AppLog.Error("Could not save playback position", error);
        }
    }

    // ===== Transport =====

    private void TogglePlayPause()
    {
        if (_engine is null)
        {
            return;
        }

        if (_engine.Status == PlaybackStatus.Playing)
        {
            _engine.Pause();
        }
        else
        {
            _engine.Play();
        }
    }

    private void SeekBy(TimeSpan offset)
    {
        if (_engine is not null)
        {
            _engine.Seek(_engine.Position + offset);
        }
    }

    private void ToggleMute()
    {
        if (_engine is null)
        {
            return;
        }

        _engine.IsMuted = !_engine.IsMuted;
        UpdateVolumeGlyph();
    }

    private void UpdateVolumeGlyph()
    {
        var muted = _engine is null || _engine.IsMuted || VolumeSlider.Value == 0;
        VolumeGlyph.Glyph = muted ? MuteGlyph : SpeakerGlyph;
        AutomationName(VolumeButton, _engine?.IsMuted == true ? "Unmute" : "Mute", _engine?.IsMuted == true ? "Unmute (M)" : "Mute (M)");
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnMuteClick(object sender, RoutedEventArgs e) => ToggleMute();

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnMiniPlayerClick(object sender, RoutedEventArgs e) => MiniPlayerToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnTimelineValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingTimeline || _engine is null)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(e.NewValue);
        _engine.Seek(target);
        TimeText.Text = $"{TimeFormat.Clock(target)} / {TimeFormat.Clock(_engine.Duration)}";
        _overlay.Poke();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_engine is null)
        {
            return;
        }

        _engine.Volume = e.NewValue / 100;
        if (_engine.IsMuted && e.NewValue > e.OldValue)
        {
            _engine.IsMuted = false;
        }

        UpdateVolumeGlyph();
    }

    // ===== Speed, picture shape, tracks =====

    private void BuildSpeedMenu()
    {
        foreach (var speed in PlaybackSpeed.Steps)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = PlaybackSpeed.Label(speed),
                GroupName = "Speed",
                Tag = speed,
                IsChecked = speed == PlaybackSpeed.Normal,
            };
            item.Click += (_, _) => SetSpeed(speed, announce: false);
            SpeedMenu.Items.Add(item);
        }
    }

    private void BuildAspectMenu()
    {
        foreach (var mode in AspectModes.All)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = AspectModes.Label(mode),
                GroupName = "Aspect",
                Tag = mode,
                IsChecked = mode == AspectMode.Original,
            };
            item.Click += (_, _) => SetAspect(mode, announce: false);
            AspectMenu.Items.Add(item);
        }
    }

    private void SetSpeed(double speed, bool announce)
    {
        if (_engine is null)
        {
            return;
        }

        _engine.Rate = speed;
        foreach (var item in SpeedMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is double value && Math.Abs(value - speed) < 0.001;
        }

        if (announce)
        {
            ShowToast($"Speed {PlaybackSpeed.Label(speed)}", ToastDuration);
        }
    }

    private void SetAspect(AspectMode mode, bool announce)
    {
        if (_engine is null)
        {
            return;
        }

        _engine.Aspect = mode;
        foreach (var item in AspectMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is AspectMode value && value == mode;
        }

        if (announce)
        {
            ShowToast($"Picture: {AspectModes.Label(mode)}", ToastDuration);
        }
    }

    private void ResetSpeedMenu() => SetSpeed(PlaybackSpeed.Normal, announce: false);

    private void OnTracksChanged(object? sender, EventArgs e) => UpdateTracksButton();

    private void UpdateTracksButton()
    {
        var tracks = _engine?.Tracks ?? [];
        var subtitle = tracks.FirstOrDefault(t => t.Kind == TrackKind.Subtitle && t.IsSelected);
        var tooltip = subtitle is null ? "Audio and subtitles" : $"Audio and subtitles (showing {TrackLabels.Name(subtitle)})";
        ToolTipService.SetToolTip(TracksButton, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(TracksButton, tooltip);
    }

    private void OnTracksMenuOpening(object sender, object e)
    {
        var tracks = _engine?.Tracks ?? [];
        var subtitles = tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
        var audio = tracks.Where(t => t.Kind == TrackKind.Audio).ToList();

        SubtitleMenu.Items.Clear();
        var off = new RadioMenuFlyoutItem { Text = "Off", GroupName = "Subtitles", IsChecked = !subtitles.Any(t => t.IsSelected) };
        off.Click += (_, _) => _engine?.SelectSubtitleTrack(null);
        SubtitleMenu.Items.Add(off);
        foreach (var track in subtitles)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = TrackLabels.Name(track),
                KeyboardAcceleratorTextOverride = TrackLabels.Detail(track),
                GroupName = "Subtitles",
                IsChecked = track.IsSelected,
            };
            var id = track.Id;
            item.Click += (_, _) => _engine?.SelectSubtitleTrack(id);
            SubtitleMenu.Items.Add(item);
        }

        var shownSubtitle = subtitles.FirstOrDefault(t => t.IsSelected);
        SubtitleMenu.Text = shownSubtitle is null ? "Subtitles: Off" : $"Subtitles: {TrackLabels.Name(shownSubtitle)}";

        AudioMenu.Items.Clear();
        foreach (var track in audio)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = TrackLabels.Name(track),
                KeyboardAcceleratorTextOverride = TrackLabels.Detail(track),
                GroupName = "Audio",
                IsChecked = track.IsSelected,
            };
            var id = track.Id;
            item.Click += (_, _) => _engine?.SelectAudioTrack(id);
            AudioMenu.Items.Add(item);
        }

        var playingAudio = audio.FirstOrDefault(t => t.IsSelected);
        AudioMenu.Text = audio.Count == 0 ? "Audio: None" : playingAudio is null ? "Audio: Off" : $"Audio: {TrackLabels.Name(playingAudio)}";
        AudioMenu.IsEnabled = audio.Count > 0;
    }

    private void CycleSubtitles()
    {
        if (_engine is null)
        {
            return;
        }

        var subtitles = _engine.Tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
        if (subtitles.Count == 0)
        {
            ShowToast("This video has no subtitles. Use Audio and subtitles to add a file.", ToastDuration);
            return;
        }

        var current = subtitles.FindIndex(t => t.IsSelected);
        var next = current + 1 < subtitles.Count ? subtitles[current + 1] : null;
        _engine.SelectSubtitleTrack(next?.Id);
        ShowToast(next is null ? "Subtitles off" : $"Subtitles: {TrackLabels.Name(next)}", ToastDuration);
    }

    private void CycleAudio()
    {
        if (_engine is null)
        {
            return;
        }

        var audio = _engine.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        if (audio.Count < 2)
        {
            ShowToast(audio.Count == 0 ? "This video has no sound" : "This video has one audio track", ToastDuration);
            return;
        }

        var next = audio[(audio.FindIndex(t => t.IsSelected) + 1) % audio.Count];
        _engine.SelectAudioTrack(next.Id);
        ShowToast($"Audio: {TrackLabels.Name(next)}", ToastDuration);
    }

    private void OnAddSubtitleClick(object sender, RoutedEventArgs e) => SubtitlePickRequested?.Invoke(this, EventArgs.Empty);

    // ===== Toast and playback info =====

    private void ShowToast(string text, TimeSpan duration)
    {
        ToastText.Text = text;
        Toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Interval = duration;
        _toastTimer.Start();
    }

    private void SetInfoVisible(bool visible)
    {
        if (_isMini || _engine is null)
        {
            visible = false;
        }

        InfoPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InfoMenuItem.IsChecked = visible;
        if (visible)
        {
            RefreshInfo();
            _infoTimer.Start();
        }
        else
        {
            _infoTimer.Stop();
        }
    }

    private void RefreshInfo()
    {
        if (_engine is null)
        {
            return;
        }

        var diagnostics = _engine.GetDiagnostics();
        InfoSections.Children.Clear();
        if (diagnostics.Sections.Count == 0)
        {
            InfoSections.Children.Add(InfoText("Nothing is playing.", secondary: true));
            return;
        }

        foreach (var section in diagnostics.Sections)
        {
            var group = new StackPanel { Spacing = 2 };
            var header = InfoText(section.Title, secondary: true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
            group.Children.Add(header);
            foreach (var item in section.Items)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(InfoText(item.Label, secondary: true));
                var value = InfoText(item.Value, secondary: false);
                value.IsTextSelectionEnabled = true;
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                group.Children.Add(row);
            }

            InfoSections.Children.Add(group);
        }
    }

    private static TextBlock InfoText(string text, bool secondary) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[secondary ? "TextOnVideoSecondaryBrush" : "TextOnVideoBrush"],
        TextWrapping = TextWrapping.Wrap,
    };

    private void OnInfoMenuClick(object sender, RoutedEventArgs e) => SetInfoVisible(InfoMenuItem.IsChecked);

    private void OnInfoCloseClick(object sender, RoutedEventArgs e) => SetInfoVisible(false);

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (_path is not null)
        {
            ShellActions.ShowInFolder(_path);
        }
    }

    private void OnStartOverClick(object sender, RoutedEventArgs e)
    {
        _engine?.Seek(TimeSpan.Zero);
        _engine?.Play();
        ResumeNotice.Visibility = Visibility.Collapsed;
        UpdateTimelineNow();
    }

    private void OnErrorOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private async void OnErrorRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_history is not null && _path is not null)
        {
            await _history.RemoveAsync(_path);
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    // ===== Overlay, pointer and cursor =====

    private bool IsOverlayPinned() =>
        _engine?.Status is not PlaybackStatus.Playing
        || _pointerOnControls
        || _keyboardInControls
        || _menuOpen
        || _scrubbing;

    private void OnOverlayVisibilityChanged(object? sender, EventArgs e)
    {
        var visible = _overlay.IsVisible;
        Overlay.Opacity = visible ? 1 : 0;
        Overlay.IsHitTestVisible = visible;

        // Keep subtitles clear of the controls while they are shown.
        var lift = visible && !_isMini && Root.ActualHeight > 0 ? (ControlsPanel.ActualHeight + 16) / Root.ActualHeight : 0;
        _engine?.LiftSubtitles(lift);
        if (visible || !_pointerInside)
        {
            _cursor.Show();
        }
        else
        {
            _cursor.Hide();
        }

        OverlayVisibilityChanged?.Invoke(this, visible);
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        _overlay.Poke();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        _cursor.Show();
        if (_engine?.Status == PlaybackStatus.Playing)
        {
            _overlay.TryHide();
        }
    }

    private void OnControlsPointerEntered(object sender, PointerRoutedEventArgs e) => _pointerOnControls = true;

    private void OnControlsPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOnControls = false;
        _overlay.Poke();
    }

    private void OnControlsGotFocus(object sender, RoutedEventArgs e) =>
        _keyboardInControls = e.OriginalSource is Control { FocusState: FocusState.Keyboard };

    private void OnControlsLostFocus(object sender, RoutedEventArgs e)
    {
        _keyboardInControls = false;
        _overlay.Poke();
    }

    private void OnFlyoutOpened(object sender, object e) => _menuOpen = true;

    private void OnFlyoutClosed(object sender, object e)
    {
        _menuOpen = false;
        _overlay.Poke();
    }

    private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_engine?.Status is PlaybackStatus.Opening or PlaybackStatus.Failed)
        {
            return;
        }

        // Wait briefly so a double-click toggles full screen without also toggling play.
        _singleTapTimer.Stop();
        _singleTapTimer.Start();
        _overlay.Poke();
    }

    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _singleTapTimer.Stop();
        FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void AutomationName(Button button, string name, string tooltip)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tooltip);
    }
}
