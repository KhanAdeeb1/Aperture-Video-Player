using Aperture.App.Interop;
using Aperture.App.Playback;
using Aperture.App.Services;
using Aperture.Core.Library;
using Aperture.Core.Media;
using Aperture.Core.Playback;
using Aperture.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Aperture.App.Views;

/// <summary>The playback screen: video, auto-hiding controls, opening and error states.</summary>
internal sealed partial class PlayerView : UserControl
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

    private readonly DispatcherQueueTimer _ticker;
    private readonly DispatcherQueueTimer _resumeNoticeTimer;
    private readonly DispatcherQueueTimer _singleTapTimer;
    private readonly CursorVisibility _cursor = new();
    private readonly IdleOverlay _overlay;

    private MediaFoundationEngine? _engine;
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

    /// <summary>Raised when the controls fade in or out, so the window can fade its title bar too.</summary>
    public event EventHandler<bool>? OverlayVisibilityChanged;

    /// <summary>Title of the open file, for the title bar.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>True while the overlay (and the title bar with it) should be visible.</summary>
    public bool IsOverlayVisible => _overlay.IsVisible;

    public void Initialize(MediaFoundationEngine engine, IPlaybackHistory history, Func<AppSettings> settings)
    {
        _engine = engine;
        _history = history;
        _settings = settings;
        _recorder = new ProgressRecorder(history, () => _settings().RememberHistory, TimeProvider.System);
        Surface.SetMediaPlayer(engine.Player);
        engine.StatusChanged += OnEngineStatusChanged;
        VolumeSlider.Value = engine.Volume * 100;
    }

    /// <summary>Opens a file, resuming from the saved position when Settings allow it.</summary>
    public async Task OpenAsync(string path)
    {
        if (_engine is null || _history is null)
        {
            return;
        }

        await SaveProgressAsync();

        _path = path;
        Title = MediaFileTypes.TitleFromPath(path);
        OpeningText.Text = Title;
        ResumeNotice.Visibility = Visibility.Collapsed;
        ResetSpeedMenu();
        SetTimeline(TimeSpan.Zero, TimeSpan.Zero);

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
        FullscreenGlyph.Glyph = isFullscreen ? ExitFullscreenGlyph : EnterFullscreenGlyph;
        AutomationName(FullscreenButton, isFullscreen ? "Exit full screen" : "Full screen", isFullscreen ? "Exit full screen (F or Esc)" : "Full screen (F)");
        MiniPlayerGlyph.Glyph = isMini ? ExitMiniGlyph : EnterMiniGlyph;
        AutomationName(MiniPlayerButton, isMini ? "Return to full window" : "Mini player", isMini ? "Return to full window (P or Esc)" : "Mini player (P)");
        _overlay.Poke();
    }

    /// <summary>Playback keyboard shortcuts. Returns true when the key was used.</summary>
    public bool HandleKey(VirtualKey key, bool control, bool shift)
    {
        if (_engine is null || control || ErrorPanel.Visibility == Visibility.Visible)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Space:
            case VirtualKey.K:
                TogglePlayPause();
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

    private void ShowFailure(PlaybackFailure failure)
    {
        _ticker.Stop();
        _cursor.Show();
        var fileName = _path is null ? string.Empty : Path.GetFileName(_path);
        ErrorRemoveButton.Visibility = Visibility.Collapsed;
        VisualStateManager.GoToState(this, "CriticalTone", false);

        switch (failure.Kind)
        {
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
                ErrorTitle.Text = "This video’s format isn’t supported yet";
                ErrorMessage.Text = $"Windows has no decoder for the picture or sound in “{fileName}”. Wider format support arrives with Aperture’s own playback engine.";
                ErrorDetail.Text = failure.Detail ?? string.Empty;
                break;
            default:
                ErrorGlyph.Glyph = ErrorGlyphCode;
                ErrorTitle.Text = "This video stopped unexpectedly";
                ErrorMessage.Text = $"“{fileName}” may be damaged or incomplete. Your file has not been changed.";
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

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_engine is not null && sender is RadioMenuFlyoutItem { Tag: string tag }
            && double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            _engine.Rate = rate;
        }
    }

    private void ResetSpeedMenu()
    {
        NormalSpeedItem.IsChecked = true;
        if (_engine is not null)
        {
            _engine.Rate = 1;
        }
    }

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
