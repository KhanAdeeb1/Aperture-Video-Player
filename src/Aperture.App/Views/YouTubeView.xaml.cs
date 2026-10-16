using Aperture.App.Services;
using Aperture.Core.YouTube;
using Aperture.YouTube;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace Aperture.App.Views;

/// <summary>A request to watch a YouTube video, with the list it came from (for Up next).</summary>
internal sealed record YouTubePlayRequest(string VideoId, YouTubeVideo? Video, TimeSpan Start, IReadOnlyList<YouTubeVideo> Queue, bool FromStart = false);

/// <summary>
/// The YouTube section: Continue watching and Saved (Aperture's own records), the signed-in
/// account's liked videos and playlists, YouTube's popular chart, and search results. Calls
/// that spend YouTube quota (search 100 units, the others 1) are made only when asked for or
/// once per visit, and their results are kept for the session.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A view lives as long as the window; each request's cancellation source is disposed when the next one replaces it.")]
public sealed partial class YouTubeView : UserControl
{
    private AppServices? _services;
    private CancellationTokenSource? _work;
    private string? _query;
    private string? _nextPage;
    private List<YouTubeCardModel> _results = [];
    private HashSet<string> _saved = new(StringComparer.Ordinal);
    private IReadOnlyList<YouTubeVideo>? _popular;
    private IReadOnlyList<YouTubeVideo>? _liked;
    private IReadOnlyList<YouTubePlaylist>? _playlists;
    private bool _maintained;
    private NoticeAction _noticeAction;

    public YouTubeView()
    {
        InitializeComponent();
        CardGrid.Fit(ResultsRepeater);
    }

    private enum NoticeAction
    {
        None,
        OpenSettings,
        Retry,
    }

    /// <summary>A video was chosen.</summary>
    internal event EventHandler<YouTubePlayRequest>? PlayRequested;

    /// <summary>The user wants to set up a key or sign in.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>The user declined the terms; the window goes back.</summary>
    public event EventHandler? Declined;

    /// <summary>True while results (search or a playlist) are shown instead of the shelves.</summary>
    public bool IsShowingResults => ResultsSection.Visibility == Visibility.Visible;

    internal void Initialize(AppServices services)
    {
        _services = services;
        services.YouTube.Account.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => ForgetCachedLists());
    }

    /// <summary>Key or sign-in changed in Settings: what can be shown changed with them.</summary>
    public void ForgetCachedLists()
    {
        _popular = null;
        _liked = null;
        _playlists = null;
    }

    /// <summary>Shows the section: the terms the first time, the shelves after that.</summary>
    public async Task ShowAsync()
    {
        if (_services is null)
        {
            return;
        }

        var accepted = _services.Settings.Current.YouTubeAccepted;
        ConsentScroller.Visibility = accepted ? Visibility.Collapsed : Visibility.Visible;
        MainScroller.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        if (!accepted)
        {
            AcceptButton.Focus(FocusState.Programmatic);
            return;
        }

        if (!IsShowingResults)
        {
            await RefreshShelvesAsync();
        }

        if (!_maintained)
        {
            _maintained = true;
            _ = MaintainAsync();
        }
    }

    /// <summary>
    /// What was typed in the search box and submitted: a YouTube link plays straight away; other
    /// text searches YouTube when a key or sign-in allows it.
    /// </summary>
    public async Task SubmitAsync(string text)
    {
        if (_services is null || !_services.Settings.Current.YouTubeAccepted || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (YouTubeLink.TryParse(text) is { } link)
        {
            PlayRequested?.Invoke(this, new YouTubePlayRequest(link.VideoId, null, link.Start, []));
            return;
        }

        if (!_services.YouTube.CanBrowse)
        {
            ShowNotice("Searching YouTube needs your own YouTube Data API key (free from Google). Links you paste play without one.", NoticeAction.OpenSettings, "Set up…");
            return;
        }

        _query = text.Trim();
        _nextPage = null;
        _results = [];
        await LoadResultsAsync(append: false);
    }

    /// <summary>Leaves search results or a playlist and shows the shelves again.</summary>
    public async Task ShowShelvesAsync()
    {
        _query = null;
        _results = [];
        ResultsRepeater.ItemsSource = null;
        ResultsSection.Visibility = Visibility.Collapsed;
        BackToShelvesButton.Visibility = Visibility.Collapsed;
        HideNotice();
        await RefreshShelvesAsync();
    }

    /// <summary>Moves focus to the first card, for keyboard users arriving from the search box.</summary>
    public void FocusFirstItem()
    {
        if (IsShowingResults && ResultsRepeater.TryGetElement(0) is YouTubeCard result)
        {
            result.FocusCard();
        }
        else if (FindFirstCard(ShelvesHost) is { } card)
        {
            card.FocusCard();
        }
    }

    // ===== Terms =====

    private async void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        var updated = _services.Settings.Current with { YouTubeAccepted = true };
        _services.Settings.Current = updated;
        try
        {
            await _services.SettingsStore.SaveAsync(updated);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save settings", error);
        }

        await ShowAsync();
    }

    private void OnNotNowClick(object sender, RoutedEventArgs e) => Declined?.Invoke(this, EventArgs.Empty);

    // ===== Results =====

    private async Task LoadResultsAsync(bool append)
    {
        if (_services is null || _query is null)
        {
            return;
        }

        var work = Restart();
        ShowResultsFrame($"Results for “{_query}”");
        ResultsRing.IsActive = !append;
        ResultsRing.Visibility = append ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.IsEnabled = false;
        try
        {
            var page = await _services.YouTube.SearchAsync(_query, _nextPage, work.Token);
            if (work.IsCancellationRequested)
            {
                return;
            }

            await LoadSavedAsync();
            _nextPage = page.NextPageToken;
            _results = [.. _results, .. page.Videos.Select(v => YouTubeCardModel.From(v))];
            ShowResults(_results.Count == 0 ? $"YouTube found nothing for “{_query}”." : null);
        }
        catch (YouTubeApiException error)
        {
            AppLog.Warning($"YouTube search failed: {error.Message}");
            ShowResults(null);
            ShowProblem(error);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ShowPlaylistAsync(YouTubePlaylist playlist)
    {
        if (_services is null)
        {
            return;
        }

        var work = Restart();
        _query = null;
        _nextPage = null;
        _results = [];
        ShowResultsFrame(playlist.Title);
        ResultsRing.IsActive = true;
        ResultsRing.Visibility = Visibility.Visible;
        try
        {
            var videos = await _services.YouTube.PlaylistVideosAsync(playlist.Id, work.Token);
            if (work.IsCancellationRequested)
            {
                return;
            }

            await LoadSavedAsync();
            _results = videos.Select(v => YouTubeCardModel.From(v)).ToList();
            ShowResults(_results.Count == 0 ? "This playlist has no videos Aperture can show." : null);
        }
        catch (YouTubeApiException error)
        {
            AppLog.Warning($"YouTube playlist failed: {error.Message}");
            ShowResults(null);
            ShowProblem(error);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowResultsFrame(string heading)
    {
        HideNotice();
        ShelvesHost.Children.Clear();
        EmptyPanel.Visibility = Visibility.Collapsed;
        ResultsSection.Visibility = Visibility.Visible;
        BackToShelvesButton.Visibility = Visibility.Visible;
        HeaderText.Text = heading;
        HeaderCaption.Text = string.Empty;
        if (_results.Count == 0)
        {
            ResultsRepeater.ItemsSource = null;
        }
    }

    private void ShowResults(string? emptyMessage)
    {
        ResultsRing.IsActive = false;
        ResultsRing.Visibility = Visibility.Collapsed;
        ResultsRepeater.ItemsSource = _results;
        MoreButton.Visibility = _query is not null && _nextPage is not null ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.IsEnabled = true;
        HeaderCaption.Text = _results.Count == 0 ? string.Empty : _query is not null ? "From YouTube search · only videos that can play in other apps" : "From your YouTube account";
        if (emptyMessage is not null)
        {
            ShowNotice(emptyMessage, NoticeAction.None, null);
        }
    }

    private void OnResultPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is YouTubeCard card && args.Index < _results.Count)
        {
            var model = _results[args.Index];
            card.IsSaved = _saved.Contains(model.VideoId);
            card.Model = model;
            card.ActionRequested -= OnResultAction;
            card.ActionRequested += OnResultAction;
        }
    }

    private void OnResultAction(object? sender, (YouTubeCardAction Action, YouTubeCardModel Model) e) =>
        _ = HandleCardActionAsync(e.Action, e.Model, _results.Select(r => r.Video).OfType<YouTubeVideo>().ToList());

    private async void OnMoreClick(object sender, RoutedEventArgs e) => await LoadResultsAsync(append: true);

    private async void OnBackToShelvesClick(object sender, RoutedEventArgs e) => await ShowShelvesAsync();

    // ===== Shelves =====

    private async Task RefreshShelvesAsync()
    {
        if (_services is null)
        {
            return;
        }

        HeaderText.Text = "YouTube";
        var service = _services.YouTube;
        IReadOnlyList<YouTubeEntry> entries;
        try
        {
            entries = await service.GetEntriesAsync(CancellationToken.None);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not read YouTube records", error);
            entries = [];
        }

        _saved = entries.Where(e => e.SavedUtc is not null).Select(e => e.VideoId).ToHashSet(StringComparer.Ordinal);
        var shelves = new List<UIElement>();
        var showProgress = _services.Settings.Current.RememberHistory;
        var continuing = showProgress ? YouTubeDataRules.ContinueWatching(entries) : [];
        if (continuing.Count > 0)
        {
            shelves.Add(Shelf("Continue watching", continuing.Select(e => YouTubeCardModel.From(e, showProgress: true)).ToList(), large: true));
        }

        var saved = YouTubeDataRules.Saved(entries);
        if (saved.Count > 0)
        {
            shelves.Add(Shelf("Saved", saved.Select(e => YouTubeCardModel.From(e, showProgress: false)).ToList(), large: false));
        }

        HeaderCaption.Text = service.Account.IsSignedIn ? "Signed in to YouTube (read-only)"
            : service.HasApiKey ? "Search above, or paste a link"
            : "Paste a YouTube link in the search box above";

        ShelvesHost.Children.Clear();
        foreach (var shelf in shelves)
        {
            ShelvesHost.Children.Add(shelf);
        }

        if (!service.CanBrowse)
        {
            ShowNotice("To search YouTube and see what’s popular, add your own YouTube Data API key. Links you paste play without one.", NoticeAction.OpenSettings, "Set up…");
        }
        else
        {
            HideNotice();
        }

        EmptyPanel.Visibility = shelves.Count == 0 && !service.CanBrowse ? Visibility.Visible : Visibility.Collapsed;
        EmptyDetail.Text = "Copy a link from YouTube (a video’s Share button, or the address bar), paste it into the search box above and press Enter. Videos you watch or save here appear on this page.";

        if (service.CanBrowse)
        {
            await AddOnlineShelvesAsync(entries);
        }
    }

    /// <summary>Liked videos and playlists (signed in) and the popular chart (key or sign-in), once per session.</summary>
    private async Task AddOnlineShelvesAsync(IReadOnlyList<YouTubeEntry> entries)
    {
        if (_services is null)
        {
            return;
        }

        var service = _services.YouTube;
        var work = Restart();
        try
        {
            if (service.Account.IsSignedIn)
            {
                _liked ??= await service.LikedAsync(work.Token);
                _playlists ??= await service.PlaylistsAsync(work.Token);
            }

            _popular ??= await service.PopularAsync(work.Token);
        }
        catch (YouTubeApiException error)
        {
            AppLog.Warning($"YouTube lists couldn't be loaded: {error.Message}");
            ShowProblem(error);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (work.IsCancellationRequested || IsShowingResults)
        {
            return;
        }

        var byId = entries.ToDictionary(e => e.VideoId, StringComparer.Ordinal);
        if (_liked is { Count: > 0 } liked)
        {
            ShelvesHost.Children.Add(Shelf("Liked videos", liked.Select(v => YouTubeCardModel.From(v, byId.GetValueOrDefault(v.Id))).ToList(), large: false));
        }

        if (_playlists is { Count: > 0 } playlists)
        {
            ShelvesHost.Children.Add(PlaylistShelf(playlists));
        }

        if (_popular is { Count: > 0 } popular)
        {
            var region = service.Region is { Length: 2 } code ? RegionName(code) : null;
            ShelvesHost.Children.Add(Shelf(region is null ? "Popular on YouTube" : $"Popular in {region}", popular.Select(v => YouTubeCardModel.From(v, byId.GetValueOrDefault(v.Id))).ToList(), large: false));
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
    }

    private StackPanel Shelf(string title, IReadOnlyList<YouTubeCardModel> models, bool large)
    {
        var section = new StackPanel { Margin = new Thickness(0, 32, 0, 0) };
        section.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(40, 0, 40, 16),
            Style = (Style)Application.Current.Resources["SubtitleTextStyle"],
        });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel((TextBlock)section.Children[0], Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(40, 0, 40, 0) };
        var queue = models.Select(m => m.Video ?? (m.Entry is { } e ? new YouTubeVideo(e.VideoId, e.Title, e.Video?.Channel ?? string.Empty, e.ThumbnailUrl, e.Video?.Duration) : null)).OfType<YouTubeVideo>().ToList();
        var width = (double)Application.Current.Resources[large ? "ArtworkLargeWidth" : "ArtworkSmallWidth"];
        foreach (var model in models)
        {
            var card = new YouTubeCard { Width = width, IsSaved = _saved.Contains(model.VideoId), Model = model };
            card.ActionRequested += (_, e) => _ = HandleCardActionAsync(e.Action, e.Model, queue);
            row.Children.Add(card);
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            XYFocusKeyboardNavigation = Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Enabled,
            Content = row,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(scroller, title);
        section.Children.Add(scroller);
        return section;
    }

    private StackPanel PlaylistShelf(IReadOnlyList<YouTubePlaylist> playlists)
    {
        var section = new StackPanel { Margin = new Thickness(0, 32, 0, 0) };
        section.Children.Add(new TextBlock
        {
            Text = "Your playlists",
            Margin = new Thickness(40, 0, 40, 16),
            Style = (Style)Application.Current.Resources["SubtitleTextStyle"],
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(40, 0, 40, 0) };
        var width = (double)Application.Current.Resources["ArtworkSmallWidth"];
        foreach (var playlist in playlists)
        {
            var art = new Grid
            {
                Width = width,
                Height = Math.Round(width * 9 / 16),
                CornerRadius = (CornerRadius)Application.Current.Resources["RadiusMedium"],
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ArtworkEmptyBrush"],
            };
            if (Uri.TryCreate(playlist.ThumbnailUrl, UriKind.Absolute, out var uri))
            {
                art.Children.Add(new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill, Source = new BitmapImage(uri) { DecodePixelWidth = 320 } });
            }

            var content = new StackPanel();
            content.Children.Add(art);
            content.Children.Add(new TextBlock { Text = playlist.Title, Margin = new Thickness(0, 10, 0, 0), Style = (Style)Application.Current.Resources["CardTitleTextStyle"] });
            content.Children.Add(new TextBlock { Text = playlist.ItemCount == 1 ? "1 video" : $"{playlist.ItemCount} videos", Margin = new Thickness(0, 2, 0, 0), Style = (Style)Application.Current.Resources["CardMetaTextStyle"] });
            var button = new Button { Content = content, Width = width, Style = (Style)Application.Current.Resources["CardButtonStyle"] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{playlist.Title}, playlist");
            button.Click += async (_, _) => await ShowPlaylistAsync(playlist);
            row.Children.Add(button);
        }

        section.Children.Add(new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = row,
        });
        return section;
    }

    // ===== Card actions =====

    private async Task HandleCardActionAsync(YouTubeCardAction action, YouTubeCardModel model, IReadOnlyList<YouTubeVideo> queue)
    {
        if (_services is null)
        {
            return;
        }

        var service = _services.YouTube;
        try
        {
            switch (action)
            {
                case YouTubeCardAction.Play:
                case YouTubeCardAction.PlayFromStart:
                    PlayRequested?.Invoke(this, new YouTubePlayRequest(model.VideoId, model.Video, TimeSpan.Zero, queue, action == YouTubeCardAction.PlayFromStart));
                    return;
                case YouTubeCardAction.Save:
                    await service.SaveAsync(model.Video ?? new YouTubeVideo(model.VideoId, model.Title, model.Meta, model.ThumbnailUrl, model.Length), CancellationToken.None);
                    break;
                case YouTubeCardAction.Unsave:
                    await service.UnsaveAsync(model.VideoId, CancellationToken.None);
                    break;
                case YouTubeCardAction.RemoveFromContinue:
                    await service.ForgetProgressAsync(model.VideoId, CancellationToken.None);
                    break;
                case YouTubeCardAction.OpenOnYouTube:
                    _ = Windows.System.Launcher.LaunchUriAsync(YouTubeLink.WatchUri(model.VideoId));
                    return;
                case YouTubeCardAction.CopyLink:
                    var package = new DataPackage();
                    package.SetText(YouTubeLink.WatchUri(model.VideoId).ToString());
                    Clipboard.SetContent(package);
                    return;
            }
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not update YouTube records", error);
            return;
        }

        if (IsShowingResults)
        {
            await LoadSavedAsync();
            ResultsRepeater.ItemsSource = null;
            ResultsRepeater.ItemsSource = _results;
        }
        else
        {
            await RefreshShelvesAsync();
        }
    }

    private async Task LoadSavedAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            var entries = await _services.YouTube.GetEntriesAsync(CancellationToken.None);
            _saved = entries.Where(e => e.SavedUtc is not null).Select(e => e.VideoId).ToHashSet(StringComparer.Ordinal);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not read YouTube records", error);
        }
    }

    // ===== Notices =====

    private void ShowProblem(YouTubeApiException error)
    {
        var action = error.Problem switch
        {
            YouTubeProblem.KeyRejected or YouTubeProblem.ApiNotEnabled or YouTubeProblem.SignInExpired => NoticeAction.OpenSettings,
            YouTubeProblem.Offline => NoticeAction.Retry,
            _ => NoticeAction.None,
        };
        ShowNotice(error.Explain(), action, action == NoticeAction.OpenSettings ? "Open Settings" : action == NoticeAction.Retry ? "Try again" : null);
    }

    private void ShowNotice(string text, NoticeAction action, string? button)
    {
        _noticeAction = action;
        NoticeText.Text = text;
        NoticeButton.Content = button ?? string.Empty;
        NoticeButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeRow.Visibility = Visibility.Visible;
    }

    private void HideNotice() => NoticeRow.Visibility = Visibility.Collapsed;

    private async void OnNoticeButtonClick(object sender, RoutedEventArgs e)
    {
        switch (_noticeAction)
        {
            case NoticeAction.OpenSettings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case NoticeAction.Retry when _query is not null:
                await LoadResultsAsync(append: false);
                break;
            case NoticeAction.Retry:
                ForgetCachedLists();
                await RefreshShelvesAsync();
                break;
        }
    }

    // ===== Helpers =====

    /// <summary>Applies the 30-day rule to YouTube's data once per session, in the background.</summary>
    private async Task MaintainAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            var (refreshed, removed) = await _services.YouTube.MaintainDetailsAsync(CancellationToken.None);
            if (refreshed + removed > 0)
            {
                AppLog.Info($"YouTube details: {refreshed} refreshed, {removed} removed under the 30-day rule.");
                if (!IsShowingResults && MainScroller.Visibility == Visibility.Visible)
                {
                    await RefreshShelvesAsync();
                }
            }
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not refresh YouTube details", error);
        }
    }

    private CancellationTokenSource Restart()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = new CancellationTokenSource();
        return _work;
    }

    private static string? RegionName(string code)
    {
        try
        {
            return new Windows.Globalization.GeographicRegion(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static YouTubeCard? FindFirstCard(DependencyObject root)
    {
        if (root is YouTubeCard card)
        {
            return card;
        }

        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindFirstCard(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
