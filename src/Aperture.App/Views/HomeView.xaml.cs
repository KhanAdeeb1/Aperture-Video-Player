using Aperture.App.Services;
using Aperture.Core.Library;
using Aperture.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>
/// The home screen: Continue Watching, the newest library items of each kind, Recently
/// Played, search across the library and history, and the first-launch state.
/// </summary>
public sealed partial class HomeView : UserControl
{
    /// <summary>Cards per library row; "See all" opens the rest in the library.</summary>
    private const int RowLimit = 12;

    private AppServices? _services;
    private ThumbnailCache? _thumbnails;
    private CardActionHandler? _actions;
    private IReadOnlyList<HistoryEntry> _entries = [];
    private LibrarySnapshot _library = LibrarySnapshot.Empty;
    private string _query = string.Empty;
    private bool _loaded;

    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>The user chose something to play.</summary>
    internal event EventHandler<PlayRequest>? PlayRequested;

    /// <summary>The user asked to open a file from the empty state.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The user asked to add a library folder from the empty state.</summary>
    public event EventHandler? AddFolderRequested;

    /// <summary>The user chose "See all" on a library row.</summary>
    public event EventHandler<MediaCategory>? LibraryRequested;

    /// <summary>True when the first-launch state is showing.</summary>
    public bool IsEmpty => _loaded && _entries.Count == 0 && _library.IsEmpty;

    internal void Initialize(AppServices services, ThumbnailCache thumbnails)
    {
        _services = services;
        _thumbnails = thumbnails;
        _actions = new CardActionHandler(services, thumbnails);
    }

    /// <summary>Reloads history and the library from storage and redraws.</summary>
    public async Task RefreshAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            _entries = await _services.History.GetAllAsync();
            _library = await _services.Library.LoadAsync();
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not load Home", error);
        }

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
            return;
        }

        foreach (var repeater in (ItemsRepeater[])[ContinueRepeater, MoviesRepeater, ShowsRepeater, PersonalRepeater, PodcastsRepeater, RecentRepeater])
        {
            if (repeater.Visibility == Visibility.Visible && repeater.TryGetElement(0) is MediaCard first)
            {
                first.FocusCard();
                return;
            }
        }
    }

    private void Render()
    {
        if (!_loaded)
        {
            VisualStateManager.GoToState(this, "HistoryLoading", false);
            return;
        }

        var now = DateTimeOffset.Now;
        if (_query.Length > 0)
        {
            // Library items first (with their details and copies), then played files outside the library.
            var inLibrary = _library.Query(null, WatchFilter.All, LibrarySort.Title, _query);
            var libraryPaths = new HashSet<string>(inLibrary.SelectMany(e => e.Group.All).Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            var played = HomeShelves.Search(_entries, _query).Where(e => !libraryPaths.Contains(e.Path) && _library.GroupOf(e.Path) is null);
            var results = inLibrary.Select(MediaCardModel.FromLibrary)
                .Concat(played.Select(e => MediaCardModel.FromHistory(e, showProgress: false, now)))
                .ToList();
            ResultsRepeater.ItemsSource = results;
            ResultsHeader.Text = results.Count == 1 ? "1 result" : $"{results.Count:N0} results";
            NoResultsTitle.Text = $"Nothing matches “{_query}”";
            VisualStateManager.GoToState(this, results.Count > 0 ? "SearchResults" : "NoResults", false);
            return;
        }

        var shelves = HomeShelves.From(_entries);
        if (shelves.IsEmpty && _library.IsEmpty)
        {
            VisualStateManager.GoToState(this, "FirstLaunch", false);
            return;
        }

        ContinueRepeater.ItemsSource = shelves.ContinueWatching.Select(e => Card(e, showProgress: true, now)).ToList();
        RecentRepeater.ItemsSource = shelves.RecentlyPlayed.Select(e => Card(e, showProgress: false, now)).ToList();
        ContinueSection.Visibility = shelves.ContinueWatching.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var anyRow = false;
        anyRow |= ShowRow(MediaCategory.Movie, MoviesSection, MoviesHeader, MoviesRepeater);
        anyRow |= ShowRow(MediaCategory.Episode, ShowsSection, ShowsHeader, ShowsRepeater);
        anyRow |= ShowRow(MediaCategory.Personal, PersonalSection, PersonalHeader, PersonalRepeater);
        anyRow |= ShowRow(MediaCategory.Podcast, PodcastsSection, PodcastsHeader, PodcastsRepeater);

        RecentHeader.Visibility = shelves.RecentlyPlayed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentRepeater.Visibility = RecentHeader.Visibility;
        RecentHeader.Margin = shelves.ContinueWatching.Count > 0 || anyRow ? new Thickness(0, 40, 0, 16) : new Thickness(0, 0, 0, 16);
        FirstRowWithoutGap(shelves.ContinueWatching.Count > 0);
        VisualStateManager.GoToState(this, "Shelves", false);
    }

    /// <summary>A played file, shown with its library details when it is in the library.</summary>
    private MediaCardModel Card(HistoryEntry entry, bool showProgress, DateTimeOffset now)
    {
        if (!showProgress && _library.GroupOf(entry.Path) is { } group)
        {
            var state = WatchStatus.Of(entry, _library.WatchedOverrides.TryGetValue(entry.Path, out var marked) ? marked : null);
            return MediaCardModel.FromLibrary(new LibraryEntry(group, state, entry));
        }

        return MediaCardModel.FromHistory(entry, showProgress, now);
    }

    private bool ShowRow(MediaCategory category, StackPanel section, TextBlock header, ItemsRepeater repeater)
    {
        // Newest additions not yet finished: what is waiting to be watched.
        var entries = _library.Query(category, WatchFilter.Unwatched, LibrarySort.RecentlyAdded, null);
        if (entries.Count == 0)
        {
            section.Visibility = Visibility.Collapsed;
            return false;
        }

        header.Text = MediaCategories.PluralLabel(category);
        repeater.ItemsSource = entries.Take(RowLimit).Select(MediaCardModel.FromLibrary).ToList();
        section.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>The first visible library row sits at the top when there is nothing to continue.</summary>
    private void FirstRowWithoutGap(bool hasContinue)
    {
        var first = true;
        foreach (var (section, row) in new (StackPanel, Grid)[] { (MoviesSection, MoviesHeaderRow), (ShowsSection, ShowsHeaderRow), (PersonalSection, PersonalHeaderRow), (PodcastsSection, PodcastsHeaderRow) })
        {
            if (section.Visibility != Visibility.Visible)
            {
                continue;
            }

            var margin = row.Margin;
            row.Margin = new Thickness(margin.Left, first && !hasContinue ? 0 : 40, margin.Right, margin.Bottom);
            first = false;
        }
    }

    private void OnCardPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not MediaCard card || sender.ItemsSourceView?.GetAt(args.Index) is not MediaCardModel model)
        {
            return;
        }

        card.ActionRequested -= OnCardAction;
        card.ActionRequested += OnCardAction;
        card.Model = model;

        if (model.Artwork is null && _thumbnails is not null)
        {
            _ = LoadArtworkAsync(card, model);
        }
    }

    private async Task LoadArtworkAsync(MediaCard card, MediaCardModel model)
    {
        var image = await _thumbnails!.GetAsync(model.Path, model.ArtworkPath);
        model.Artwork = image;
        if (ReferenceEquals(card.Model, model))
        {
            card.SetArtwork(image);
        }
    }

    private async void OnCardAction(object? sender, CardAction action)
    {
        if (_actions is not null && await _actions.HandleAsync(action, XamlRoot, request => PlayRequested?.Invoke(this, request)))
        {
            await RefreshAsync();
        }
    }

    private void OnSeeAllClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<MediaCategory>(tag, out var category))
        {
            LibraryRequested?.Invoke(this, category);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddFolderClick(object sender, RoutedEventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);
}
