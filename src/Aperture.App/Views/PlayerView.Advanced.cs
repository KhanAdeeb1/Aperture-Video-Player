using System.Runtime.InteropServices.WindowsRuntime;
using Aperture.App.Services;
using Aperture.Core.Bookmarks;
using Aperture.Core.Input;
using Aperture.Core.Playback;
using Aperture.Core.Series;
using Aperture.Playback.Mpv;
using Aperture.Playback.Mpv.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Aperture.App.Views;

/// <summary>
/// The player's optional extras: seek previews on the timeline, bookmarks with notes, A–B
/// looping, clearer dialogue and even volume, the choices carried from episode to episode of a
/// show, and recovering from (or reporting) a playback failure. None of them shows anything
/// until it is used, so ordinary playback looks as it did.
/// </summary>
public sealed partial class PlayerView
{
    // The preview picture fits this box (in view pixels); 16:9 video fills it exactly.
    private const double PreviewBoxWidth = 192;
    private const double PreviewBoxHeight = 108;

    // The slider's thumb is 20 wide and its centre never leaves the track, so the track's
    // usable length starts and ends half a thumb in from the control's edges.
    private const double TimelineInset = 10;

    private MpvSeekPreviews? _previews;
    private string? _previewsPath;
    private bool _previewsOpening;
    private bool _previewsUnavailable;
    private TimeSpan? _previewWanted;
    private WriteableBitmap? _previewBitmap;

    private IReadOnlyList<Bookmark> _bookmarks = [];
    private int _bookmarkLoad;
    private double _marksDuration;

    private SoundOptions? _fileSound;
    private bool _soundChosen;
    private ShowChoices _showChoices = ShowChoices.None;

    private bool _reachedPlayback;
    private bool _recoveredOnce;
    private TimeSpan _lastGoodPosition;

    private void InitializeAdvanced()
    {
        _overlay.VisibilityChanged += (_, _) =>
        {
            if (!_overlay.IsVisible)
            {
                HidePreview();
            }
        };
    }

    /// <summary>Labels for the extras' menu items and buttons, with the keys from the shortcut map.</summary>
    private void ApplyAdvancedShortcutLabels(ShortcutMap map)
    {
        BookmarksMenuItem.KeyboardAcceleratorTextOverride = map.Label(PlayerCommand.Bookmarks) ?? string.Empty;
        ClearDialogueItem.KeyboardAcceleratorTextOverride = map.Label(PlayerCommand.ClearDialogue) ?? string.Empty;
        EvenVolumeItem.KeyboardAcceleratorTextOverride = map.Label(PlayerCommand.EvenVolume) ?? string.Empty;
        LoopStartItem.KeyboardAcceleratorTextOverride = map.Label(PlayerCommand.LoopSection) ?? string.Empty;
        ToolTipService.SetToolTip(AddBookmarkButton, map.WithKey("Bookmark this moment", PlayerCommand.AddBookmark));
        ToolTipService.SetToolTip(BookmarksCloseButton, map.WithKey("Close", PlayerCommand.Bookmarks));
        ToolTipService.SetToolTip(LoopChip, map.WithKey("Stop looping", PlayerCommand.LoopSection));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LoopChip, "Stop looping");
    }

    // ===== Opening and leaving a file =====

    /// <summary>
    /// Sets up the extras for a file about to open: its sound options (the file's own choice,
    /// else the show's, else Settings), the show's carried-over choices, and its bookmarks.
    /// Returns the speed to start at when the file itself doesn't remember one.
    /// </summary>
    private async Task<double?> PrepareAdvancedAsync(string path, PlaybackPreferences choices, bool retrying)
    {
        ClosePreviews();
        if (!retrying)
        {
            _recoveredOnce = false;
        }

        _reachedPlayback = false;
        _lastGoodPosition = TimeSpan.Zero;
        _bookmarks = [];
        RenderBookmarks();
        _showChoices = ShowChoices.None;
        if (_services is not { } services || _engine is null)
        {
            return null;
        }

        if (_series is { } show)
        {
            _showChoices = await Safely(() => services.Series.GetShowChoicesAsync(show.Key), ShowChoices.None);
        }

        _fileSound = choices.Sound;
        _soundChosen = false;
        var sound = choices.Sound ?? _showChoices.Sound ?? services.Settings.Current.SoundDefaults;
        if (_engine.SetSound(sound) is { } problem)
        {
            ShowToast(problem, NoticeDuration);
        }

        UpdateSoundMenu();
        _ = LoadBookmarksAsync(path);
        return _showChoices.Speed;
    }

    /// <summary>Remembers the sound options for the file, and the show's choices for its next episode.</summary>
    private async Task SaveShowChoicesAsync()
    {
        if (_engine is null || _services is not { } services)
        {
            return;
        }

        if (_series is { } show)
        {
            var carried = ShowChoices.Capture(_engine.Tracks, _engine.Rate, _engine.Sound, services.Settings.Current.SoundDefaults);
            await Safely(async () => { await services.Series.SaveShowChoicesAsync(show.Key, carried); return true; }, false);
        }
    }

    /// <summary>The sound options to remember for this file: only once the user chose them for it.</summary>
    private SoundOptions? FileSoundToRemember() => _soundChosen || _fileSound is not null ? _engine?.Sound : null;

    /// <summary>
    /// Picks the show's remembered audio and subtitle language for whatever the file itself
    /// doesn't remember; the file's own choices always win.
    /// </summary>
    private (long? Audio, long? Subtitle, bool SubtitlesOff) WithShowTracks(PlaybackPreferences choices, (long? Audio, long? Subtitle, bool SubtitlesOff) picked)
    {
        if (_engine is null || _showChoices == ShowChoices.None)
        {
            return picked;
        }

        var show = _showChoices.PickTracks(_engine.Tracks);
        var audio = picked.Audio ?? (choices.AudioTrackId is null ? show.AudioTrackId : null);
        if (choices.SubtitleTrackId is null && !choices.SubtitlesOff && picked.Subtitle is null && !picked.SubtitlesOff)
        {
            return (audio, show.SubtitleTrackId, show.SubtitlesOff);
        }

        return (audio, picked.Subtitle, picked.SubtitlesOff);
    }

    private void ResetAdvancedState()
    {
        ClosePreviews();
        SetBookmarksVisible(false);
        _bookmarks = [];
        _bookmarkLoad++;
        _showChoices = ShowChoices.None;
        _fileSound = null;
        _soundChosen = false;
        _reachedPlayback = false;
        UpdateLoopUi();
        DrawTimelineMarks();
    }

    // ===== Seek previews =====

    private void OnTimelinePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_engine is null || _services is null || _path is not { } path || _isMini
            || !_services.Settings.Current.SeekPreviews
            || _engine.Duration < SeekPreview.ShortestVideo
            || _engine.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended)
            || AudioOnlyPanel.Visibility == Visibility.Visible)
        {
            HidePreview();
            return;
        }

        var x = e.GetCurrentPoint(Timeline).Position.X;
        var duration = _engine.Duration;
        var time = TimelineTime(x, duration);
        PreviewTime.Text = TimeFormat.Clock(time);

        if (_previewsUnavailable && string.Equals(_previewsPath, path, StringComparison.Ordinal))
        {
            // Only the time, as the slider's own tooltip used to show.
            PreviewFrame.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewFrame.Visibility = Visibility.Visible;
            if (PreviewImage.Source is null)
            {
                PreviewFrame.Width = PreviewBoxWidth;
                PreviewFrame.Height = PreviewBoxHeight;
            }

            _previewWanted = SeekPreview.Slot(time, duration);
            _ = ShowPreviewAsync(path, _previewWanted.Value);
        }

        PreviewCard.Visibility = Visibility.Visible;
        PlacePreview(x);
    }

    private void OnTimelinePointerExited(object sender, PointerRoutedEventArgs e) => HidePreview();

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => DrawTimelineMarks();

    /// <summary>The time under <paramref name="x"/> on the timeline.</summary>
    private TimeSpan TimelineTime(double x, TimeSpan duration)
    {
        var length = Timeline.ActualWidth - (2 * TimelineInset);
        var fraction = length > 0 ? Math.Clamp((x - TimelineInset) / length, 0, 1) : 0;
        return TimeSpan.FromSeconds(fraction * duration.TotalSeconds);
    }

    /// <summary>Where <paramref name="time"/> sits along the timeline, in its own coordinates.</summary>
    private double TimelineX(TimeSpan time, TimeSpan duration)
    {
        var length = Math.Max(0, Timeline.ActualWidth - (2 * TimelineInset));
        return duration > TimeSpan.Zero ? TimelineInset + (Math.Clamp(time / duration, 0, 1) * length) : TimelineInset;
    }

    private void PlacePreview(double x)
    {
        PreviewCard.UpdateLayout();
        var origin = Timeline.TransformToVisual(Overlay).TransformPoint(new Point(0, 0));
        var width = Math.Max(PreviewCard.ActualWidth, PreviewFrame.Visibility == Visibility.Visible ? PreviewFrame.Width : 0);
        var left = Math.Clamp(origin.X + x - (width / 2), 8, Math.Max(8, Overlay.ActualWidth - width - 8));
        var bottom = Math.Max(0, Overlay.ActualHeight - origin.Y + 4);
        PreviewCard.Margin = new Thickness(left, 0, 0, bottom);
    }

    private void HidePreview()
    {
        _previewWanted = null;
        PreviewCard.Visibility = Visibility.Collapsed;
    }

    private async Task ShowPreviewAsync(string path, TimeSpan slot)
    {
        var previews = await EnsurePreviewsAsync(path);
        if (previews is null || _previewWanted != slot)
        {
            return;
        }

        PreviewFrame? frame;
        try
        {
            frame = await previews.GetAsync(slot);
        }
        catch (ObjectDisposedException)
        {
            return; // the file changed while the picture was being made
        }

        if (frame is null || !ReferenceEquals(previews, _previews) || PreviewCard.Visibility != Visibility.Visible)
        {
            return;
        }

        // Pictures for older pointer positions still help while the newest is being made.
        DrawPreview(frame);
    }

    private async Task<MpvSeekPreviews?> EnsurePreviewsAsync(string path)
    {
        if (string.Equals(_previewsPath, path, StringComparison.Ordinal))
        {
            return _previews;
        }

        if (_previewsOpening)
        {
            return null;
        }

        _previewsOpening = true;
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Round(PreviewBoxWidth * scale);
        var height = (int)Math.Round(PreviewBoxHeight * scale);
        MpvSeekPreviews? previews = null;
        try
        {
            previews = await Task.Run(() => MpvSeekPreviews.OpenAsync(path, width, height, AppLog.FromEngine));
        }
        catch (Exception error) when (error is MpvUnavailableException or IOException or InvalidOperationException)
        {
            AppLog.Warning("Seek previews could not start", error);
        }
        finally
        {
            _previewsOpening = false;
        }

        if (!string.Equals(_path, path, StringComparison.Ordinal))
        {
            // The user moved on while the preview decoder was starting.
            if (previews is not null)
            {
                _ = Task.Run(previews.Dispose);
            }

            return null;
        }

        _previews = previews;
        _previewsPath = path;
        _previewsUnavailable = previews is null;
        return previews;
    }

    private void DrawPreview(PreviewFrame frame)
    {
        if (_previewBitmap is null || _previewBitmap.PixelWidth != frame.Width || _previewBitmap.PixelHeight != frame.Height)
        {
            _previewBitmap = new WriteableBitmap(frame.Width, frame.Height);
            PreviewImage.Source = _previewBitmap;
        }

        using (var pixels = _previewBitmap.PixelBuffer.AsStream())
        {
            pixels.Write(frame.Pixels, 0, frame.Pixels.Length);
        }

        _previewBitmap.Invalidate();
        var scale = XamlRoot?.RasterizationScale ?? 1;
        PreviewFrame.Width = (frame.Width / scale) + 2;
        PreviewFrame.Height = (frame.Height / scale) + 2;
    }

    /// <summary>Stops the preview decoder for the file being left (in the background: it may be mid-picture).</summary>
    private void ClosePreviews()
    {
        HidePreview();
        if (_previews is { } previews)
        {
            _ = Task.Run(previews.Dispose);
        }

        _previews = null;
        _previewsPath = null;
        _previewsUnavailable = false;
        _previewBitmap = null;
        PreviewImage.Source = null;
    }

    /// <summary>Settings turned previews off (or on) while a file plays.</summary>
    private void ApplyPreviewSetting(bool enabled)
    {
        if (!enabled)
        {
            ClosePreviews();
        }
    }

    // ===== Marks on the timeline =====

    /// <summary>Bookmarks as small ticks, and an A–B loop as a band, just above the timeline's track.</summary>
    private void DrawTimelineMarks()
    {
        TimelineMarks.Children.Clear();
        var duration = _engine?.Duration ?? TimeSpan.Zero;
        _marksDuration = duration.TotalSeconds;
        if (duration <= TimeSpan.Zero || Timeline.ActualWidth <= 2 * TimelineInset || _path is null)
        {
            return;
        }

        var middle = Timeline.ActualHeight / 2;
        if (_engine?.Loop is { Start: { } start } loop)
        {
            var a = TimelineX(start, duration);
            var b = loop.End is { } end ? TimelineX(end, duration) : a + 2;
            var band = new Rectangle
            {
                Width = Math.Max(2, b - a),
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                Fill = (Brush)Application.Current.Resources["AccentBrush"],
                Opacity = loop.IsLooping ? 0.55 : 1,
            };
            Canvas.SetLeft(band, a);
            Canvas.SetTop(band, middle - 10);
            TimelineMarks.Children.Add(band);
        }

        var tick = (Brush)Application.Current.Resources["TextOnVideoBrush"];
        foreach (var bookmark in _bookmarks)
        {
            var mark = new Rectangle { Width = 2, Height = 8, Fill = tick, Opacity = 0.85 };
            Canvas.SetLeft(mark, TimelineX(bookmark.Position, duration) - 1);
            Canvas.SetTop(mark, middle - 14);
            TimelineMarks.Children.Add(mark);
        }
    }

    /// <summary>Called every tick: the marks move once the file's length is known.</summary>
    private void UpdateAdvancedOnTick()
    {
        if (_engine is null)
        {
            return;
        }

        if (Math.Abs(_engine.Duration.TotalSeconds - _marksDuration) > 0.5)
        {
            DrawTimelineMarks();
        }

        if (_engine.Status == PlaybackStatus.Playing)
        {
            _reachedPlayback = true;
            _lastGoodPosition = _engine.Position;
        }
    }

    // ===== Bookmarks =====

    private async Task LoadBookmarksAsync(string path)
    {
        if (_services is not { } services)
        {
            return;
        }

        var load = ++_bookmarkLoad;
        var bookmarks = await Safely(() => services.Bookmarks.ListAsync(path), []);
        if (load != _bookmarkLoad || !string.Equals(_path, path, StringComparison.Ordinal))
        {
            return;
        }

        _bookmarks = bookmarks;
        RenderBookmarks();
        DrawTimelineMarks();
    }

    private async Task AddBookmarkAsync()
    {
        if (_engine is null || _services is not { } services || _path is not { } path
            || _engine.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended))
        {
            return;
        }

        var position = _engine.Status == PlaybackStatus.Ended ? _engine.Duration : _engine.Position;
        if (Bookmark.Near(_bookmarks, position) is { } existing)
        {
            ShowToast($"Already bookmarked at {TimeFormat.Clock(existing.Position)}", ToastDuration);
            return;
        }

        var added = await Safely<Bookmark?>(async () => await services.Bookmarks.AddAsync(path, position, string.Empty), null);
        if (added is null)
        {
            ShowToast("The bookmark couldn’t be saved. Aperture’s log has the details.", NoticeDuration);
            return;
        }

        await LoadBookmarksAsync(path);
        var open = _shortcuts().Label(PlayerCommand.Bookmarks);
        ShowToast(
            BookmarksPanel.Visibility == Visibility.Visible || open is null
                ? $"Bookmarked {TimeFormat.Clock(added.Position)}"
                : $"Bookmarked {TimeFormat.Clock(added.Position)} · {open} shows bookmarks",
            ToastDuration);
    }

    private void RenderBookmarks()
    {
        BookmarkList.Children.Clear();
        if (_bookmarks.Count == 0)
        {
            var key = _shortcuts().Label(PlayerCommand.AddBookmark);
            BookmarkList.Children.Add(new TextBlock
            {
                Text = key is null
                    ? "No bookmarks in this video yet."
                    : $"No bookmarks in this video yet. Press {key} while watching to mark a moment.",
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                Foreground = (Brush)Application.Current.Resources["TextOnVideoSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var bookmark in _bookmarks)
        {
            BookmarkList.Children.Add(BookmarkRow(bookmark));
        }
    }

    private Grid BookmarkRow(Bookmark bookmark)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var clock = TimeFormat.Clock(bookmark.Position);
        var jump = new Button
        {
            Content = clock,
            Style = (Style)Application.Current.Resources["QuietButtonStyle"],
            Foreground = (Brush)Application.Current.Resources["AccentBrush"],
            VerticalAlignment = VerticalAlignment.Top,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(jump, $"Go to {clock}");
        ToolTipService.SetToolTip(jump, $"Go to {clock}");
        jump.Click += (_, _) =>
        {
            EndAsking();
            _engine?.Seek(bookmark.Position);
            UpdateTimelineNow();
        };
        row.Children.Add(jump);

        var note = new TextBox
        {
            Text = bookmark.Note,
            PlaceholderText = "Add a note",
            MaxLength = Bookmark.MaximumNoteLength,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(note, $"Note at {clock}");
        note.LostFocus += async (_, _) => await SaveNoteAsync(bookmark, note.Text);
        note.KeyDown += (_, args) =>
        {
            // Enter finishes the note and gives the keys back to the player.
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                Focus(FocusState.Programmatic);
            }
        };
        Grid.SetColumn(note, 1);
        row.Children.Add(note);

        var delete = new Button
        {
            Style = (Style)Application.Current.Resources["VideoIconButtonStyle"],
            Content = new FontIcon
            {
                Glyph = "",
                FontSize = (double)Application.Current.Resources["IconSmall"],
                Style = (Style)Application.Current.Resources["IconGlyphStyle"],
            },
            VerticalAlignment = VerticalAlignment.Top,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete the bookmark at {clock}");
        ToolTipService.SetToolTip(delete, "Delete bookmark");
        delete.Click += async (_, _) => await DeleteBookmarkAsync(bookmark);
        Grid.SetColumn(delete, 2);
        row.Children.Add(delete);
        return row;
    }

    private async Task SaveNoteAsync(Bookmark bookmark, string text)
    {
        if (_services is not { } services)
        {
            return;
        }

        var note = Bookmark.CleanNote(text);
        if (note == bookmark.Note)
        {
            return;
        }

        var saved = await Safely(async () => { await services.Bookmarks.UpdateNoteAsync(bookmark.Id, note); return true; }, false);
        if (!saved)
        {
            ShowToast("The note couldn’t be saved. Aperture’s log has the details.", NoticeDuration);
            return;
        }

        // Keep the list's copy current without redrawing it (focus may have moved to the next note).
        _bookmarks = [.. _bookmarks.Select(b => b.Id == bookmark.Id ? b with { Note = note } : b)];
    }

    private async Task DeleteBookmarkAsync(Bookmark bookmark)
    {
        if (_services is not { } services || _path is not { } path)
        {
            return;
        }

        var deleted = await Safely(async () => { await services.Bookmarks.DeleteAsync(bookmark.Id); return true; }, false);
        if (!deleted)
        {
            ShowToast("The bookmark couldn’t be deleted. Aperture’s log has the details.", NoticeDuration);
            return;
        }

        await LoadBookmarksAsync(path);
        AddBookmarkButton.Focus(FocusState.Programmatic);
    }

    private void SetBookmarksVisible(bool visible)
    {
        if (_isMini || _engine is null || _path is null)
        {
            visible = false;
        }

        BookmarksPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BookmarksMenuItem.IsChecked = visible;
        if (visible)
        {
            // It lives on the right, like the other right-hand panels; one at a time.
            SetAdjustVisible(false);
            SetSegmentsVisible(false);
            CastPanel.Visibility = Visibility.Collapsed;
            if (InfoPanel.Visibility == Visibility.Visible)
            {
                SetInfoVisible(Root.ActualWidth >= 800);
            }

            RenderBookmarks();
        }
        else
        {
            _focusInPanel = false;
        }

        _overlay.Poke();
    }

    private void OnBookmarksMenuClick(object sender, RoutedEventArgs e) => SetBookmarksVisible(BookmarksMenuItem.IsChecked);

    private void OnBookmarksCloseClick(object sender, RoutedEventArgs e)
    {
        SetBookmarksVisible(false);
        Focus(FocusState.Programmatic);
    }

    private async void OnAddBookmarkClick(object sender, RoutedEventArgs e) => await AddBookmarkAsync();

    // ===== A–B loop =====

    /// <summary>The loop key: marks the start, then the end, then stops looping.</summary>
    private void CycleLoop()
    {
        if (_engine is null)
        {
            return;
        }

        var position = _engine.Position;
        var next = _engine.Loop.Next(position, out var refused);
        if (refused)
        {
            ShowToast("The loop needs to be at least half a second long", ToastDuration);
            return;
        }

        _engine.SetLoop(next);
        var key = _shortcuts().Label(PlayerCommand.LoopSection);
        ShowToast(
            next.IsMarking ? (key is null ? next.Describe() : $"{next.Describe()} · {key} again sets the end")
            : next.IsLooping ? next.Describe()
            : "Loop stopped",
            next.IsMarking ? NoticeDuration : ToastDuration);
        UpdateLoopUi();
    }

    private void OnLoopStartClick(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            return;
        }

        var loop = _engine.Loop.WithStart(_engine.Position);
        _engine.SetLoop(loop);
        ShowToast(loop.Describe(), ToastDuration);
        UpdateLoopUi();
    }

    private void OnLoopEndClick(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            return;
        }

        if (!_engine.Loop.TryWithEnd(_engine.Position, out var loop))
        {
            ShowToast("The end must be at least half a second after the start", ToastDuration);
            return;
        }

        _engine.SetLoop(loop);
        ShowToast(loop.Describe(), ToastDuration);
        UpdateLoopUi();
    }

    private void OnLoopStopClick(object sender, RoutedEventArgs e) => StopLoop();

    private void OnLoopChipClick(object sender, RoutedEventArgs e)
    {
        StopLoop();
        Focus(FocusState.Programmatic);
    }

    private void StopLoop()
    {
        if (_engine is null)
        {
            return;
        }

        _engine.SetLoop(LoopPoints.None);
        ShowToast("Loop stopped", ToastDuration);
        UpdateLoopUi();
    }

    private void UpdateLoopUi()
    {
        var loop = _engine?.Loop ?? LoopPoints.None;
        var shown = _path is not null && loop.Start is not null && !_isMini;
        LoopChip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        LoopChipText.Text = loop.Describe();
        LoopEndItem.IsEnabled = loop.Start is not null;
        LoopStopItem.IsEnabled = loop.Start is not null;
        LoopMenu.Text = loop.IsLooping ? $"Loop a section: {TimeFormat.Clock(loop.Start!.Value)} – {TimeFormat.Clock(loop.End!.Value)}" : "Loop a section";
        DrawTimelineMarks();
    }

    // ===== Sound =====

    private void ToggleSound(bool clearDialogue)
    {
        if (_engine is null)
        {
            return;
        }

        var current = _engine.Sound;
        var wanted = clearDialogue
            ? current with { ClearDialogue = !current.ClearDialogue }
            : current with { EvenVolume = !current.EvenVolume };
        _soundChosen = true;
        var problem = _engine.SetSound(wanted);
        UpdateSoundMenu();
        if (problem is not null)
        {
            ShowToast(problem, NoticeDuration);
            return;
        }

        ShowToast(
            clearDialogue
                ? (wanted.ClearDialogue ? "Clearer dialogue on" : "Clearer dialogue off")
                : (wanted.EvenVolume ? "Even volume on" : "Even volume off"),
            ToastDuration);
    }

    private void UpdateSoundMenu()
    {
        var sound = _engine?.Sound ?? SoundOptions.Off;
        ClearDialogueItem.IsChecked = sound.ClearDialogue;
        EvenVolumeItem.IsChecked = sound.EvenVolume;
        SoundMenu.Text = sound.IsOff ? "Sound" : $"Sound: {sound.Describe()}";
    }

    private void OnClearDialogueClick(object sender, RoutedEventArgs e) => ToggleSound(clearDialogue: true);

    private void OnEvenVolumeClick(object sender, RoutedEventArgs e) => ToggleSound(clearDialogue: false);

    // ===== Recovering and reporting =====

    /// <summary>
    /// A file that was playing fine and then failed (a network drive dropping for a moment, a
    /// decoder hiccup) is reopened once where it was, before the error is shown.
    /// </summary>
    private bool TryRecover(PlaybackFailure failure)
    {
        if (_recoveredOnce || !_reachedPlayback || failure.Kind != PlaybackFailureKind.Failed || _path is not { } path)
        {
            return false;
        }

        _recoveredOnce = true;
        var position = _lastGoodPosition;
        AppLog.Warning($"Playback failed at {TimeFormat.Clock(position)} ({failure.Detail ?? "no detail"}); reopening once.");
        ShowToast("Playback stopped unexpectedly. Picking up where it was.", NoticeDuration);
        _ = RecoverAsync(path, position);
        return true;
    }

    private Task RecoverAsync(string path, TimeSpan? position) => OpenAsync(path, at: position, retrying: true);

    /// <summary>Try again: where it was playing, or, if it never started, as an ordinary open.</summary>
    private async void OnErrorRetryClick(object sender, RoutedEventArgs e)
    {
        if (_path is { } path)
        {
            await RecoverAsync(path, _reachedPlayback ? _lastGoodPosition : null);
        }
    }

    private void OnErrorCopyClick(object sender, RoutedEventArgs e)
    {
        var problem = string.Join(Environment.NewLine, new[] { ErrorTitle.Text, ErrorMessage.Text, ErrorDetail.Text }.Where(t => t.Length > 0));
        var facts = new List<(string, string?)>
        {
            ("File type", _path is null ? null : System.IO.Path.GetExtension(_path)),
            ("Engine", _engineProblem is null ? "started" : "could not start"),
        };
        DiagnosticClipboard.Copy(problem, facts, _path);
        ShowToast("Copied. File and folder names were removed; nothing was sent anywhere.", NoticeDuration);
    }

    /// <summary>Whether the error panel offers "Try again" for this failure.</summary>
    private static bool CanRetry(PlaybackFailure failure, bool engineMissing) =>
        !engineMissing && failure.Kind is not PlaybackFailureKind.Unsupported;
}
