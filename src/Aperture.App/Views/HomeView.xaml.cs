using Aperture.App.Services;
using Aperture.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>The home screen: Continue Watching, Recently Played, search, and the first-launch state.</summary>
internal sealed partial class HomeView : UserControl
{
    private IPlaybackHistory? _history;
    private ThumbnailCache? _thumbnails;
    private IReadOnlyList<HistoryEntry> _entries = [];
    private string _query = string.Empty;
    private bool _loaded;

    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>The user chose a played file.</summary>
    public event EventHandler<string>? PlayRequested;

    /// <summary>The user asked to open a file from the empty state.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>True when the first-launch state is showing.</summary>
    public bool IsEmpty => _loaded && _entries.Count == 0;

    public void Initialize(IPlaybackHistory history, ThumbnailCache thumbnails)
    {
        _history = history;
        _thumbnails = thumbnails;
    }

    /// <summary>Reloads history from storage and redraws the shelves.</summary>
    public async Task RefreshAsync()
    {
        if (_history is null)
        {
            return;
        }

        _entries = await _history.GetAllAsync();
        _loaded = true;
        Render();
    }

    /// <summary>Filters by a search query; an empty query shows the shelves again.</summary>
    public void Search(string query)
    {
        _query = query.Trim();
        Render();
    }

    /// <summary>Puts keyboard focus on the first card, or the open button when empty.</summary>
    public void FocusFirstItem()
    {
        if (IsEmpty)
        {
            FirstLaunchOpenButton.Focus(FocusState.Programmatic);
        }
        else if ((ContinueRepeater.TryGetElement(0) ?? RecentRepeater.TryGetElement(0)) is MediaCard first)
        {
            first.FocusCard();
        }
    }

    private void Render()
    {
        if (!_loaded)
        {
            VisualStateManager.GoToState(this, "Loading", false);
            return;
        }

        if (_query.Length > 0)
        {
            var results = HomeShelves.Search(_entries, _query);
            var now = DateTimeOffset.Now;
            ResultsRepeater.ItemsSource = results.Select(e => new MediaCardModel(e, showProgress: false, now)).ToList();
            ResultsHeader.Text = results.Count == 1 ? "1 result" : $"{results.Count} results";
            NoResultsTitle.Text = $"Nothing matches “{_query}”";
            VisualStateManager.GoToState(this, results.Count > 0 ? "SearchResults" : "NoResults", false);
            return;
        }

        var shelves = HomeShelves.From(_entries);
        if (shelves.IsEmpty)
        {
            VisualStateManager.GoToState(this, "FirstLaunch", false);
            return;
        }

        var stamp = DateTimeOffset.Now;
        ContinueRepeater.ItemsSource = shelves.ContinueWatching.Select(e => new MediaCardModel(e, showProgress: true, stamp)).ToList();
        RecentRepeater.ItemsSource = shelves.RecentlyPlayed.Select(e => new MediaCardModel(e, showProgress: false, stamp)).ToList();

        ContinueSection.Visibility = shelves.ContinueWatching.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentHeader.Visibility = shelves.RecentlyPlayed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentRepeater.Visibility = RecentHeader.Visibility;
        RecentHeader.Margin = shelves.ContinueWatching.Count > 0 ? new Thickness(0, 40, 0, 16) : new Thickness(0, 0, 0, 16);
        VisualStateManager.GoToState(this, "Shelves", false);
    }

    private void OnContinueCardPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args) => PrepareCard(sender, args);

    private void OnRecentCardPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args) => PrepareCard(sender, args);

    private void PrepareCard(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not MediaCard card || sender.ItemsSourceView?.GetAt(args.Index) is not MediaCardModel model)
        {
            return;
        }

        card.Invoked -= OnCardInvoked;
        card.RemoveRequested -= OnCardRemoveRequested;
        card.Invoked += OnCardInvoked;
        card.RemoveRequested += OnCardRemoveRequested;
        card.Model = model;

        if (model.Artwork is null && _thumbnails is not null)
        {
            _ = LoadArtworkAsync(card, model);
        }
    }

    private async Task LoadArtworkAsync(MediaCard card, MediaCardModel model)
    {
        var image = await _thumbnails!.GetAsync(model.Entry.Path);
        model.Artwork = image;
        if (ReferenceEquals(card.Model, model))
        {
            card.SetArtwork(image);
        }
    }

    private void OnCardInvoked(object? sender, MediaCardModel model) => PlayRequested?.Invoke(this, model.Entry.Path);

    private async void OnCardRemoveRequested(object? sender, MediaCardModel model)
    {
        if (_history is null)
        {
            return;
        }

        await _history.RemoveAsync(model.Entry.Path);
        _thumbnails?.Forget(model.Entry.Path);
        await RefreshAsync();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);
}
