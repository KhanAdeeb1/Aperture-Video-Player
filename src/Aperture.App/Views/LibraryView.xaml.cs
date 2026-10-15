using Aperture.App.Services;
using Aperture.Core.Library;
using Aperture.Media;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>
/// The library: every item from the user's library folders, by kind, filtered, ordered and
/// searched in memory, with scan progress and cancel. Never changes files.
/// </summary>
public sealed partial class LibraryView : UserControl
{
    private readonly DispatcherQueueTimer _refreshTimer;
    private AppServices? _services;
    private ThumbnailCache? _thumbnails;
    private CardActionHandler? _actions;
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty;
    private string _query = string.Empty;
    private bool _loaded;
    private bool _scanEndedSinceLoad;
    private bool _showing;

    public LibraryView()
    {
        InitializeComponent();

        Kinds.Items.Add(new SelectorBarItem { Text = "All", Tag = null, IsSelected = true });
        foreach (var category in MediaCategories.All)
        {
            Kinds.Items.Add(new SelectorBarItem { Text = MediaCategories.PluralLabel(category), Tag = category });
        }

        foreach (var (label, filter) in new (string, WatchFilter)[] { ("All", WatchFilter.All), ("Unwatched", WatchFilter.Unwatched), ("In progress", WatchFilter.InProgress), ("Watched", WatchFilter.Watched) })
        {
            FilterBox.Items.Add(new ComboBoxItem { Content = label, Tag = filter });
        }

        foreach (var (label, sort) in new (string, LibrarySort)[] { ("Recently added", LibrarySort.RecentlyAdded), ("Title", LibrarySort.Title), ("Year", LibrarySort.Year) })
        {
            SortBox.Items.Add(new ComboBoxItem { Content = label, Tag = sort });
        }

        FilterBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;

        // Scans save in batches; redraw at most once a second while they run.
        _refreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>The user chose something to play.</summary>
    internal event EventHandler<PlayRequest>? PlayRequested;

    /// <summary>The user asked to add a folder.</summary>
    public event EventHandler? AddFolderRequested;

    /// <summary>The user asked to see the library folders (in Settings).</summary>
    public event EventHandler? FoldersRequested;

    private MediaCategory? Category => Kinds.SelectedItem?.Tag as MediaCategory?;

    private WatchFilter Filter => FilterBox.SelectedItem is ComboBoxItem { Tag: WatchFilter filter } ? filter : WatchFilter.All;

    private LibrarySort Sort => SortBox.SelectedItem is ComboBoxItem { Tag: LibrarySort sort } ? sort : LibrarySort.RecentlyAdded;

    internal void Initialize(AppServices services, ThumbnailCache thumbnails)
    {
        _services = services;
        _thumbnails = thumbnails;
        _actions = new CardActionHandler(services, thumbnails);
        var dispatcher = DispatcherQueue;
        services.Library.ProgressChanged += (_, progress) => dispatcher.TryEnqueue(() =>
        {
            // A scan just ended; until the library is read again, the folders' scan state shown
            // here predates it.
            _scanEndedSinceLoad |= progress is null;
            ShowProgress(progress);
        });
        services.Library.Changed += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_showing)
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            }
            else
            {
                _loaded = false;
            }
        });
        ShowProgress(services.Library.Progress);
    }

    /// <summary>Shows the library, optionally switching to one kind.</summary>
    public async Task ShowAsync(MediaCategory? category)
    {
        _showing = true;
        if (category is not null || Kinds.SelectedItem is null)
        {
            var target = Kinds.Items.FirstOrDefault(i => (i.Tag as MediaCategory?) == category);
            if (target is not null)
            {
                Kinds.SelectedItem = target;
            }
        }

        await RefreshAsync();
    }

    /// <summary>Called when another screen replaces the library.</summary>
    public void Hide() => _showing = false;

    /// <summary>Filters by the title bar search box.</summary>
    public void Search(string query)
    {
        _query = query.Trim();
        Render();
    }

    /// <summary>Puts keyboard focus on the first card, or the add button when there are no folders.</summary>
    public void FocusFirstItem()
    {
        if (NoFoldersPanel.Visibility == Visibility.Visible)
        {
            AddFirstFolderButton.Focus(FocusState.Programmatic);
        }
        else if (ItemsGrid.TryGetElement(0) is MediaCard first)
        {
            first.FocusCard();
        }
        else
        {
            Kinds.Focus(FocusState.Programmatic);
        }
    }

    private async Task RefreshAsync()
    {
        if (_services is null)
        {
            return;
        }

        _scanEndedSinceLoad = false;
        try
        {
            _snapshot = await _services.Library.LoadAsync();
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not load the library", error);
        }

        _loaded = true;
        Render();
        ShowProgress(_services.Library.Progress);
    }

    private void Render()
    {
        if (!_loaded)
        {
            VisualStateManager.GoToState(this, "LoadingState", false);
            return;
        }

        if (_snapshot.Folders.Count == 0)
        {
            ItemsGrid.ItemsSource = null;
            VisualStateManager.GoToState(this, "NoFoldersState", false);
            return;
        }

        foreach (var item in Kinds.Items)
        {
            var count = item.Tag is MediaCategory category ? _snapshot.Count(category) : _snapshot.Groups.Count;
            var label = item.Tag is MediaCategory c ? MediaCategories.PluralLabel(c) : "All";
            item.Text = count > 0 ? $"{label} {count:N0}" : label;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, $"{label}, {count:N0} items");
        }

        var entries = _snapshot.Query(Category, Filter, Sort, _query);
        ItemsGrid.ItemsSource = entries.Select(MediaCardModel.FromLibrary).ToList();
        if (entries.Count > 0)
        {
            VisualStateManager.GoToState(this, "ItemsState", false);
            return;
        }

        var kind = Category is { } selected ? MediaCategories.PluralLabel(selected).ToLowerInvariant() : "videos";
        if (_query.Length > 0)
        {
            EmptyTitle.Text = $"Nothing matches “{_query}”";
            EmptyText.Text = "Search looks at titles, episode names, years and file names.";
        }
        else if (_services?.Library.IsBusy == true && _snapshot.IsEmpty)
        {
            EmptyTitle.Text = "Finding your videos…";
            EmptyText.Text = "Items appear here as the scan finds them. You can keep using Aperture meanwhile.";
        }
        else if (Filter != WatchFilter.All)
        {
            EmptyTitle.Text = $"No {Filter switch { WatchFilter.Unwatched => "unwatched", WatchFilter.InProgress => "part-watched", _ => "watched" }} {kind}";
            EmptyText.Text = "Change “Show” to see everything.";
        }
        else
        {
            EmptyTitle.Text = $"No {kind} yet";
            EmptyText.Text = "Aperture sorts files by their names and folders. If something landed in the wrong place, right-click it and choose Edit details.";
        }

        VisualStateManager.GoToState(this, "EmptyState", false);
    }

    private void ShowProgress(ScanProgress? progress)
    {
        var busy = progress is not null;
        ScanRing.IsActive = busy;
        ScanRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelScanButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !busy;
        ScanText.Text = progress?.Describe() ?? LastScanText();
    }

    private string LastScanText()
    {
        if (_scanEndedSinceLoad)
        {
            return string.Empty;
        }

        var last = _snapshot.Folders.Select(f => f.LastScanCompletedUtc).Where(t => t is not null).Max();
        // While a scan is queued or running it is the one finishing the job, so the warning would
        // only contradict the progress about to appear. A cancelled scan refreshes the view again.
        if (_services?.Library.IsBusy != true && _snapshot.Folders.Any(f => f.ScanIncomplete))
        {
            return "Last scan didn’t finish; scan again to pick up the rest";
        }

        return last is { } when ? $"Last scan: {RelativeDate.Describe(when, DateTimeOffset.Now)}" : string.Empty;
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

    // Render shows the loading state until the first load, so changes made while building are harmless.
    private void OnKindChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => Render();

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void OnScanClick(object sender, RoutedEventArgs e) => _services?.Library.RequestScan();

    private void OnCancelScanClick(object sender, RoutedEventArgs e) => _services?.Library.Cancel();

    private void OnFoldersClick(object sender, RoutedEventArgs e) => FoldersRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddFolderClick(object sender, RoutedEventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);
}
