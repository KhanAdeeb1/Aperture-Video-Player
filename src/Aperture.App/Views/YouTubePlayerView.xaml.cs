using System.Text.Json;
using Aperture.App.Services;
using Aperture.Core.Playback;
using Aperture.Core.Series;
using Aperture.Core.Settings;
using Aperture.Core.YouTube;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Aperture.App.Views;

/// <summary>
/// Plays YouTube videos in YouTube's own embedded player (IFrame Player API in WebView2).
/// Aperture only tells the player which video to load and where to start, listens to its state
/// for Continue Watching and autoplay, and pauses it whenever it can't be seen. It never reads
/// stream addresses, changes the player, or plays anything in the background. Local playback
/// (PlayerView and libmpv) is not involved.
/// </summary>
public sealed partial class YouTubePlayerView : UserControl
{
    /// <summary>
    /// The application identifier YouTube sees as the Referer of every player request:
    /// the page is served from https://com.aperture.player/ by a WebView2 virtual host.
    /// </summary>
    public const string HostName = "com.aperture.player";

    private static readonly Uri PageUri = new($"https://{HostName}/player.html");

    /// <summary>How long the next video waits when YouTube autoplay is on.</summary>
    private static readonly TimeSpan NextCountdown = TimeSpan.FromSeconds(10);

    /// <summary>Positions are saved this often while playing, and on pause, end and close.</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);

    private readonly NextEpisodeCountdown _countdown = new(TimeProvider.System);
    private AppServices? _services;
    private Task<bool>? _webStart;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _countdownTimer;
    private string? _videoId;
    private YouTubeVideo? _video;
    private IReadOnlyList<YouTubeVideo> _queue = [];
    private int _index = -1;
    private TimeSpan _position;
    private TimeSpan _duration;
    private DateTimeOffset _lastSaved;
    private bool _started;
    private bool _fullscreen;
    private bool _visibleToUser = true;
    private int _openGeneration;

    public YouTubePlayerView()
    {
        InitializeComponent();
        DetailsRow.SizeChanged += (_, _) => FitPlayer();
        NextCard.SizeChanged += (_, _) => FitPlayer();
        SizeChanged += (_, e) => QueuePanel.Visibility = _queue.Count > _index + 1 && e.NewSize.Width >= 960 && !_fullscreen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>YouTube's player asked for (or left) full screen; the window follows.</summary>
    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>The video was saved or removed from Saved, so the YouTube page can refresh.</summary>
    public event EventHandler? RecordsChanged;

    /// <summary>The video's title for the window's title bar.</summary>
    public string Title => _video?.Title ?? "YouTube";

    internal void Initialize(AppServices services)
    {
        _services = services;
        AutoplaySwitch.IsOn = services.Settings.Current.YouTubeAutoplay;
    }

    /// <summary>Settings changed elsewhere (the autoplay switch lives in both places).</summary>
    internal void ApplySettings(AppSettings settings) => AutoplaySwitch.IsOn = settings.YouTubeAutoplay;

    /// <summary>
    /// Opens a video. <paramref name="queue"/> is the list it was chosen from (search results, a
    /// shelf, a playlist): the rest of it is Up next. A link's own start time wins over the saved
    /// position; <paramref name="fromStart"/> ignores both.
    /// </summary>
    internal async Task OpenAsync(string videoId, YouTubeVideo? video, TimeSpan linkStart, IReadOnlyList<YouTubeVideo> queue, bool fromStart = false)
    {
        if (_services is null)
        {
            return;
        }

        await SaveProgressAsync();
        var generation = ++_openGeneration;
        CancelNext();
        _videoId = videoId;
        _video = video;
        _queue = queue;
        _index = queue.ToList().FindIndex(v => v.Id == videoId);
        _position = TimeSpan.Zero;
        _duration = video?.Duration ?? TimeSpan.Zero;
        _started = false;
        ShowDetails();
        ShowQueue();
        ProblemPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;

        // Check first, so a video YouTube won't play here gets a clear message instead of a broken player.
        var playability = video?.Status is { } status
            ? YouTubePlayabilityRules.Evaluate(status, _services.YouTube.Region)
            : YouTubePlayability.Playable;
        if (video?.Status is null)
        {
            var lookup = await _services.YouTube.LookupAsync(videoId, CancellationToken.None);
            if (generation != _openGeneration)
            {
                return;
            }

            playability = lookup.Playability;
            _video = lookup.Video ?? video;
            _duration = _video?.Duration ?? _duration;
            ShowDetails();
        }

        if (playability != YouTubePlayability.Playable)
        {
            AppLog.Info($"YouTube video not played here: {playability}.");
            ShowProblem(YouTubePlayabilityRules.Describe(playability));
            return;
        }

        YouTubeEntry? entry = null;
        try
        {
            entry = await _services.YouTube.GetEntryAsync(videoId, CancellationToken.None);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not read the YouTube position", error);
        }

        var start = fromStart ? TimeSpan.Zero : YouTubeDataRules.StartPosition(entry, linkStart);
        if (!await EnsureWebAsync() || generation != _openGeneration)
        {
            return;
        }

        Post(new { cmd = "load", id = videoId, start = Math.Floor(start.TotalSeconds), autoplay = _visibleToUser });
        Web.Visibility = Visibility.Visible;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        RefreshSaveButton(entry);
    }

    /// <summary>Stops the video and saves where it was. Called when leaving the player and when the window closes.</summary>
    internal async Task CloseAsync()
    {
        _openGeneration++;
        CancelNext();
        Post(new { cmd = "stop" });
        await SaveProgressAsync();
        _videoId = null;
        Web.Visibility = Visibility.Collapsed;
        ProblemPanel.Visibility = Visibility.Collapsed;
        if (_fullscreen)
        {
            SetFullscreenLayout(false);
            FullscreenChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// The window was minimised or hidden: pause (YouTube's policies don't allow playing in the
    /// background) and stop any countdown, since a video must not start where it can't be seen.
    /// </summary>
    internal void SetVisibleToUser(bool visible)
    {
        _visibleToUser = visible;
        if (!visible && _videoId is not null)
        {
            Post(new { cmd = "pause" });
            if (_countdown.IsRunning)
            {
                StopCountdown("Up next");
            }
        }
    }

    /// <summary>Leaves YouTube's full screen (when the window left full screen some other way, such as Esc).</summary>
    internal void ExitFullscreen()
    {
        if (Web.CoreWebView2 is { ContainsFullScreenElement: true } core)
        {
            _ = core.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()");
        }
    }

    /// <summary>Deletes the player's cookies and cache (part of "Delete YouTube data").</summary>
    internal async Task ClearBrowsingDataAsync()
    {
        if (Web.CoreWebView2 is { } core)
        {
            await core.Profile.ClearBrowsingDataAsync();
            return;
        }

        if (_services is null)
        {
            return;
        }

        // WebView2 hasn't started this session, so nothing holds its folder open.
        var folder = Path.Combine(_services.DataLocation.Root, "WebView2");
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Could not delete the YouTube player's browsing data", error);
        }
    }

    // ===== WebView2 and the page =====

    /// <summary>Starts WebView2 once, with its data in Aperture's folder, and loads the player page.</summary>
    private Task<bool> EnsureWebAsync() => _webStart ??= StartWebAsync();

    private async Task<bool> StartWebAsync()
    {
        try
        {
            var folder = Path.Combine(_services!.DataLocation.Root, "WebView2");
            var options = new CoreWebView2EnvironmentOptions
            {
                // The user chose to play the video in Aperture; the player starts it without a second click.
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, options);
            await Web.EnsureCoreWebView2Async(environment);
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "Assets", "YouTube"), CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.ContainsFullScreenElementChanged += (_, _) =>
            {
                var full = core.ContainsFullScreenElement;
                SetFullscreenLayout(full);
                FullscreenChanged?.Invoke(this, full);
            };

            var loaded = new TaskCompletionSource<bool>();
            void OnLoaded(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                core.NavigationCompleted -= OnLoaded;
                loaded.TrySetResult(args.IsSuccess);
            }

            core.NavigationCompleted += OnLoaded;
            core.Navigate(PageUri.ToString());
            if (!await loaded.Task)
            {
                AppLog.Error("The YouTube player page didn't load.");
                ShowProblem(("The YouTube player couldn’t start", "Aperture’s player page didn’t load. Details are in Aperture’s log."));
                _webStart = null;
                return false;
            }

            AppLog.Info($"YouTube player host ready (WebView2 {core.Environment.BrowserVersionString}).");
            return true;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("WebView2 could not start for the YouTube player", error);
            ShowProblem(("YouTube needs Microsoft Edge WebView2", "YouTube videos play in YouTube’s own player, which Windows provides through the WebView2 Runtime. Install or repair it from Microsoft, then try again. Your own videos aren’t affected."));
            _webStart = null;
            return false;
        }
    }

    /// <summary>Only Aperture's own page may load in the top frame; links leave for the browser.</summary>
    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        args.Cancel = true;
        OpenExternally(args.Uri);
    }

    /// <summary>The YouTube logo, title and "Watch on YouTube" in the player open the system browser (or YouTube's app), as YouTube requires.</summary>
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        OpenExternally(args.Uri);
    }

    private static void OpenExternally(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void Post(object message)
    {
        if (Web.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
    }

    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // Messages come only from Aperture's own page; anything else is ignored.
        if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) || !source.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string type;
        double time = 0, duration = 0;
        int state = 0, code = 0;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            type = root.GetProperty("type").GetString() ?? string.Empty;
            if (root.TryGetProperty("time", out var t) && t.TryGetDouble(out var tv))
            {
                time = tv;
            }

            if (root.TryGetProperty("duration", out var d) && d.TryGetDouble(out var dv))
            {
                duration = dv;
            }

            if (root.TryGetProperty("state", out var s) && s.TryGetInt32(out var sv))
            {
                state = sv;
            }

            if (root.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv))
            {
                code = cv;
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Warning("The YouTube player page sent a message Aperture couldn't read.", error);
            return;
        }

        if (_videoId is null)
        {
            return;
        }

        if (time > 0)
        {
            _position = TimeSpan.FromSeconds(time);
        }

        if (duration > 0)
        {
            _duration = TimeSpan.FromSeconds(duration);
        }

        switch (type)
        {
            case "state":
                await OnPlayerStateAsync(state);
                break;
            case "time" when DateTimeOffset.UtcNow - _lastSaved >= SaveInterval:
                await SaveProgressAsync();
                break;
            case "error":
                var reason = YouTubePlayabilityRules.FromPlayerError(code);
                AppLog.Warning($"YouTube player error {code} ({reason}).");
                ShowProblem(YouTubePlayabilityRules.Describe(reason));
                break;
            case "apiFailed":
                AppLog.Warning("YouTube's player script couldn't be loaded.");
                ShowProblem(("YouTube can’t be reached", "Check your connection, then try again. Your own videos aren’t affected."));
                break;
            case "autoplayBlocked":
                AppLog.Info("YouTube player: autoplay was blocked; YouTube shows its play button.");
                break;
        }
    }

    /// <summary>YT.PlayerState: -1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued.</summary>
    private async Task OnPlayerStateAsync(int state)
    {
        switch (state)
        {
            case 1:
                if (!_started)
                {
                    _started = true;
                    AppLog.Info("YouTube player: playing.");
                }

                if (!_visibleToUser)
                {
                    Post(new { cmd = "pause" });
                }

                CancelNext();
                break;
            case 2:
                await SaveProgressAsync();
                break;
            case 0:
                AppLog.Info("YouTube player: ended.");
                _position = _duration;
                await SaveProgressAsync();
                OfferNext();
                break;
        }
    }

    // ===== Records =====

    private async Task SaveProgressAsync()
    {
        if (_services is null || _videoId is not { } id || !_started || !_services.Settings.Current.RememberHistory)
        {
            return;
        }

        _lastSaved = DateTimeOffset.UtcNow;
        try
        {
            await _services.YouTube.RecordProgressAsync(id, _video, _position, _duration, CancellationToken.None);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save the YouTube position", error);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _videoId is not { } id)
        {
            return;
        }

        try
        {
            var entry = await _services.YouTube.GetEntryAsync(id, CancellationToken.None);
            if (entry?.SavedUtc is not null)
            {
                await _services.YouTube.UnsaveAsync(id, CancellationToken.None);
            }
            else
            {
                await _services.YouTube.SaveAsync(_video ?? new YouTubeVideo(id, "YouTube video", string.Empty, YouTubeVideo.DefaultThumbnail(id), null), CancellationToken.None);
            }

            RefreshSaveButton(await _services.YouTube.GetEntryAsync(id, CancellationToken.None));
            RecordsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save the YouTube video for later", error);
        }
    }

    private void RefreshSaveButton(YouTubeEntry? entry)
    {
        var saved = entry?.SavedUtc is not null;
        SaveLabel.Text = saved ? "Saved" : "Save";
        SaveGlyph.Glyph = saved ? "" : "";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SaveButton, saved ? "Remove from Saved" : "Save for later");
        ToolTipService.SetToolTip(SaveButton, saved ? "Remove from Saved" : "Save for later in Aperture");
    }

    private void OnOpenOnYouTubeClick(object sender, RoutedEventArgs e)
    {
        if (_videoId is { } id)
        {
            Post(new { cmd = "pause" });
            _ = Windows.System.Launcher.LaunchUriAsync(YouTubeLink.WatchUri(id, _position));
        }
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (_videoId is { } id)
        {
            var package = new DataPackage();
            package.SetText(YouTubeLink.WatchUri(id).ToString());
            Clipboard.SetContent(package);
            ToolTipService.SetToolTip(CopyButton, "Link copied");
        }
    }

    // ===== Layout =====

    private void ShowDetails()
    {
        TitleText.Text = _video?.Title ?? "YouTube video";
        var parts = new List<string>(2);
        if (_video?.Channel is { Length: > 0 } channel)
        {
            parts.Add(channel);
        }

        if (_video?.Status?.Live == YouTubeLiveState.Live)
        {
            parts.Add("Live");
        }
        else if (_video?.Duration is { } length)
        {
            parts.Add(TimeFormat.Length(length));
        }

        ChannelText.Text = string.Join(" · ", parts);
        ToolTipService.SetToolTip(CopyButton, "Copy link");
    }

    private void ShowProblem((string Title, string Detail) problem)
    {
        // The player is taken away rather than covered: nothing is drawn over YouTube's player.
        Post(new { cmd = "stop" });
        Web.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ProblemTitle.Text = problem.Title;
        ProblemDetail.Text = problem.Detail;
        ProblemOpenButton.Visibility = _videoId is null ? Visibility.Collapsed : Visibility.Visible;
        ProblemPanel.Visibility = Visibility.Visible;
    }

    private void ShowQueue()
    {
        QueueList.Children.Clear();
        var upcoming = _queue.Skip(_index + 1).Take(12).ToList();
        foreach (var video in upcoming)
        {
            QueueList.Children.Add(QueueRow(video));
        }

        QueuePanel.Visibility = upcoming.Count > 0 && ActualWidth >= 960 && !_fullscreen ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button QueueRow(YouTubeVideo video)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var thumbnail = new Border
        {
            Width = 128,
            Height = 72,
            CornerRadius = (CornerRadius)Application.Current.Resources["RadiusSmall"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ArtworkEmptyBrush"],
            Child = new Image
            {
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Source = Uri.TryCreate(video.ThumbnailUrl, UriKind.Absolute, out var uri) ? new BitmapImage(uri) { DecodePixelWidth = 160 } : null,
            },
        };
        grid.Children.Add(thumbnail);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = video.Title, MaxLines = 2, TextWrapping = TextWrapping.WrapWholeWords, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["BodyTextStyle"] });
        text.Children.Add(new TextBlock { Text = video.Channel, Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = new Button
        {
            Content = grid,
            Style = (Style)Application.Current.Resources["CardButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{video.Title}, {video.Channel}");
        button.Click += async (_, _) => await OpenAsync(video.Id, video, TimeSpan.Zero, _queue);
        return button;
    }

    private void OnMainAreaSizeChanged(object sender, SizeChangedEventArgs e) => FitPlayer();

    /// <summary>The largest 16:9 player that fits above the details (YouTube asks for at least 480 × 270 where possible).</summary>
    private void FitPlayer()
    {
        if (_fullscreen)
        {
            PlayerBox.Width = double.NaN;
            PlayerBox.Height = double.NaN;
            return;
        }

        var width = MainArea.ActualWidth;
        var height = MainArea.ActualHeight - DetailsRow.ActualHeight - DetailsRow.Margin.Top
            - (NextCard.Visibility == Visibility.Visible ? NextCard.ActualHeight + NextCard.Margin.Top : 0);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var playerWidth = Math.Floor(Math.Max(Math.Min(width, height * 16 / 9), Math.Min(width, 480)));
        PlayerBox.Width = playerWidth;
        PlayerBox.Height = Math.Floor(playerWidth * 9 / 16);
        DetailsRow.MaxWidth = playerWidth;
    }

    private void SetFullscreenLayout(bool fullscreen)
    {
        _fullscreen = fullscreen;
        var details = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        DetailsRow.Visibility = details;
        if (fullscreen)
        {
            NextCard.Visibility = Visibility.Collapsed;
        }

        MainArea.Margin = fullscreen ? new Thickness(0) : new Thickness(24, 8, 24, 24);
        PlayerBox.CornerRadius = fullscreen ? new CornerRadius(0) : (CornerRadius)Application.Current.Resources["RadiusMedium"];
        ShowQueue();
        FitPlayer();
    }

    // ===== Up next and YouTube autoplay =====

    private YouTubeVideo? NextVideo() =>
        _queue.Skip(_index + 1).FirstOrDefault(v => YouTubePlayabilityRules.Evaluate(v.Status, _services?.YouTube.Region) == YouTubePlayability.Playable);

    /// <summary>
    /// At the end of a video from a list: offer the next one. With YouTube autoplay on (and the
    /// window visible) it counts down and plays it; with autoplay off it only offers it.
    /// </summary>
    private void OfferNext()
    {
        if (_fullscreen || NextVideo() is not { } next)
        {
            return;
        }

        NextTitle.Text = next.Title;
        NextCard.Visibility = Visibility.Visible;
        CancelNextButton.Visibility = Visibility.Visible;
        if (_services?.Settings.Current.YouTubeAutoplay == true && _visibleToUser)
        {
            _countdown.Start(NextCountdown);
            NextProgress.Visibility = Visibility.Visible;
            UpdateCountdown();
            _countdownTimer ??= CreateCountdownTimer();
            _countdownTimer.Start();
        }
        else
        {
            StopCountdown("Up next");
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateCountdownTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += async (_, _) =>
        {
            if (!_countdown.IsRunning)
            {
                timer.Stop();
                return;
            }

            UpdateCountdown();
            if (_countdown.IsDue)
            {
                timer.Stop();
                await PlayNextAsync();
            }
        };
        return timer;
    }

    private void UpdateCountdown()
    {
        NextCaption.Text = $"Next video in {_countdown.SecondsLeft} s";
        NextProgress.Value = _countdown.Fraction;
    }

    private void StopCountdown(string caption)
    {
        _countdown.Cancel();
        _countdownTimer?.Stop();
        NextProgress.Visibility = Visibility.Collapsed;
        NextCaption.Text = caption;
    }

    private void CancelNext()
    {
        StopCountdown(string.Empty);
        NextCard.Visibility = Visibility.Collapsed;
    }

    private async Task PlayNextAsync()
    {
        if (NextVideo() is { } next)
        {
            CancelNext();
            AppLog.Info("YouTube autoplay: playing the next video in the list.");
            await OpenAsync(next.Id, next, TimeSpan.Zero, _queue);
        }
    }

    private async void OnPlayNextClick(object sender, RoutedEventArgs e) => await PlayNextAsync();

    private void OnCancelNextClick(object sender, RoutedEventArgs e) => CancelNext();

    private async void OnAutoplayToggled(object sender, RoutedEventArgs e)
    {
        if (_services is null || _services.Settings.Current.YouTubeAutoplay == AutoplaySwitch.IsOn)
        {
            return;
        }

        var updated = _services.Settings.Current with { YouTubeAutoplay = AutoplaySwitch.IsOn };
        _services.Settings.Current = updated;
        if (!updated.YouTubeAutoplay && _countdown.IsRunning)
        {
            StopCountdown("Up next");
        }

        try
        {
            await _services.SettingsStore.SaveAsync(updated);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save settings", error);
        }
    }
}
