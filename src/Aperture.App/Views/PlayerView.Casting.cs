using Aperture.App.Services;
using Aperture.Core.Casting;
using Aperture.Core.Input;
using Aperture.Core.Playback;
using Aperture.Core.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Aperture.App.Views;

/// <summary>
/// Play on a TV. The media server finds DLNA TVs, sends them the file and follows them; this
/// view picks a TV, pauses the picture here, and becomes a remote for the TV until casting
/// ends. While a file is casting the server alone records its position, so this player saves
/// nothing for it; when casting ends the player waits here, paused, where the TV stopped.
/// Every request to the server runs off the UI thread, so the window stays responsive.
/// </summary>
public sealed partial class PlayerView
{
    private static readonly TimeSpan CastPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CastSeekHold = TimeSpan.FromSeconds(3);

    private DispatcherQueueTimer? _castTimer;
    private DispatcherQueueTimer? _castSeekTimer;
    private CastStatus? _cast;
    private string? _castPath;
    private bool _castPolling;
    private bool _updatingCastTimeline;
    private int _castMissedPolls;
    private DateTime _castSeekUntil;

    /// <summary>True while the open file is playing on a TV (this player then saves nothing for it).</summary>
    public bool IsCasting => _cast is { IsActive: true } && _castPath is not null && string.Equals(_castPath, _path, StringComparison.Ordinal);

    private MediaServerHost? CastHost => _services?.MediaServer;

    private void InitializeCasting()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _castTimer = dispatcher.CreateTimer();
        _castTimer.Interval = CastPoll;
        _castTimer.Tick += async (_, _) => await PollCastAsync();

        // Dragging the timeline sends one seek once the thumb settles.
        _castSeekTimer = dispatcher.CreateTimer();
        _castSeekTimer.IsRepeating = false;
        _castSeekTimer.Interval = TimeSpan.FromMilliseconds(400);
        _castSeekTimer.Tick += async (_, _) => await CastSeekToAsync(TimeSpan.FromSeconds(CastTimeline.Value));
    }

    // ===== Choosing a TV =====

    private void OnCastMenuClick(object sender, RoutedEventArgs e) => _ = ShowCastPickerAsync(search: true);

    private void OnCastPanelCloseClick(object sender, RoutedEventArgs e)
    {
        SetCastPanelVisible(false);
        Focus(FocusState.Programmatic);
    }

    private void OnCastSearchClick(object sender, RoutedEventArgs e) => _ = ShowCastPickerAsync(search: true);

    private async void OnCastStartServerClick(object sender, RoutedEventArgs e)
    {
        if (CastHost is not { } host)
        {
            return;
        }

        CastStartServerButton.IsEnabled = false;
        ShowPickerStatus("Starting the media server…", busy: true);
        var problem = await Task.Run(() => host.StartAsync());
        CastStartServerButton.IsEnabled = true;
        if (problem is not null)
        {
            ShowPickerStatus(problem, busy: false);
            return;
        }

        await ShowCastPickerAsync(search: true);
    }

    private void SetCastPanelVisible(bool visible)
    {
        if (_isMini || _engine is null)
        {
            visible = false;
        }

        CastPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            // It lives on the right, like the other right-hand panels; one at a time.
            SetAdjustVisible(false);
            SetSegmentsVisible(false);
        }

        _overlay.Poke();
    }

    private async Task ShowCastPickerAsync(bool search)
    {
        if (_path is null || CastHost is not { } host)
        {
            return;
        }

        SetCastPanelVisible(true);
        CastRendererList.Children.Clear();
        CastStartServerButton.Visibility = Visibility.Collapsed;
        CastSearchButton.Visibility = Visibility.Collapsed;

        if (!MediaServerHost.IsInstalled)
        {
            ShowPickerStatus("This copy of Aperture doesn't include the media server, which sends the video to the TV.", busy: false);
            return;
        }

        ShowPickerStatus("Looking for TVs on your network…", busy: true);
        var renderers = await Task.Run(() => host.GetRenderersAsync(search));
        if (CastPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        if (renderers is null)
        {
            ShowPickerStatus("Casting sends the video through Aperture's media server, which is off.", busy: false);
            CastStartServerButton.Visibility = Visibility.Visible;
            return;
        }

        CastSearchButton.Visibility = Visibility.Visible;
        if (renderers.Count == 0)
        {
            ShowPickerStatus("No TVs found. Check the TV is on and on the same network as this computer.", busy: false);
            return;
        }

        ShowPickerStatus(renderers.Count == 1 ? "One TV found." : $"{renderers.Count} TVs found.", busy: false);
        foreach (var renderer in renderers)
        {
            CastRendererList.Children.Add(RendererButton(renderer));
        }
    }

    private Button RendererButton(CastRenderer renderer)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = renderer.Name,
            Style = (Style)Application.Current.Resources["BodyStrongTextStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (renderer.Model is { Length: > 0 } model)
        {
            text.Children.Add(new TextBlock
            {
                Text = model,
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextOnVideoSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8),
            Style = (Style)Application.Current.Resources["ApertureButtonStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Play on {renderer.Name}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "CastRenderer");
        button.Click += async (_, _) => await StartCastingAsync(renderer);
        return button;
    }

    private void ShowPickerStatus(string text, bool busy)
    {
        CastPickerStatus.Text = text;
        CastSearchRing.IsActive = busy;
        CastSearchRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task StartCastingAsync(CastRenderer renderer)
    {
        if (_engine is null || _path is not { } path || CastHost is not { } host)
        {
            return;
        }

        // Where the desktop is now becomes where the TV starts; saved first, so it is the resume point if anything fails.
        await SaveProgressAsync();
        var position = _engine.Status == PlaybackStatus.Ended ? TimeSpan.Zero : _engine.Position;
        var request = new CastStartRequest(renderer.Id, path, Title, position.TotalSeconds, _engine.Duration.TotalSeconds);
        ShowPickerStatus($"Sending to {renderer.Name}…", busy: true);
        foreach (var child in CastRendererList.Children.OfType<Control>())
        {
            child.IsEnabled = false;
        }

        var (status, problem) = await Task.Run(() => host.StartCastAsync(request));
        foreach (var child in CastRendererList.Children.OfType<Control>())
        {
            child.IsEnabled = true;
        }

        if (status is null || !string.Equals(_path, path, StringComparison.Ordinal))
        {
            ShowPickerStatus(problem ?? "Casting couldn't start.", busy: false);
            return;
        }

        SetCastPanelVisible(false);
        EnterCasting(status, path);
    }

    // ===== While casting =====

    /// <summary>After a file opens: if it is already playing on a TV (cast earlier, or from before the player was left), show the remote again.</summary>
    private async Task AttachToCastAsync(string path)
    {
        if (CastHost is not { } host || !MediaServerHost.IsInstalled)
        {
            return;
        }

        var status = await Task.Run(() => host.GetCastStatusAsync());
        if (status is { IsActive: true } && string.Equals(status.Path, path, StringComparison.Ordinal) && string.Equals(_path, path, StringComparison.Ordinal))
        {
            EnterCasting(status, path);
        }
    }

    private void EnterCasting(CastStatus status, string path)
    {
        _cast = status;
        _castPath = path;
        _castMissedPolls = 0;
        if (_engine?.Status == PlaybackStatus.Playing)
        {
            _engine.Pause();
        }

        HideResumeNotice();
        SetInfoVisible(false);
        SetAdjustVisible(false);
        SetSegmentsVisible(false);
        Overlay.Visibility = Visibility.Collapsed;
        CastingPanel.Visibility = Visibility.Visible;
        ShowCast(status);
        _castTimer?.Start();
        CastPlayPauseButton.Focus(FocusState.Programmatic);
    }

    /// <summary>Leaves the remote. <paramref name="resumeHere"/> moves this player to where the TV stopped.</summary>
    private void ExitCasting(string? notice, bool resumeHere)
    {
        _castTimer?.Stop();
        _castSeekTimer?.Stop();
        var last = _cast;
        _cast = null;
        _castPath = null;
        CastingPanel.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
        if (resumeHere && _engine is not null && last is { PositionSeconds: > 0 } && last.State != CastState.Failed)
        {
            // Paused where the TV left off; Space carries on here.
            _engine.Seek(TimeSpan.FromSeconds(last.PositionSeconds));
            UpdateTimelineNow();
        }

        if (notice is not null)
        {
            ShowToast(notice, NoticeDuration);
        }

        _overlay.Poke();
        Focus(FocusState.Programmatic);
    }

    private async Task PollCastAsync()
    {
        if (_castPolling || CastHost is not { } host || _cast is null)
        {
            return;
        }

        _castPolling = true;
        try
        {
            var status = await Task.Run(() => host.GetCastStatusAsync());
            if (_cast is not { } current)
            {
                return;
            }

            if (status is null)
            {
                // The server went away (stopped, or the computer is busy): give it a few seconds.
                if (++_castMissedPolls >= 5)
                {
                    ExitCasting("The media server stopped, so casting ended. Your place is saved.", resumeHere: true);
                }

                return;
            }

            _castMissedPolls = 0;
            if (status.SessionId != current.SessionId)
            {
                ExitCasting(status.IsActive ? "Casting moved to another video." : null, resumeHere: false);
                return;
            }

            _cast = status;
            if (!status.IsActive)
            {
                ExitCasting(EndedNotice(status), resumeHere: true);
                return;
            }

            ShowCast(status);
        }
        finally
        {
            _castPolling = false;
        }
    }

    private static string EndedNotice(CastStatus status) => status.State switch
    {
        CastState.Ended => $"Finished on {status.RendererName}.",
        CastState.Stopped when status.Message == "Stopped casting." =>
            $"Stopped casting at {TimeFormat.Clock(TimeSpan.FromSeconds(status.PositionSeconds))}. Press Space to carry on here.",
        _ => status.Message ?? "Casting ended.",
    };

    private void ShowCast(CastStatus status)
    {
        var tv = status.RendererName ?? "the TV";
        CastHeading.Text = status.State switch
        {
            CastState.Connecting => $"Connecting to {tv}…",
            CastState.Buffering => $"Loading on {tv}…",
            CastState.Paused => $"Paused on {tv}",
            CastState.Reconnecting => $"Waiting for {tv}",
            _ => $"Playing on {tv}",
        };
        CastTitle.Text = status.Title ?? Title;
        CastMessage.Text = status.Message ?? string.Empty;
        CastMessage.Visibility = status.Message is null || _isMini ? Visibility.Collapsed : Visibility.Visible;
        CastDelivery.Text = status.Delivery ?? string.Empty;

        var playing = status.State is CastState.Playing or CastState.Buffering or CastState.Connecting;
        CastPlayPauseGlyph.Glyph = playing ? PauseGlyph : PlayGlyph;
        AutomationName(CastPlayPauseButton, playing ? "Pause" : "Play", _shortcuts().WithKey(playing ? "Pause" : "Play", PlayerCommand.PlayPause));
        var canControl = status.State is CastState.Playing or CastState.Paused or CastState.Buffering;
        CastPlayPauseButton.IsEnabled = canControl;
        CastBackButton.IsEnabled = canControl;
        CastForwardButton.IsEnabled = canControl;
        CastTimeline.IsEnabled = canControl && status.DurationSeconds > 0;

        // Right after a move the TV still reports the old place for a moment; keep the thumb where it was put.
        if (DateTime.UtcNow < _castSeekUntil || _castSeekTimer?.IsRunning == true)
        {
            return;
        }

        _updatingCastTimeline = true;
        CastTimeline.Maximum = Math.Max(1, status.DurationSeconds);
        CastTimeline.Value = Math.Clamp(status.PositionSeconds, 0, CastTimeline.Maximum);
        _updatingCastTimeline = false;
        CastPositionText.Text = TimeFormat.Clock(TimeSpan.FromSeconds(status.PositionSeconds));
        CastDurationText.Text = status.DurationSeconds > 0 ? TimeFormat.Clock(TimeSpan.FromSeconds(status.DurationSeconds)) : string.Empty;
    }

    private void OnCastTimelineChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingCastTimeline)
        {
            return;
        }

        CastPositionText.Text = TimeFormat.Clock(TimeSpan.FromSeconds(e.NewValue));
        _castSeekTimer?.Stop();
        _castSeekTimer?.Start();
    }

    private async void OnCastPlayPauseClick(object sender, RoutedEventArgs e) => await ToggleCastPlayAsync();

    private async void OnCastBackClick(object sender, RoutedEventArgs e) => await CastSeekByAsync(-JumpStep);

    private async void OnCastForwardClick(object sender, RoutedEventArgs e) => await CastSeekByAsync(JumpStep);

    private async void OnStopCastingClick(object sender, RoutedEventArgs e) => await StopCastingAsync();

    private async Task ToggleCastPlayAsync()
    {
        if (CastHost is not { } host || _cast is not { } status)
        {
            return;
        }

        var pause = status.State is CastState.Playing or CastState.Buffering;
        CastPlayPauseGlyph.Glyph = pause ? PlayGlyph : PauseGlyph;
        if (!await Task.Run(() => host.CastCommandAsync(pause ? CastCommand.Pause : CastCommand.Play)))
        {
            ShowToast($"{status.RendererName ?? "The TV"} didn't respond.", NoticeDuration);
        }

        await PollCastAsync();
    }

    private async Task CastSeekByAsync(TimeSpan offset)
    {
        if (_cast is not { } status)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(Math.Max(0, CastTimeline.Value)) + offset;
        if (status.DurationSeconds > 0)
        {
            target = TimeSpan.FromSeconds(Math.Clamp(target.TotalSeconds, 0, status.DurationSeconds - 1));
        }

        _updatingCastTimeline = true;
        CastTimeline.Value = target.TotalSeconds;
        _updatingCastTimeline = false;
        CastPositionText.Text = TimeFormat.Clock(target);
        await CastSeekToAsync(target);
    }

    private async Task CastSeekToAsync(TimeSpan target)
    {
        if (CastHost is not { } host || _cast is null)
        {
            return;
        }

        _castSeekUntil = DateTime.UtcNow + CastSeekHold;
        if (!await Task.Run(() => host.CastSeekAsync(target)))
        {
            _castSeekUntil = DateTime.MinValue;
            ShowToast($"{_cast?.RendererName ?? "The TV"} didn't move there.", NoticeDuration);
        }
    }

    private async Task StopCastingAsync()
    {
        if (CastHost is not { } host)
        {
            return;
        }

        StopCastingButton.IsEnabled = false;
        _castTimer?.Stop();
        await Task.Run(() => host.CastCommandAsync(CastCommand.Stop));
        StopCastingButton.IsEnabled = true;
        _castPolling = false;
        await PollCastAsync();
        if (_cast is { IsActive: true })
        {
            // The server didn't confirm; leave the remote anyway so this player is usable.
            ExitCasting("Casting may still be running on the TV. Use its remote to stop it.", resumeHere: false);
        }
    }

    /// <summary>Keys while casting drive the TV: play and pause, and moving in the video. Returns true when used.</summary>
    private bool HandleCastCommand(PlayerCommand command)
    {
        switch (command)
        {
            case PlayerCommand.PlayPause:
                _ = ToggleCastPlayAsync();
                return true;
            case PlayerCommand.SeekBack or PlayerCommand.JumpBack:
                _ = CastSeekByAsync(-JumpStep);
                return true;
            case PlayerCommand.SeekForward or PlayerCommand.JumpForward:
                _ = CastSeekByAsync(JumpStep);
                return true;
            case PlayerCommand.SeekBackLong:
                _ = CastSeekByAsync(-LongStep);
                return true;
            case PlayerCommand.SeekForwardLong:
                _ = CastSeekByAsync(LongStep);
                return true;
            case PlayerCommand.Fullscreen:
                FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case PlayerCommand.MiniPlayer:
                MiniPlayerToggleRequested?.Invoke(this, EventArgs.Empty);
                return true;
            default:
                return false; // nothing else applies to the TV
        }
    }

    /// <summary>Leaving the player or opening another file: the TV carries on; the remote closes.</summary>
    private void DetachFromCast()
    {
        _castTimer?.Stop();
        _castSeekTimer?.Stop();
        _cast = null;
        _castPath = null;
        CastingPanel.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
        SetCastPanelVisible(false);
    }
}
