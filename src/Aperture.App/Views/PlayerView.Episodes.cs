using Aperture.App.Services;
using Aperture.Core.Input;
using Aperture.Core.Library;
using Aperture.Core.Playback;
using Aperture.Core.Series;
using Aperture.Core.Settings;
using Aperture.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Aperture.App.Views;

/// <summary>
/// The player's TV episode features: the episode's name in the title, Skip intro, recap and
/// credits, the next-episode card with its countdown, next and previous episodes, per-show
/// choices, and the panel for checking and correcting where the intro and credits are.
/// Everything is based on the open file's own segments; nothing is skipped because another
/// episode had an intro at the same time.
/// </summary>
public sealed partial class PlayerView
{
    private static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(5);

    private readonly NextEpisodeCountdown _countdown = new(TimeProvider.System);
    private DispatcherQueueTimer? _undoTimer;
    private bool _episodesWired;
    private int _episodeLoad;
    private SeriesInfo? _series;
    private SeriesEpisode? _episode;
    private SeriesEpisode? _nextEpisode;
    private SeriesEpisode? _previousEpisode;
    private SeriesPreferences _showPreferences = SeriesPreferences.Default;
    private string? _fingerprint;
    private EpisodeCueTracker? _cues;
    private MediaSegment? _offered;
    private MediaSegment? _undoSegment;
    private NextUpMoment _nextUpShown;
    private NextUpMoment _nextUpDismissed;
    private bool _countdownCancelled;
    private bool _markedWatched;
    private bool _openingByAutoplay;
    private int _autoplayRun;
    private SegmentKind _editKind = SegmentKind.Intro;
    private TimeSpan? _draftStart;
    private TimeSpan? _draftEnd;

    /// <summary>The user (or the countdown) chose another episode; the window opens it.</summary>
    public event EventHandler<string>? EpisodeRequested;

    private void InitializeEpisodes()
    {
        _undoTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _undoTimer.IsRepeating = false;
        _undoTimer.Interval = UndoDuration;
        _undoTimer.Tick += (_, _) =>
        {
            _undoSegment = null;
            SkipButton.Visibility = Visibility.Collapsed;
            UpdateEpisodeCues();
        };

        foreach (var kind in (SegmentKind[])[SegmentKind.Recap, SegmentKind.Intro, SegmentKind.Credits])
        {
            SegmentKindBox.Items.Add(new ComboBoxItem { Content = MediaSegment.Label(kind), Tag = kind });
        }
    }

    private void WireEpisodes(AppServices services)
    {
        if (_episodesWired)
        {
            return;
        }

        _episodesWired = true;
        services.Analysis.SegmentsChanged += (_, fingerprint) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (fingerprint == _fingerprint)
            {
                await ReloadSegmentsAsync();
            }
        });
    }

    // ===== Opening and leaving an episode =====

    /// <summary>
    /// Works out whether <paramref name="path"/> is an episode (and its neighbours), reads its
    /// segments and the show's choices, and queues it for analysis first. Called before playback starts.
    /// </summary>
    private async Task PrepareEpisodeAsync(string path)
    {
        ResetEpisodeState();
        if (!_openingByAutoplay)
        {
            _autoplayRun = 0;
        }

        _openingByAutoplay = false;
        if (_services is not { } services)
        {
            return;
        }

        var load = ++_episodeLoad;
        var catalog = services.Shows.Catalog;
        if (!services.Shows.IsLoaded)
        {
            catalog = await Safely(() => services.Shows.RefreshAsync(), catalog);
        }

        var series = catalog.SeriesOf(path);
        var episode = catalog.EpisodeOf(path);
        var copy = episode?.Group.All.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        var fingerprint = copy?.Fingerprint ?? await Task.Run(() => FileFingerprint.Compute(path));
        var segments = EpisodeSegments.None;
        if (fingerprint is { } file)
        {
            segments = await Safely(() => services.Series.GetSegmentsAsync(file), EpisodeSegments.None);
        }

        var preferences = SeriesPreferences.Default;
        if (series is { } show)
        {
            preferences = await Safely(() => services.Series.GetPreferencesAsync(show.Key), SeriesPreferences.Default);
        }

        if (load != _episodeLoad)
        {
            return;
        }

        _series = series;
        _episode = episode;
        _nextEpisode = catalog.Next(path);
        _previousEpisode = catalog.Previous(path);
        _fingerprint = fingerprint;
        _showPreferences = preferences;
        _cues = new EpisodeCueTracker(segments, preferences.Resolve(services.Settings.Current), _nextEpisode is not null);
        if (series is not null && episode is not null)
        {
            var item = copy ?? episode.Item;
            Title = item.EpisodeTitle is { Length: > 0 } name ? $"{series.Title} · {episode.Label} · {name}" : $"{series.Title} · {episode.Label}";
            services.Analysis.Prioritize(path);
        }

        UpdateEpisodeButtons();
    }

    private void ResetEpisodeState()
    {
        _countdown.Cancel();
        _undoTimer?.Stop();
        _offered = null;
        _undoSegment = null;
        _nextUpShown = NextUpMoment.None;
        _nextUpDismissed = NextUpMoment.None;
        _countdownCancelled = false;
        _markedWatched = false;
        _draftStart = null;
        _draftEnd = null;
        _cues = null;
        _series = null;
        _episode = null;
        _nextEpisode = null;
        _previousEpisode = null;
        _fingerprint = null;
        _showPreferences = SeriesPreferences.Default;
        SkipButton.Visibility = Visibility.Collapsed;
        NextUpCard.Visibility = Visibility.Collapsed;
        SetSegmentsVisible(false);
        UpdateEpisodeButtons();
    }

    /// <summary>True when the open episode was marked watched, so opening it again starts at the beginning.</summary>
    private async Task<bool> WasMarkedWatchedAsync(string path)
    {
        if (_services is not { } services)
        {
            return false;
        }

        var marks = await Safely(() => services.ItemState.GetWatchedOverridesAsync(), new Dictionary<string, bool>());
        return marks.TryGetValue(path, out var watched) && watched;
    }

    private void UpdateEpisodeButtons()
    {
        var map = _shortcuts();
        NextEpisodeButton.Visibility = _nextEpisode is not null && !_isMini ? Visibility.Visible : Visibility.Collapsed;
        if (_nextEpisode is { } next)
        {
            AutomationName(NextEpisodeButton, "Next episode", map.WithKey($"Next episode: {next.Label}", PlayerCommand.NextEpisode));
        }

        PreviousEpisodeItem.KeyboardAcceleratorTextOverride = map.Label(PlayerCommand.PreviousEpisode) ?? string.Empty;
    }

    // ===== Following playback =====

    /// <summary>Called on every tick and status change: offers Skip, skips when allowed, shows the next episode.</summary>
    private void UpdateEpisodeCues()
    {
        if (_engine is null || _cues is null || _asking || _engine.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended))
        {
            return;
        }

        var ended = _engine.Status == PlaybackStatus.Ended;
        var duration = _engine.Duration;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var state = _cues.Evaluate(ended ? duration : _engine.Position, duration, ended);
        if (state.SkipNow is { } segment && _engine.Status == PlaybackStatus.Playing)
        {
            SkipSegment(segment, automatic: true);
            return;
        }

        ShowOffer(_undoSegment is null ? state.Offer : null);
        ShowNextUp(state.NextUp);
    }

    private void ShowOffer(MediaSegment? segment)
    {
        if (_undoSegment is not null)
        {
            return;
        }

        if (segment is null)
        {
            _offered = null;
            SkipButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (segment == _offered && SkipButton.Visibility == Visibility.Visible)
        {
            return;
        }

        _offered = segment;
        var text = $"Skip {MediaSegment.Label(segment.Kind).ToLowerInvariant()}";
        SkipButton.Content = text;
        AutomationName(SkipButton, text, _shortcuts().WithKey(text, PlayerCommand.SkipSegment));
        SkipButton.Visibility = Visibility.Visible;
    }

    private void SkipSegment(MediaSegment segment, bool automatic)
    {
        if (_engine is null)
        {
            return;
        }

        _engine.Seek(segment.End);
        _offered = null;
        UpdateTimelineNow();
        if (automatic)
        {
            // Skipped without asking: for a few seconds, one press takes it back.
            _undoSegment = segment;
            var name = MediaSegment.Label(segment.Kind).ToLowerInvariant();
            SkipButton.Content = $"Watch the {name}";
            AutomationName(SkipButton, $"Watch the {name}", $"The {name} was skipped. Go back to watch it.");
            SkipButton.Visibility = Visibility.Visible;
            _undoTimer?.Stop();
            _undoTimer?.Start();
        }
        else
        {
            SkipButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The Skip key: skips what is offered, or takes back an automatic skip. False when there is nothing.</summary>
    private bool SkipOrUndo()
    {
        if (_undoSegment is { } skipped && _engine is not null)
        {
            _undoSegment = null;
            _undoTimer?.Stop();
            SkipButton.Visibility = Visibility.Collapsed;
            _engine.Seek(skipped.Start);
            UpdateTimelineNow();
            return true;
        }

        if (_offered is { } segment)
        {
            SkipSegment(segment, automatic: false);
            return true;
        }

        return false;
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        _autoplayRun = 0;
        SkipOrUndo();
        Focus(FocusState.Programmatic);
    }

    // ===== Next episode =====

    private void ShowNextUp(NextUpMoment moment)
    {
        if (_nextEpisode is not { } next || _cues is null || _engine is null)
        {
            NextUpCard.Visibility = Visibility.Collapsed;
            return;
        }

        var dismissed = moment == NextUpMoment.Credits ? _nextUpDismissed != NextUpMoment.None : moment == NextUpMoment.Ended && _nextUpDismissed == NextUpMoment.Ended;
        if (moment == NextUpMoment.None || dismissed)
        {
            _countdown.Cancel();
            _nextUpShown = moment == NextUpMoment.None ? NextUpMoment.None : _nextUpShown;
            NextUpCard.Visibility = Visibility.Collapsed;
            return;
        }

        if (_nextUpShown != moment)
        {
            _nextUpShown = moment;
            var item = next.PreferredCopy(_path);
            NextUpTitle.Text = item.EpisodeTitle is { Length: > 0 } name ? $"{next.Label} · {name}" : next.Label;
            NextUpCard.Visibility = Visibility.Visible;
            if (moment == NextUpMoment.Credits && _cues.Credits is { Confidence: >= SegmentPolicy.AutoSkipThreshold })
            {
                _ = MarkWatchedAsync();
            }

            if (_engine.Status != PlaybackStatus.Paused && _cues.ShouldCountDown(moment, _countdownCancelled, _autoplayRun))
            {
                _countdown.Start(_cues.Rules.Countdown);
            }
            else
            {
                _countdown.Cancel();
            }
        }

        // Pausing, or turning autoplay off, means the user is deciding: stop counting.
        if (_countdown.IsRunning && (_engine.Status == PlaybackStatus.Paused || !_cues.Rules.Autoplay))
        {
            _countdown.Cancel();
            _countdownCancelled = true;
        }

        if (_countdown.IsDue)
        {
            _countdown.Cancel();
            _ = PlayNextAsync(automatic: true);
            return;
        }

        var counting = _countdown.IsRunning;
        NextUpCaption.Text = counting ? $"Next episode in {_countdown.SecondsLeft} s" : "Next episode";
        NextUpProgress.Visibility = counting ? Visibility.Visible : Visibility.Collapsed;
        NextUpProgress.Value = _countdown.Fraction;
        NextUpCancelButton.Content = counting ? "Cancel" : "Close";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NextUpCancelButton, counting ? "Cancel the next episode" : "Close");
        if (_autoplayRun >= Autoplay.ConsecutiveLimit && moment == NextUpMoment.Ended)
        {
            NextUpCaption.Text = "Still watching? Next episode";
        }
    }

    /// <summary>Stops the countdown and puts the card away for this episode (Cancel, Close or Esc).</summary>
    private void DismissNextUp()
    {
        _countdown.Cancel();
        _countdownCancelled = true;
        _nextUpDismissed = _nextUpShown;
        NextUpCard.Visibility = Visibility.Collapsed;
    }

    private async Task PlayNextAsync(bool automatic)
    {
        if (_nextEpisode is not { } next || _path is not { } path)
        {
            ShowToast(_episode is null ? "Next episode works for shows in your library." : "This is the last episode.", ToastDuration);
            return;
        }

        if (automatic)
        {
            _autoplayRun++;
            _openingByAutoplay = true;
        }
        else
        {
            _autoplayRun = 0;
        }

        // Moving on from the credits or the end means this one is finished.
        if (_nextUpShown != NextUpMoment.None)
        {
            await MarkWatchedAsync();
        }

        _countdown.Cancel();
        NextUpCard.Visibility = Visibility.Collapsed;
        EpisodeRequested?.Invoke(this, next.PreferredCopy(path).Path);
    }

    private void PlayPrevious()
    {
        if (_previousEpisode is not { } previous || _path is not { } path)
        {
            ShowToast(_episode is null ? "Previous episode works for shows in your library." : "This is the first episode.", ToastDuration);
            return;
        }

        _autoplayRun = 0;
        EpisodeRequested?.Invoke(this, previous.PreferredCopy(path).Path);
    }

    private async Task MarkWatchedAsync()
    {
        if (_markedWatched || _services is not { } services || _path is not { } path || _episode is null)
        {
            return;
        }

        _markedWatched = true;
        await Safely(async () => { await services.Library.SetWatchedAsync([path], true); return true; }, false);
    }

    private async void OnNextUpPlayClick(object sender, RoutedEventArgs e) => await PlayNextAsync(automatic: false);

    private void OnNextUpCancelClick(object sender, RoutedEventArgs e)
    {
        DismissNextUp();
        Focus(FocusState.Programmatic);
    }

    private async void OnNextEpisodeClick(object sender, RoutedEventArgs e) => await PlayNextAsync(automatic: false);

    private void OnPreviousEpisodeClick(object sender, RoutedEventArgs e) => PlayPrevious();

    // The controls have no height until their first layout, which can come after the overlay was shown.
    private void OnControlsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSubtitleLift();
        UpdateCueLift();
    }

    /// <summary>Keeps Skip and the next-episode card clear of the controls while those show.</summary>
    private void UpdateCueLift()
    {
        var side = _isMini ? 8 : 24;
        var bottom = _isMini ? 8.0 : 32.0;
        if (_overlay.IsVisible && ControlsPanel.Visibility == Visibility.Visible && ControlsPanel.ActualHeight > 0)
        {
            bottom = ControlsPanel.ActualHeight + ControlsPanel.Margin.Bottom + 12;
        }

        CueArea.Margin = new Thickness(side, 0, side, bottom);
    }

    // ===== This show =====

    private void OnMoreMenuOpening(object sender, object e)
    {
        var isEpisode = _series is not null;
        ShowMenu.Visibility = isEpisode ? Visibility.Visible : Visibility.Collapsed;
        PreviousEpisodeItem.Visibility = _previousEpisode is not null ? Visibility.Visible : Visibility.Collapsed;
        EpisodeSeparator.Visibility = isEpisode ? Visibility.Visible : Visibility.Collapsed;
        SegmentsMenuItem.IsChecked = SegmentsPanel.Visibility == Visibility.Visible;
        SegmentsMenuItem.IsEnabled = _fingerprint is not null;
        if (!isEpisode || _cues is null)
        {
            return;
        }

        var rules = _cues.Rules;
        ShowMenu.Text = $"This show: {_series!.Title}";
        ShowAutoplayItem.IsChecked = rules.Autoplay;
        FillModes(ShowIntroMenu, "Intro", rules.Intro, mode => _showPreferences with { Intro = mode });
        FillModes(ShowRecapMenu, "Recap", rules.Recap, mode => _showPreferences with { Recap = mode });
        ShowResetItem.IsEnabled = !_showPreferences.IsDefault;

        void FillModes(MenuFlyoutSubItem menu, string group, SkipMode current, Func<SkipMode, SeriesPreferences> change)
        {
            menu.Items.Clear();
            foreach (var mode in (SkipMode[])[SkipMode.Button, SkipMode.Automatic, SkipMode.Never])
            {
                var item = new RadioMenuFlyoutItem { Text = EpisodeRules.Label(mode), GroupName = group, IsChecked = mode == current };
                item.Click += async (_, _) => await SaveShowPreferencesAsync(change(mode));
                menu.Items.Add(item);
            }
        }
    }

    private async void OnShowAutoplayClick(object sender, RoutedEventArgs e) =>
        await SaveShowPreferencesAsync(_showPreferences with { Autoplay = ShowAutoplayItem.IsChecked });

    private async void OnShowResetClick(object sender, RoutedEventArgs e) => await SaveShowPreferencesAsync(SeriesPreferences.Default);

    private async Task SaveShowPreferencesAsync(SeriesPreferences preferences)
    {
        if (_services is not { } services || _series is not { } series || _cues is null)
        {
            return;
        }

        _showPreferences = preferences;
        var rules = preferences.Resolve(services.Settings.Current);
        _cues.SetRules(rules);
        await Safely(async () => { await services.Series.SavePreferencesAsync(series.Key, preferences); return true; }, false);
        ShowToast(rules.Autoplay ? $"{series.Title}: next episodes play automatically" : $"{series.Title}: next episodes wait for you", ToastDuration);
        UpdateEpisodeCues();
    }

    /// <summary>Settings changed while watching: the show's own choices still win.</summary>
    private void ApplyEpisodeSettings(AppSettings settings) => _cues?.SetRules(_showPreferences.Resolve(settings));

    // ===== Intro and credits panel =====

    private void SetSegmentsVisible(bool visible)
    {
        if (_isMini || _engine is null || _fingerprint is null)
        {
            visible = false;
        }

        SegmentsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SegmentsMenuItem.IsChecked = visible;
        if (visible)
        {
            SetAdjustVisible(false);
            if (InfoPanel.Visibility == Visibility.Visible)
            {
                SetInfoVisible(Root.ActualWidth >= 800);
            }

            // Open on the part playing now, if any; otherwise the intro.
            var position = _engine!.Position;
            var current = (SegmentKind[])[SegmentKind.Recap, SegmentKind.Intro, SegmentKind.Credits];
            var playing = current.FirstOrDefault(k => _cues?.Segments.Effective(k)?.Contains(position) == true, SegmentKind.Intro);
            _draftStart = null;
            _draftEnd = null;
            SelectKind(playing);
            RenderSegmentsPanel(null);
        }
        else
        {
            _focusInPanel = false;
        }
    }

    private void SelectKind(SegmentKind kind)
    {
        _editKind = kind;
        SegmentKindBox.SelectedItem = SegmentKindBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SegmentKind k && k == kind);
    }

    private void OnSegmentKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SegmentKindBox.SelectedItem is ComboBoxItem { Tag: SegmentKind kind } && kind != _editKind)
        {
            _editKind = kind;
            _draftStart = null;
            _draftEnd = null;
            RenderSegmentsPanel(null);
        }
    }

    private void RenderSegmentsPanel(string? message)
    {
        var segments = _cues?.Segments ?? EpisodeSegments.None;
        var name = MediaSegment.Label(_editKind).ToLowerInvariant();
        var effective = segments.Effective(_editKind);
        var correction = segments.CorrectionFor(_editKind);

        SegmentTimesText.Text = effective is not null
            ? $"{TimeFormat.Clock(effective.Start)} – {TimeFormat.Clock(effective.End)}"
            : correction is { IsNone: true } ? $"No {name} in this video" : $"No {name} found";

        SegmentSourceText.Text = (effective, correction) switch
        {
            (_, { IsNone: true }) => "You said this video has none.",
            ({ Source: SegmentSource.Manual }, _) => "Set by you.",
            ({ } found, _) when found.Confidence < SegmentPolicy.OfferThreshold =>
                $"{found.Evidence ?? "Found by Aperture"}. Not offered, because Aperture isn’t sure enough.",
            ({ } found, _) => $"{found.Evidence ?? "Found by Aperture"}. {SegmentPolicy.DescribeConfidence(found.Confidence)}.",
            _ when _episode is not null => "Aperture compares this episode with others of the show in the background. You can also mark it yourself.",
            _ => "Only episodes in your library are compared with others. You can mark it yourself.",
        };

        SegmentHint.Text = message
            ?? (_draftStart is { } start ? $"Start marked at {TimeFormat.Clock(start)}. Go to where it ends and press Ends here."
            : _draftEnd is { } end ? $"End marked at {TimeFormat.Clock(end)}. Go to where it starts and press Starts here."
            : "Pause where it starts or ends, then press the button. Saved for this file only; other releases keep their own times.");

        WatchSegmentButton.IsEnabled = effective is not null;
        ConfirmSegmentButton.Visibility = effective is { Source: not SegmentSource.Manual } ? Visibility.Visible : Visibility.Collapsed;
        NoSegmentButton.Visibility = correction is { IsNone: true } ? Visibility.Collapsed : Visibility.Visible;
        NoSegmentButton.Content = $"There’s no {name}";
        ResetSegmentButton.Visibility = correction is not null ? Visibility.Visible : Visibility.Collapsed;
        ResetSegmentButton.Content = segments.DetectedFor(_editKind) is not null ? "Use what Aperture found" : "Forget my times";
    }

    private async void OnMarkStartClick(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            return;
        }

        var now = _engine.Position;
        var end = _draftEnd ?? _cues?.Segments.Effective(_editKind)?.End;
        if (end is { } e2 && e2 - now >= SegmentCorrection.MinimumLength)
        {
            await SaveCorrectionAsync(new SegmentCorrection(_editKind, now, e2));
            return;
        }

        _draftStart = now;
        _draftEnd = null;
        RenderSegmentsPanel(null);
    }

    private async void OnMarkEndClick(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            return;
        }

        var now = _engine.Status == PlaybackStatus.Ended ? _engine.Duration : _engine.Position;
        var start = _draftStart ?? _cues?.Segments.Effective(_editKind)?.Start;
        if (start is { } s && now - s >= SegmentCorrection.MinimumLength)
        {
            await SaveCorrectionAsync(new SegmentCorrection(_editKind, s, now));
            return;
        }

        _draftEnd = now;
        _draftStart = null;
        RenderSegmentsPanel(null);
    }

    private async void OnConfirmSegmentClick(object sender, RoutedEventArgs e)
    {
        if (_cues?.Segments.Effective(_editKind) is { } found)
        {
            await SaveCorrectionAsync(new SegmentCorrection(_editKind, found.Start, found.End));
        }
    }

    private async void OnNoSegmentClick(object sender, RoutedEventArgs e) =>
        await SaveCorrectionAsync(new SegmentCorrection(_editKind, null, null));

    private async void OnResetSegmentClick(object sender, RoutedEventArgs e)
    {
        if (_services is not { } services || _fingerprint is not { } fingerprint)
        {
            return;
        }

        var kind = _editKind;
        await Safely(async () => { await services.Series.ClearCorrectionAsync(fingerprint, kind); return true; }, false);
        _draftStart = null;
        _draftEnd = null;
        await ReloadSegmentsAsync();
        RenderSegmentsPanel("Your times were removed.");
    }

    private void OnWatchSegmentClick(object sender, RoutedEventArgs e)
    {
        if (_engine is not null && _cues?.Segments.Effective(_editKind) is { } found)
        {
            // A moment before, so the boundary itself can be judged.
            _engine.Seek(found.Start > TimeSpan.FromSeconds(2) ? found.Start - TimeSpan.FromSeconds(2) : TimeSpan.Zero);
            UpdateTimelineNow();
        }
    }

    private async Task SaveCorrectionAsync(SegmentCorrection correction)
    {
        if (_services is not { } services || _fingerprint is not { } fingerprint || _engine is null)
        {
            return;
        }

        if (!correction.IsNone && SegmentCorrection.Validate(correction.Start!.Value, correction.End!.Value, _engine.Duration) is { } problem)
        {
            RenderSegmentsPanel(problem);
            return;
        }

        var saved = await Safely(async () => { await services.Series.SaveCorrectionAsync(fingerprint, correction); return true; }, false);
        _draftStart = null;
        _draftEnd = null;
        await ReloadSegmentsAsync();
        RenderSegmentsPanel(saved ? "Saved for this file." : "Couldn’t save. Details are in Aperture’s log.");
    }

    private async Task ReloadSegmentsAsync()
    {
        if (_services is not { } services || _fingerprint is not { } fingerprint)
        {
            return;
        }

        var segments = await Safely(() => services.Series.GetSegmentsAsync(fingerprint), EpisodeSegments.None);
        if (fingerprint != _fingerprint || _cues is null)
        {
            return;
        }

        _cues.SetSegments(segments);
        if (SegmentsPanel.Visibility == Visibility.Visible)
        {
            RenderSegmentsPanel(null);
        }

        UpdateEpisodeCues();
    }

    private void OnSegmentsMenuClick(object sender, RoutedEventArgs e) => SetSegmentsVisible(SegmentsMenuItem.IsChecked);

    private void OnSegmentsCloseClick(object sender, RoutedEventArgs e)
    {
        SetSegmentsVisible(false);
        Focus(FocusState.Programmatic);
    }
}
