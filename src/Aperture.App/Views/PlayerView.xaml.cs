using Aperture.App.Interop;
using Aperture.App.Services;
using Aperture.App.Video;
using Aperture.Core.Input;
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
using Microsoft.UI.Xaml.Media;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Aperture.App.Views;

/// <summary>
/// The playback screen: the libmpv video surface, auto-hiding controls, the audio and
/// subtitle menu, sync and subtitle look, playback info, the shortcut list, and the
/// opening, resume and error states. Keys arrive as commands from the user's shortcut map.
/// </summary>
public sealed partial class PlayerView : UserControl
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string SpeakerGlyph = "";
    private const string MuteGlyph = "";
    private const string EnterFullscreenGlyph = "";
    private const string ExitFullscreenGlyph = "";
    private const string EnterMiniGlyph = "";
    private const string ExitMiniGlyph = "";
    private const string WarningGlyph = "";
    private const string ErrorGlyphCode = "";
    private const string VideoGlyph = "";

    private static readonly TimeSpan SmallStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JumpStep = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongStep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResumeNoticeDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(1.8);
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(6);
    private const double VolumeStep = 5;

    private readonly DispatcherQueueTimer _ticker;
    private readonly DispatcherQueueTimer _resumeNoticeTimer;
    private readonly DispatcherQueueTimer _singleTapTimer;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _infoTimer;
    private readonly DispatcherQueueTimer _volumeSaveTimer;
    private readonly CursorVisibility _cursor = new();
    private readonly IdleOverlay _overlay;

    private MpvPlaybackEngine? _engine;
    private SwapChainPanelBridge? _bridge;
    private string? _engineProblem;
    private AppServices? _services;
    private ProgressRecorder? _recorder;
    private Func<ShortcutMap> _shortcuts = () => ShortcutMap.Default;
    private string? _path;
    private PlaybackPreferences? _pendingChoices;
    private bool _asking;
    private bool _updatingTimeline;
    private bool _updatingLook;
    private bool _scrubbing;
    private bool _pointerOnControls;
    private bool _keyboardInControls;
    private bool _focusInPanel;
    private bool _menuOpen;
    private bool _pointerInside;
    private bool _isMini;
    private bool _isFullscreen;

    public PlayerView()
    {
        InitializeComponent();

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ticker = dispatcher.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(250);
        _ticker.Tick += OnTick;

        _resumeNoticeTimer = dispatcher.CreateTimer();
        _resumeNoticeTimer.IsRepeating = false;
        _resumeNoticeTimer.Tick += (_, _) => HideResumeNotice();

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

        // Volume is remembered between sessions; saved once the user stops changing it.
        _volumeSaveTimer = dispatcher.CreateTimer();
        _volumeSaveTimer.IsRepeating = false;
        _volumeSaveTimer.Interval = TimeSpan.FromSeconds(1);
        _volumeSaveTimer.Tick += async (_, _) => await SaveVolumeAsync();

        BuildSpeedMenu();
        BuildAspectMenu();
        BuildLookChoices();

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

    /// <summary>The user asked to stop playback; the window should go home.</summary>
    public event EventHandler? StopRequested;

    /// <summary>The user asked to load a subtitle file; the window shows the picker.</summary>
    public event EventHandler? SubtitlePickRequested;

    /// <summary>Raised when the controls fade in or out, so the window can fade its title bar too.</summary>
    public event EventHandler<bool>? OverlayVisibilityChanged;

    /// <summary>The user changed the subtitle look from the player; the window saves it.</summary>
    public event EventHandler<SubtitleStyle>? SubtitleStyleChanged;

    /// <summary>Title of the open file, for the title bar.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>True while the overlay (and the title bar with it) should be visible.</summary>
    public bool IsOverlayVisible => _overlay.IsVisible;

    /// <summary>True while a side panel or the shortcut list is open (Esc closes it first).</summary>
    public bool HasOpenPanel =>
        AdjustPanel.Visibility == Visibility.Visible || ShortcutsPanel.Visibility == Visibility.Visible || InfoPanel.Visibility == Visibility.Visible;

    /// <summary>True when keyboard focus is in one of the player's panels (its lists and buttons then get the keys).</summary>
    public bool IsFocusInPanel => _focusInPanel;

    internal void Initialize(MpvPlaybackEngine? engine, VideoPresenter? presenter, string? engineProblem, AppServices services, Func<ShortcutMap> shortcuts)
    {
        _services = services;
        _shortcuts = shortcuts;
        _recorder = new ProgressRecorder(services.History, () => services.Settings.Current.RememberHistory, TimeProvider.System);
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

        // Sound level and mute are remembered from the last session.
        var settings = services.Settings.Current;
        engine.Volume = settings.Volume;
        engine.IsMuted = settings.Muted;
        VolumeSlider.Value = settings.Volume * 100;
        ApplySettings(settings);
        ApplyShortcutLabels();
        UpdateVolumeGlyph();
    }

    /// <summary>Applies settings that take effect during playback (pitch, subtitle look, idle delay).</summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _overlay.Delay = TimeSpan.FromSeconds(settings.ControlsHideDelaySeconds);
        if (_engine is null)
        {
            return;
        }

        _engine.PreservePitch = settings.PreservePitch;
        _engine.ApplySubtitleStyle(settings.SubtitleStyle);
        ShowLook(settings.SubtitleStyle);
    }

    /// <summary>Updates tooltips, menu hints and the shortcut list after the keys changed.</summary>
    public void ApplyShortcutLabels()
    {
        var map = _shortcuts();
        var playing = _engine?.Status == PlaybackStatus.Playing;
        AutomationName(PlayPauseButton, playing ? "Pause" : "Play", map.WithKey(playing ? "Pause" : "Play", PlayerCommand.PlayPause));
        UpdateVolumeGlyph();
        SetWindowLabels();
        InfoMenuItem.KeyboardAcceleratorTextOverride = map.Label(PlayerCommand.PlaybackInfo) ?? string.Empty;
        AdjustMenuItem.KeyboardAcceleratorTextOverride = map.Label(PlayerCommand.Adjustments) ?? string.Empty;
        ShortcutsMenuItem.KeyboardAcceleratorTextOverride = map.Label(PlayerCommand.Shortcuts) ?? string.Empty;
        ToolTipService.SetToolTip(InfoCloseButton, map.WithKey("Close", PlayerCommand.PlaybackInfo));
        ToolTipService.SetToolTip(AdjustCloseButton, map.WithKey("Close", PlayerCommand.Adjustments));
        ToolTipService.SetToolTip(ShortcutsCloseButton, map.WithKey("Close", PlayerCommand.Shortcuts));
        Describe(SubtitleEarlierButton, "Show subtitles earlier", PlayerCommand.SubtitlesEarlier);
        Describe(SubtitleLaterButton, "Show subtitles later", PlayerCommand.SubtitlesLater);
        Describe(AudioEarlierButton, "Play sound earlier", PlayerCommand.AudioEarlier);
        Describe(AudioLaterButton, "Play sound later", PlayerCommand.AudioLater);
        var earlier = map.Label(PlayerCommand.SubtitlesEarlier);
        var later = map.Label(PlayerCommand.SubtitlesLater);
        SyncHint.Text = earlier is not null && later is not null
            ? $"{earlier} and {later} move subtitles while watching; each press is 0.1 s."
            : "Each press moves by 0.1 s.";
        BuildShortcutList();

        void Describe(Button button, string name, PlayerCommand command)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, map.WithKey(name, command));
        }
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

    /// <summary>
    /// Opens a file. A part-watched file resumes, asks, or starts over as Settings say
    /// (<paramref name="fromStart"/> always starts over); the file's remembered tracks,
    /// timing offsets, picture shape and speed are restored.
    /// </summary>
    public async Task OpenAsync(string path, bool fromStart = false)
    {
        if (_services is null)
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

        await LeaveCurrentFileAsync();

        _path = path;
        Title = MediaFileTypes.TitleFromPath(path);
        OpeningText.Text = Title;
        AudioOnlyText.Text = Title;
        AudioOnlyPanel.Visibility = Visibility.Collapsed;
        HideResumeNotice();
        SetTimeline(TimeSpan.Zero, TimeSpan.Zero);
        UpdateTracksButton();

        var settings = _services.Settings.Current;
        var saved = await Safely(() => _services.History.FindAsync(path), null);
        var choices = await Safely(() => _services.ItemState.GetPreferencesAsync(path), PlaybackPreferences.None);

        // Playing a file again lets history decide whether it's watched.
        await Safely(async () => { await _services.ItemState.SetWatchedAsync(path, null); return true; }, false);

        var savedPosition = fromStart ? TimeSpan.Zero : WatchProgress.ResumePosition(saved);
        var ask = savedPosition > TimeSpan.Zero && settings.Resume == ResumeMode.Ask;
        var resumeAt = settings.Resume == ResumeMode.StartOver ? TimeSpan.Zero : savedPosition;

        ApplySettings(settings);
        SetSpeed(choices.Speed ?? PlaybackSpeed.Normal, announce: false);
        SetAspect(choices.Aspect ?? AspectMode.Original, announce: false);
        _overlay.Poke();
        await _engine.OpenAsync(path, resumeAt, startPaused: ask);
        if (_engine.Status is PlaybackStatus.Failed || !string.Equals(_path, path, StringComparison.Ordinal))
        {
            return;
        }

        _engine.SubtitleDelay = choices.SubtitleDelay;
        _engine.AudioDelay = choices.AudioDelay;
        ShowOffsets();
        _pendingChoices = choices;
        ApplyRememberedTracks();

        if (ask)
        {
            _asking = true;
            ResumeText.Text = $"You stopped at {TimeFormat.Clock(resumeAt)}";
            ResumeButton.Visibility = Visibility.Visible;
            ResumeNotice.Visibility = Visibility.Visible;
            ResumeButton.Focus(FocusState.Programmatic);
        }
        else if (resumeAt > TimeSpan.Zero)
        {
            ResumeText.Text = $"Resumed at {TimeFormat.Clock(resumeAt)}";
            ResumeButton.Visibility = Visibility.Collapsed;
            ResumeNotice.Visibility = Visibility.Visible;
            _resumeNoticeTimer.Interval = ResumeNoticeDuration;
            _resumeNoticeTimer.Start();
        }
    }

    /// <summary>Saves the position and choices and closes the file; called when leaving the player.</summary>
    public async Task CloseAsync()
    {
        await LeaveCurrentFileAsync();
        _ticker.Stop();
        _resumeNoticeTimer.Stop();
        _singleTapTimer.Stop();
        _toastTimer.Stop();
        Toast.Opacity = 0;
        HideResumeNotice();
        SetInfoVisible(false);
        SetAdjustVisible(false);
        SetShortcutsVisible(false);
        _overlay.Reset();
        _cursor.Show();
        _engine?.Close();
        _path = null;
        await SaveVolumeAsync();
    }

    /// <summary>Writes the current position to history now (also used when the window closes).</summary>
    public async Task SaveProgressAsync()
    {
        if (_engine is null || _recorder is null || _path is null || _asking)
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
            SetAdjustVisible(false);
            SetShortcutsVisible(false);
        }

        FullscreenGlyph.Glyph = isFullscreen ? ExitFullscreenGlyph : EnterFullscreenGlyph;
        MiniPlayerGlyph.Glyph = isMini ? ExitMiniGlyph : EnterMiniGlyph;
        _isFullscreen = isFullscreen;
        SetWindowLabels();
        _overlay.Poke();
        UpdateSubtitleLift();
    }

    private void SetWindowLabels()
    {
        var map = _shortcuts();
        var fullscreenKey = map.Label(PlayerCommand.Fullscreen);
        var miniKey = map.Label(PlayerCommand.MiniPlayer);
        AutomationName(FullscreenButton, _isFullscreen ? "Exit full screen" : "Full screen",
            _isFullscreen ? $"Exit full screen ({(fullscreenKey is null ? "Esc" : fullscreenKey + " or Esc")})" : map.WithKey("Full screen", PlayerCommand.Fullscreen));
        AutomationName(MiniPlayerButton, _isMini ? "Return to full window" : "Mini player",
            _isMini ? $"Return to full window ({(miniKey is null ? "Esc" : miniKey + " or Esc")})" : map.WithKey("Mini player", PlayerCommand.MiniPlayer));
    }

    /// <summary>
    /// Handles a key press already matched to a command. Returns true when the command was
    /// used. Keys while focus is in a panel's list or button go to that control instead,
    /// except the key that closes the panel.
    /// </summary>
    public bool HandleCommand(PlayerCommand command)
    {
        if (_focusInPanel && command is not (PlayerCommand.Adjustments or PlayerCommand.Shortcuts or PlayerCommand.PlaybackInfo))
        {
            return false;
        }

        if (command == PlayerCommand.Stop)
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (command == PlayerCommand.Shortcuts)
        {
            SetShortcutsVisible(ShortcutsPanel.Visibility != Visibility.Visible);
            return true;
        }

        if (_engine is null || ErrorPanel.Visibility == Visibility.Visible)
        {
            return false;
        }

        switch (command)
        {
            case PlayerCommand.PlayPause:
                if (_asking)
                {
                    Resume();
                }
                else
                {
                    TogglePlayPause();
                }

                break;
            case PlayerCommand.SeekBack:
                SeekBy(-SmallStep);
                break;
            case PlayerCommand.SeekForward:
                SeekBy(SmallStep);
                break;
            case PlayerCommand.JumpBack:
                SeekBy(-JumpStep);
                break;
            case PlayerCommand.JumpForward:
                SeekBy(JumpStep);
                break;
            case PlayerCommand.SeekBackLong:
                SeekBy(-LongStep);
                break;
            case PlayerCommand.SeekForwardLong:
                SeekBy(LongStep);
                break;
            case PlayerCommand.GoToStart:
                _engine.Seek(TimeSpan.Zero);
                break;
            case PlayerCommand.GoToEnd:
                _engine.Seek(_engine.Duration - TimeSpan.FromSeconds(1));
                break;
            case PlayerCommand.FrameBack:
                EndAsking();
                _engine.StepFrame(forward: false);
                break;
            case PlayerCommand.FrameForward:
                EndAsking();
                _engine.StepFrame(forward: true);
                break;
            case PlayerCommand.VolumeUp:
                ChangeVolume(VolumeStep);
                break;
            case PlayerCommand.VolumeDown:
                ChangeVolume(-VolumeStep);
                break;
            case PlayerCommand.Mute:
                ToggleMute();
                break;
            case PlayerCommand.Faster:
                SetSpeed(PlaybackSpeed.Faster(_engine.Rate), announce: true);
                break;
            case PlayerCommand.Slower:
                SetSpeed(PlaybackSpeed.Slower(_engine.Rate), announce: true);
                break;
            case PlayerCommand.NormalSpeed:
                SetSpeed(PlaybackSpeed.Normal, announce: true);
                break;
            case PlayerCommand.NextSubtitle:
                CycleSubtitles();
                break;
            case PlayerCommand.NextAudio:
                CycleAudio();
                break;
            case PlayerCommand.SubtitlesEarlier:
                NudgeSubtitles(-1, announce: true);
                break;
            case PlayerCommand.SubtitlesLater:
                NudgeSubtitles(1, announce: true);
                break;
            case PlayerCommand.AudioEarlier:
                NudgeAudio(-1, announce: true);
                break;
            case PlayerCommand.AudioLater:
                NudgeAudio(1, announce: true);
                break;
            case PlayerCommand.Fullscreen:
                FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case PlayerCommand.MiniPlayer:
                MiniPlayerToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case PlayerCommand.NextPictureShape:
                SetAspect(AspectModes.Next(_engine.Aspect), announce: true);
                break;
            case PlayerCommand.PlaybackInfo:
                SetInfoVisible(InfoPanel.Visibility != Visibility.Visible);
                break;
            case PlayerCommand.Adjustments:
                SetAdjustVisible(AdjustPanel.Visibility != Visibility.Visible);
                break;
            default:
                return false;
        }

        UpdateTimelineNow();
        _overlay.Poke();
        return true;
    }

    /// <summary>Esc while a panel is open closes it (the shortcut list first). Returns true when one was closed.</summary>
    public bool CloseTopPanel()
    {
        if (ShortcutsPanel.Visibility == Visibility.Visible)
        {
            SetShortcutsVisible(false);
        }
        else if (AdjustPanel.Visibility == Visibility.Visible)
        {
            SetAdjustVisible(false);
        }
        else if (InfoPanel.Visibility == Visibility.Visible)
        {
            SetInfoVisible(false);
        }
        else
        {
            return false;
        }

        Focus(FocusState.Programmatic);
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
                EndAsking();
                _ticker.Start();
                break;
            case PlaybackStatus.Paused:
                VisualStateManager.GoToState(this, "Ready", false);
                // A file opened paused (Ask each time) never reaches Playing first; the
                // timeline still has to show where it is.
                _ticker.Start();
                UpdateTimelineNow();
                _ = SaveProgressAsync();
                break;
            case PlaybackStatus.Ended:
                VisualStateManager.GoToState(this, "Ready", false);
                UpdateTimelineNow();
                _ = SaveProgressAsync();
                break;
            case PlaybackStatus.Failed:
                SetInfoVisible(false);
                SetAdjustVisible(false);
                ShowFailure(_engine.Failure ?? new PlaybackFailure(PlaybackFailureKind.Failed));
                break;
            case PlaybackStatus.Idle:
                _ticker.Stop();
                break;
        }

        var playing = _engine.Status == PlaybackStatus.Playing;
        PlayPauseGlyph.Glyph = playing ? PauseGlyph : PlayGlyph;
        AutomationName(PlayPauseButton, playing ? "Pause" : "Play", _shortcuts().WithKey(playing ? "Pause" : "Play", PlayerCommand.PlayPause));
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
        HideResumeNotice();
        AudioOnlyPanel.Visibility = Visibility.Collapsed;
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
            case PlaybackFailureKind.Unsupported when _path is not null && !MediaFileTypes.IsPlayableFile(_path):
                ErrorGlyph.Glyph = VideoGlyph;
                VisualStateManager.GoToState(this, "NeutralTone", false);
                ErrorTitle.Text = "Aperture can’t play this kind of file";
                ErrorMessage.Text = $"“{fileName}” isn’t a video or sound file. Aperture opens MP4, MKV, MOV, WebM, AVI and other common video files, and MP3, M4A, FLAC and other audio.";
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
        if (_services is not { } services || _path is not { } path)
        {
            return;
        }

        if (await Safely(() => services.History.FindAsync(path), null) is not null)
        {
            ErrorRemoveButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Saves where the user was and what they chose for the file being left.</summary>
    private async Task LeaveCurrentFileAsync()
    {
        _pendingChoices = null;
        if (_engine is null || _services is null || _path is null)
        {
            return;
        }

        await SaveProgressAsync();
        if (_engine.Status is PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended)
        {
            var path = _path;
            var choices = RememberedChoices.Capture(_engine.Tracks, _engine.SubtitleDelay, _engine.AudioDelay, _engine.Aspect, _engine.Rate);
            await Safely(async () => { await _services.ItemState.SavePreferencesAsync(path, choices); return true; }, false);
        }

        _asking = false;
    }

    private static async Task SaveSafelyAsync(Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (Exception error) when (error is System.Data.Common.DbException or IOException)
        {
            AppLog.Error("Could not save playback position", error);
        }
    }

    private static async Task<T> Safely<T>(Func<Task<T>> read, T fallback)
    {
        try
        {
            return await read();
        }
        catch (Exception error) when (error is System.Data.Common.DbException or IOException)
        {
            AppLog.Error("Could not read or write the player's records", error);
            return fallback;
        }
    }

    // ===== Resume =====

    private void Resume()
    {
        EndAsking();
        _engine?.Play();
        Focus(FocusState.Programmatic);
    }

    private void EndAsking()
    {
        if (!_asking)
        {
            return;
        }

        _asking = false;
        HideResumeNotice();
    }

    private void HideResumeNotice()
    {
        _resumeNoticeTimer.Stop();
        ResumeNotice.Visibility = Visibility.Collapsed;
        ResumeButton.Visibility = Visibility.Collapsed;
    }

    private void OnResumeClick(object sender, RoutedEventArgs e) => Resume();

    private void OnStartOverClick(object sender, RoutedEventArgs e)
    {
        _asking = false;
        _engine?.Seek(TimeSpan.Zero);
        _engine?.Play();
        HideResumeNotice();
        UpdateTimelineNow();
        Focus(FocusState.Programmatic);
    }

    // ===== Transport =====

    private void TogglePlayPause()
    {
        if (_engine is null)
        {
            return;
        }

        if (_asking)
        {
            Resume();
        }
        else if (_engine.Status == PlaybackStatus.Playing)
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

    private void ChangeVolume(double change)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + change, 0, 100);
        ShowToast(_engine?.IsMuted == true ? $"Volume {VolumeSlider.Value:0} (muted)" : $"Volume {VolumeSlider.Value:0}", ToastDuration);
    }

    private void ToggleMute()
    {
        if (_engine is null)
        {
            return;
        }

        _engine.IsMuted = !_engine.IsMuted;
        UpdateVolumeGlyph();
        ShowToast(_engine.IsMuted ? "Muted" : "Sound on", ToastDuration);
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    private void UpdateVolumeGlyph()
    {
        var muted = _engine is null || _engine.IsMuted || VolumeSlider.Value == 0;
        VolumeGlyph.Glyph = muted ? MuteGlyph : SpeakerGlyph;
        var name = _engine?.IsMuted == true ? "Unmute" : "Mute";
        AutomationName(VolumeButton, name, _shortcuts().WithKey(name, PlayerCommand.Mute));
    }

    private async Task SaveVolumeAsync()
    {
        _volumeSaveTimer.Stop();
        if (_services is null || _engine is null)
        {
            return;
        }

        var current = _services.Settings.Current;
        var updated = current with { Volume = Math.Round(_engine.Volume, 2), Muted = _engine.IsMuted };
        if (updated == current)
        {
            return;
        }

        _services.Settings.Current = updated;
        await Safely(async () => { await _services.SettingsStore.SaveAsync(updated); return true; }, false);
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
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    private void OnVideoWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_engine is null || ErrorPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        var delta = e.GetCurrentPoint(InputLayer).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            ChangeVolume(delta > 0 ? VolumeStep : -VolumeStep);
            _overlay.Poke();
            e.Handled = true;
        }
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

        SpeedMenu.Text = Math.Abs(speed - PlaybackSpeed.Normal) < 0.001 ? "Playback speed" : $"Playback speed: {PlaybackSpeed.Label(speed)}";
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

    private void OnTracksChanged(object? sender, EventArgs e)
    {
        UpdateTracksButton();
        ApplyRememberedTracks();
        UpdateAudioOnly();
    }

    /// <summary>Selects the file's remembered audio and subtitle tracks once the track list is known.</summary>
    private void ApplyRememberedTracks()
    {
        if (_engine is null || _pendingChoices is not { } choices || _engine.Tracks.Count == 0)
        {
            return;
        }

        _pendingChoices = null;
        var (audio, subtitle, subtitlesOff) = RememberedChoices.Tracks(choices, _engine.Tracks);
        if (audio is { } audioId)
        {
            _engine.SelectAudioTrack(audioId);
        }

        if (subtitle is { } subtitleId)
        {
            _engine.SelectSubtitleTrack(subtitleId);
        }
        else if (subtitlesOff)
        {
            _engine.SelectSubtitleTrack(null);
        }

        UpdateAudioOnly();
    }

    /// <summary>Files with sound but no picture get a calm placeholder instead of black.</summary>
    private void UpdateAudioOnly()
    {
        var tracks = _engine?.Tracks ?? [];
        var soundOnly = tracks.Any(t => t.Kind == TrackKind.Audio) && !tracks.Any(t => t.Kind == TrackKind.Video);
        AudioOnlyPanel.Visibility = soundOnly && ErrorPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

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

    // ===== Sync and subtitle look =====

    private void NudgeSubtitles(int steps, bool announce)
    {
        if (_engine is null)
        {
            return;
        }

        _engine.SubtitleDelay = SyncOffset.Nudge(_engine.SubtitleDelay, steps);
        ShowOffsets();
        if (announce)
        {
            ShowToast(SyncOffset.Describe("Subtitles", _engine.SubtitleDelay), ToastDuration);
        }
    }

    private void NudgeAudio(int steps, bool announce)
    {
        if (_engine is null)
        {
            return;
        }

        _engine.AudioDelay = SyncOffset.Nudge(_engine.AudioDelay, steps);
        ShowOffsets();
        if (announce)
        {
            ShowToast(SyncOffset.Describe("Sound", _engine.AudioDelay), ToastDuration);
        }
    }

    private void ShowOffsets()
    {
        var subtitle = _engine?.SubtitleDelay ?? TimeSpan.Zero;
        var audio = _engine?.AudioDelay ?? TimeSpan.Zero;
        SubtitleOffsetText.Text = SyncOffset.Label(subtitle);
        AudioOffsetText.Text = SyncOffset.Label(audio);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SubtitleOffsetText, SyncOffset.Describe("Subtitles", subtitle));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AudioOffsetText, SyncOffset.Describe("Sound", audio));
        SubtitleResetButton.IsEnabled = subtitle != TimeSpan.Zero;
        AudioResetButton.IsEnabled = audio != TimeSpan.Zero;
    }

    private void OnSubtitleEarlierClick(object sender, RoutedEventArgs e) => NudgeSubtitles(-1, announce: false);

    private void OnSubtitleLaterClick(object sender, RoutedEventArgs e) => NudgeSubtitles(1, announce: false);

    private void OnAudioEarlierClick(object sender, RoutedEventArgs e) => NudgeAudio(-1, announce: false);

    private void OnAudioLaterClick(object sender, RoutedEventArgs e) => NudgeAudio(1, announce: false);

    private void OnSubtitleResetClick(object sender, RoutedEventArgs e)
    {
        if (_engine is not null)
        {
            _engine.SubtitleDelay = TimeSpan.Zero;
            ShowOffsets();
        }
    }

    private void OnAudioResetClick(object sender, RoutedEventArgs e)
    {
        if (_engine is not null)
        {
            _engine.AudioDelay = TimeSpan.Zero;
            ShowOffsets();
        }
    }

    private void BuildLookChoices()
    {
        foreach (var size in Enum.GetValues<SubtitleSize>())
        {
            SubtitleSizeBox.Items.Add(new ComboBoxItem { Content = SubtitleStyle.Label(size), Tag = size });
        }

        foreach (var color in Enum.GetValues<SubtitleColor>())
        {
            SubtitleColorBox.Items.Add(new ComboBoxItem { Content = SubtitleStyle.Label(color), Tag = color });
        }

        foreach (var background in Enum.GetValues<SubtitleBackground>())
        {
            SubtitleBackgroundBox.Items.Add(new ComboBoxItem { Content = SubtitleStyle.Label(background), Tag = background });
        }
    }

    private void ShowLook(SubtitleStyle style)
    {
        _updatingLook = true;
        SubtitleSizeBox.SelectedItem = SubtitleSizeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SubtitleSize s && s == style.Size);
        SubtitleColorBox.SelectedItem = SubtitleColorBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SubtitleColor c && c == style.Color);
        SubtitleBackgroundBox.SelectedItem = SubtitleBackgroundBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SubtitleBackground b && b == style.Background);
        _updatingLook = false;
    }

    private void OnSubtitleLookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLook || _engine is null || _services is null)
        {
            return;
        }

        var current = _services.Settings.Current.SubtitleStyle;
        var style = new SubtitleStyle(
            SubtitleSizeBox.SelectedItem is ComboBoxItem { Tag: SubtitleSize size } ? size : current.Size,
            SubtitleColorBox.SelectedItem is ComboBoxItem { Tag: SubtitleColor color } ? color : current.Color,
            SubtitleBackgroundBox.SelectedItem is ComboBoxItem { Tag: SubtitleBackground background } ? background : current.Background);
        if (style == current)
        {
            return;
        }

        _engine.ApplySubtitleStyle(style);
        SubtitleStyleChanged?.Invoke(this, style);
    }

    private void SetAdjustVisible(bool visible)
    {
        if (_isMini || _engine is null)
        {
            visible = false;
        }

        AdjustPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AdjustMenuItem.IsChecked = visible;
        if (visible)
        {
            ShowOffsets();
            if (_services is not null)
            {
                ShowLook(_services.Settings.Current.SubtitleStyle);
            }

            if (InfoPanel.Visibility == Visibility.Visible)
            {
                // Both panels fit side by side only on wide windows.
                SetInfoVisible(Root.ActualWidth >= 800);
            }
        }
        else
        {
            _focusInPanel = false;
        }
    }

    private void OnAdjustMenuClick(object sender, RoutedEventArgs e) => SetAdjustVisible(AdjustMenuItem.IsChecked);

    private void OnAdjustCloseClick(object sender, RoutedEventArgs e)
    {
        SetAdjustVisible(false);
        Focus(FocusState.Programmatic);
    }

    // ===== Shortcut list =====

    private void BuildShortcutList()
    {
        var map = _shortcuts();
        ShortcutsLeft.Children.Clear();
        ShortcutsRight.Children.Clear();
        var groups = PlayerCommands.All.GroupBy(PlayerCommands.Group).ToList();
        for (var g = 0; g < groups.Count; g++)
        {
            var section = new StackPanel { Spacing = 4 };
            var header = ShortcutText(PlayerCommands.GroupLabel(groups[g].Key), "CaptionTextStyle", secondary: true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
            section.Children.Add(header);
            foreach (var command in groups[g])
            {
                var keys = map.KeysFor(command);
                if (keys.Count == 0)
                {
                    continue;
                }

                var row = new Grid { ColumnSpacing = 16 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(ShortcutText(PlayerCommands.Label(command), "BodyTextStyle", secondary: false));
                var key = ShortcutText(string.Join(", ", keys), "BodyStrongTextStyle", secondary: false);
                Grid.SetColumn(key, 1);
                row.Children.Add(key);
                section.Children.Add(row);
            }

            // Playback, seeking and sound on the left; subtitles and picture on the right.
            (g < 3 ? ShortcutsLeft : ShortcutsRight).Children.Add(section);
        }

        var global = new StackPanel { Spacing = 4 };
        global.Children.Add(ShortcutText("Everywhere", "CaptionTextStyle", secondary: true));
        foreach (var (label, key) in new (string, string)[] { ("Open a file", "Ctrl+O"), ("Search", "Ctrl+F"), ("Library", "Ctrl+L"), ("Settings", "Ctrl+,"), ("Back, or leave full screen", "Esc") })
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(ShortcutText(label, "BodyTextStyle", secondary: false));
            var keyText = ShortcutText(key, "BodyStrongTextStyle", secondary: false);
            Grid.SetColumn(keyText, 1);
            row.Children.Add(keyText);
            global.Children.Add(row);
        }

        ShortcutsRight.Children.Add(global);
    }

    private static TextBlock ShortcutText(string text, string style, bool secondary) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources[style],
        Foreground = (Brush)Application.Current.Resources[secondary ? "TextOnVideoSecondaryBrush" : "TextOnVideoBrush"],
        TextWrapping = TextWrapping.NoWrap,
    };

    private void SetShortcutsVisible(bool visible)
    {
        if (_isMini)
        {
            visible = false;
        }

        ShortcutsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            BuildShortcutList();
        }
        else
        {
            _focusInPanel = false;
        }

        _overlay.Poke();
    }

    private void OnShortcutsMenuClick(object sender, RoutedEventArgs e) => SetShortcutsVisible(true);

    private void OnShortcutsCloseClick(object sender, RoutedEventArgs e)
    {
        SetShortcutsVisible(false);
        Focus(FocusState.Programmatic);
    }

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
        Foreground = (Brush)Application.Current.Resources[secondary ? "TextOnVideoSecondaryBrush" : "TextOnVideoBrush"],
        TextWrapping = TextWrapping.Wrap,
    };

    private void OnInfoMenuClick(object sender, RoutedEventArgs e) => SetInfoVisible(InfoMenuItem.IsChecked);

    private void OnInfoCloseClick(object sender, RoutedEventArgs e)
    {
        SetInfoVisible(false);
        Focus(FocusState.Programmatic);
    }

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (_path is not null)
        {
            ShellActions.ShowInFolder(_path);
        }
    }

    private void OnErrorOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private async void OnErrorRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null && _path is not null)
        {
            var path = _path;
            await Safely(async () => { await _services.History.RemoveAsync(path); return true; }, false);
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    // ===== Overlay, pointer and cursor =====

    private bool IsOverlayPinned() =>
        _engine?.Status is not PlaybackStatus.Playing
        || _pointerOnControls
        || _keyboardInControls
        || _focusInPanel
        || _menuOpen
        || _scrubbing
        || _asking
        || ShortcutsPanel.Visibility == Visibility.Visible;

    private void OnOverlayVisibilityChanged(object? sender, EventArgs e)
    {
        var visible = _overlay.IsVisible;
        Overlay.Opacity = visible ? 1 : 0;
        Overlay.IsHitTestVisible = visible;
        UpdateSubtitleLift();
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

    /// <summary>Keeps subtitles clear of the controls while they are shown, at any window size.</summary>
    private void UpdateSubtitleLift()
    {
        var lift = _overlay.IsVisible && !_isMini && Root.ActualHeight > 0 ? (ControlsPanel.ActualHeight + 16) / Root.ActualHeight : 0;
        _engine?.LiftSubtitles(lift);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSubtitleLift();

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

    private void OnPanelGotFocus(object sender, RoutedEventArgs e) => _focusInPanel = true;

    private void OnPanelLostFocus(object sender, RoutedEventArgs e)
    {
        // Focus moving between controls inside the panel raises LostFocus then GotFocus.
        _focusInPanel = false;
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

        // A click on the video while a panel has focus gives the keys back to the player.
        if (_focusInPanel)
        {
            Focus(FocusState.Pointer);
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
