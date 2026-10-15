using Aperture.App.Interop;
using Aperture.App.Services;
using Aperture.App.Views;
using Aperture.Core.Input;
using Aperture.Core.Library;
using Aperture.Core.Media;
using Aperture.Core.Settings;
using Aperture.Core.Windowing;
using Aperture.Playback.Mpv;
using Aperture.Playback.Mpv.Interop;
using Aperture.Video;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Aperture.App;

/// <summary>
/// The one Aperture window: owns navigation between Home, Library, Settings and the
/// player, the window modes (windowed, full screen, mini-player), window-wide keyboard
/// shortcuts, adding library folders, drag and drop, and the title bar.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A Window is not disposed by its owner; the engine and video surface are disposed in OnAppWindowClosing, the end of the window's life.")]
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 800;
    private const int MinimumWidth = 640;
    private const int MinimumHeight = 400;
    private const double TitleBarHeight = 48;
    private const double CompactTitleBarHeight = 32;

    /// <summary>Wait after launch before re-scanning library folders, so startup stays quick.</summary>
    private static readonly TimeSpan LaunchScanDelay = TimeSpan.FromSeconds(3);

    private readonly AppServices? _services;
    private readonly MpvPlaybackEngine? _engine;
    private readonly VideoPresenter? _presenter;
    private string? _engineProblem;
    private readonly ThumbnailCache _thumbnails = new();
    private ShortcutMap _shortcuts;
    private Screen _screen = Screen.Home;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _responsiveness;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _homeRefresh;
    private Screen _screenBeforeSettings = Screen.Home;
    private Screen _screenBeforePlayer = Screen.Home;
    private bool _closingSaved;
    private Windows.Graphics.RectInt32? _windowedBounds;

    internal MainWindow(StartupResult startup)
    {
        InitializeComponent();
        _services = startup.Services;
        _shortcuts = _services?.InitialShortcuts ?? ShortcutMap.Default;

        ConfigureWindow();

        (_engine, _presenter) = StartPlayback();
        WatchResponsiveness();
        PlayerPage.FullscreenToggleRequested += (_, _) => ToggleFullscreen();
        PlayerPage.MiniPlayerToggleRequested += (_, _) => ToggleMiniPlayer();
        PlayerPage.OpenRequested += async (_, _) => await PickAndOpenAsync();
        PlayerPage.DismissRequested += async (_, _) => await LeavePlayerAsync();
        PlayerPage.StopRequested += async (_, _) => await LeavePlayerAsync();
        PlayerPage.SubtitlePickRequested += async (_, _) => await PickSubtitleAsync();
        PlayerPage.OverlayVisibilityChanged += (_, visible) => SetChromeVisible(visible);
        PlayerPage.SubtitleStyleChanged += async (_, style) => await SaveSettingsAsync(s => s with { Subtitles = style });

        HomePage.PlayRequested += async (_, request) => await OpenFileAsync(request.Path, request.FromStart);
        HomePage.OpenRequested += async (_, _) => await PickAndOpenAsync();
        HomePage.AddFolderRequested += async (_, _) => await AddLibraryFolderAsync();
        HomePage.LibraryRequested += async (_, category) => await ShowLibraryAsync(category);
        LibraryPage.PlayRequested += async (_, request) => await OpenFileAsync(request.Path, request.FromStart);
        LibraryPage.AddFolderRequested += async (_, _) => await AddLibraryFolderAsync();
        LibraryPage.FoldersRequested += (_, _) => ShowSettings();

        if (_services is { } services)
        {
            PlayerPage.Initialize(_engine, _presenter, _engineProblem, services, () => _shortcuts);
            HomePage.Initialize(services, _thumbnails);
            LibraryPage.Initialize(services, _thumbnails);
            SettingsPage.Initialize(services, () => _shortcuts, _engine?.SupportsSubtitleBox ?? true);
            SettingsPage.SettingsChanged += (_, settings) => PlayerPage.ApplySettings(settings);
            SettingsPage.ShortcutsChanged += (_, map) =>
            {
                _shortcuts = map;
                PlayerPage.ApplyShortcutLabels();
            };
            SettingsPage.HistoryCleared += (_, _) => _thumbnails.Clear();
            SettingsPage.AddFolderRequested += async (_, _) => await AddLibraryFolderAsync();
            services.Library.Changed += (_, _) => DispatcherQueue.TryEnqueue(ScheduleHomeRefresh);
            services.Library.Notice += (_, notice) => DispatcherQueue.TryEnqueue(() => SettingsPage.ShowFolderMessage(notice));
            _ = HomePage.RefreshAsync();
            StartLaunchScan(services);
        }
        else
        {
            HomePage.Visibility = Visibility.Collapsed;
            SearchBox.Visibility = Visibility.Collapsed;
            SettingsButton.Visibility = Visibility.Collapsed;
            OpenButton.Visibility = Visibility.Collapsed;
            LibraryButton.Visibility = Visibility.Collapsed;
            StartupErrorPanel.Visibility = Visibility.Visible;
            StartupErrorDetail.Text = startup.Failure is null
                ? string.Empty
                : $"{startup.Failure.Message}{(startup.Failure.DataFolder is null ? string.Empty : $"\n{startup.Failure.DataFolder}")}";
        }

        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
    }

    private enum Screen
    {
        Home,
        Library,
        Settings,
        Player,
    }

    private bool IsFullscreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    private bool IsMini => AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;

    /// <summary>Opens a file in the player: from the picker, drag and drop, Home, the library, or the command line.</summary>
    public async Task OpenFileAsync(string path, bool fromStart = false)
    {
        if (_services is null)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (_screen != Screen.Player)
        {
            _screenBeforePlayer = _screen == Screen.Library ? Screen.Library : Screen.Home;
        }

        ShowScreen(Screen.Player);
        await EnsureVideoSurfaceAsync();
        AppTitleBar.Title = MediaFileTypes.TitleFromPath(fullPath);
        // Keyboard focus goes to the player so playback shortcuts work straight away.
        PlayerPage.Focus(FocusState.Programmatic);
        await PlayerPage.OpenAsync(fullPath, fromStart);
        AppTitleBar.Title = PlayerPage.Title;
    }

    // ===== Playback engine =====

    /// <summary>
    /// Creates the libmpv engine and its video surface. Neither failing stops the app: Home,
    /// the library, history and settings still work, and the player explains what went wrong.
    /// </summary>
    private (MpvPlaybackEngine? Engine, VideoPresenter? Presenter) StartPlayback()
    {
        MpvPlaybackEngine engine;
        try
        {
            engine = MpvPlaybackEngine.Create(new MpvEngineOptions
            {
                VideoOutput = "libmpv",
                HardwareDecoding = "auto-safe",
                Log = AppLog.FromEngine,
            });
            AppLog.Info($"libmpv {MpvLibrary.ApiVersion} loaded from {MpvLibrary.LoadedFrom}: {engine.ReadProperty("mpv-version")}");
        }
        catch (MpvUnavailableException error)
        {
            AppLog.Error("The playback engine could not start", error);
            _engineProblem = error.Message;
            return (null, null);
        }

        var kind = string.Equals(Environment.GetEnvironmentVariable("APERTURE_VIDEO_RENDERER"), "software", StringComparison.OrdinalIgnoreCase)
            ? VideoRendererKind.Software
            : VideoRendererKind.Automatic;
        var presenter = VideoPresenter.Start(engine, kind, flipY: false, AppLog.FromEngine);
        return (engine, presenter);
    }

    /// <summary>Waits for the video surface before the first file is opened (libmpv needs it to exist).</summary>
    private async Task EnsureVideoSurfaceAsync()
    {
        if (_presenter is null || _engineProblem is not null)
        {
            return;
        }

        try
        {
            await _presenter.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or SharpGen.Runtime.SharpGenException)
        {
            AppLog.Error("The video surface could not start", error);
            _engineProblem = $"The video surface could not start: {error.Message}";
            PlayerPage.Initialize(null, null, _engineProblem, _services!, () => _shortcuts);
        }
    }

    // ===== Window setup =====

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetCaptionButtonsVisible(true);

        var scale = Scale();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinimumHeight * scale);
        }

        // Open at a comfortable size, never larger than the work area, centred on it.
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(DefaultWidth * scale), (int)(workArea.Width * 0.9));
        var height = Math.Min((int)(DefaultHeight * scale), (int)(workArea.Height * 0.9));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));
    }

    private double Scale() => NativeMethods.GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;

    private void SetCaptionButtonsVisible(bool visible)
    {
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
        bar.ButtonForegroundColor = visible ? Windows.UI.Color.FromArgb(0xF2, 0xF3, 0xF5, 0xF7) : Colors.Transparent;
        bar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        bar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(0xB8, 0xF3, 0xF5, 0xF7);
        bar.ButtonInactiveForegroundColor = visible ? Windows.UI.Color.FromArgb(0x8C, 0xF3, 0xF5, 0xF7) : Colors.Transparent;
    }

    /// <summary>During playback the title bar and caption buttons fade with the controls.</summary>
    private void SetChromeVisible(bool visible)
    {
        if (_screen != Screen.Player)
        {
            visible = true;
        }

        AppTitleBar.Opacity = visible ? 1 : 0;
        SetCaptionButtonsVisible(visible);
    }

    // ===== Navigation =====

    private void ShowScreen(Screen screen)
    {
        if (_screen == Screen.Library && screen != Screen.Library)
        {
            LibraryPage.Hide();
        }

        _screen = screen;
        HomePage.Visibility = screen == Screen.Home && _services is not null ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = screen == Screen.Library ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = screen == Screen.Settings ? Visibility.Visible : Visibility.Collapsed;
        PlayerPage.Visibility = screen == Screen.Player ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = screen == Screen.Player ? Visibility.Collapsed : Visibility.Visible;

        var browsing = screen is Screen.Home or Screen.Library;
        AppTitleBar.IsBackButtonVisible = screen != Screen.Home;
        AppTitleBar.Title = screen == Screen.Player ? PlayerPage.Title : string.Empty;
        Wordmark.Visibility = screen == Screen.Player ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = browsing && _services is not null ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.PlaceholderText = screen == Screen.Library ? "Search the library" : "Search";
        GlobalActions.Visibility = screen == Screen.Player ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = screen == Screen.Settings || _services is null ? Visibility.Collapsed : Visibility.Visible;
        LibraryButton.Visibility = screen == Screen.Library || _services is null ? Visibility.Collapsed : Visibility.Visible;
        SetChromeVisible(true);
    }

    private async Task ShowHomeAsync()
    {
        await LeaveWindowModesAndPlayerAsync();
        ClearSearch();
        ShowScreen(Screen.Home);
        await HomePage.RefreshAsync();
    }

    private async Task ShowLibraryAsync(MediaCategory? category = null)
    {
        if (_services is null)
        {
            return;
        }

        await LeaveWindowModesAndPlayerAsync();
        ClearSearch();
        ShowScreen(Screen.Library);
        await LibraryPage.ShowAsync(category);
    }

    /// <summary>Leaving the player goes back to where the video was chosen (Home or the library).</summary>
    private async Task LeavePlayerAsync()
    {
        if (_screenBeforePlayer == Screen.Library)
        {
            await ShowLibraryAsync();
        }
        else
        {
            await ShowHomeAsync();
        }
    }

    private async Task LeaveWindowModesAndPlayerAsync()
    {
        if (IsMini || IsFullscreen)
        {
            SetWindowed();
        }

        if (_screen == Screen.Player)
        {
            await PlayerPage.CloseAsync();
        }
    }

    private void ShowSettings()
    {
        if (_services is null || _screen == Screen.Settings)
        {
            return;
        }

        _screenBeforeSettings = _screen == Screen.Player ? _screenBeforePlayer : _screen;
        if (_screen == Screen.Player)
        {
            if (IsMini || IsFullscreen)
            {
                SetWindowed();
            }

            _ = PlayerPage.CloseAsync();
        }

        SettingsPage.Refresh();
        ShowScreen(Screen.Settings);
    }

    private async Task GoBackAsync()
    {
        switch (_screen)
        {
            case Screen.Player:
                await LeavePlayerAsync();
                break;
            case Screen.Settings when _screenBeforeSettings == Screen.Library:
                await ShowLibraryAsync();
                break;
            case Screen.Settings:
            case Screen.Library:
                await ShowHomeAsync();
                break;
        }
    }

    private void ClearSearch()
    {
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty;
        }
    }

    private void ScheduleHomeRefresh()
    {
        if (_screen != Screen.Home)
        {
            return;
        }

        if (_homeRefresh is null)
        {
            _homeRefresh = DispatcherQueue.CreateTimer();
            _homeRefresh.Interval = TimeSpan.FromSeconds(1);
            _homeRefresh.IsRepeating = false;
            _homeRefresh.Tick += async (_, _) =>
            {
                if (_screen == Screen.Home)
                {
                    await HomePage.RefreshAsync();
                }
            };
        }

        _homeRefresh.Stop();
        _homeRefresh.Start();
    }

    private async void OnBackRequested(TitleBar sender, object args) => await GoBackAsync();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    private async void OnLibraryClick(object sender, RoutedEventArgs e) => await ShowLibraryAsync();

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_screen == Screen.Library)
        {
            LibraryPage.Search(sender.Text);
        }
        else
        {
            HomePage.Search(sender.Text);
        }
    }

    private async Task SaveSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        if (_services is null)
        {
            return;
        }

        var updated = change(_services.Settings.Current);
        _services.Settings.Current = updated;
        try
        {
            await _services.SettingsStore.SaveAsync(updated);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save settings", error);
        }

        SettingsPage.Refresh();
    }

    // ===== Library folders =====

    /// <summary>A few seconds after launch, looks for new, changed and removed files in every library folder.</summary>
    private void StartLaunchScan(AppServices services)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = LaunchScanDelay;
        timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            try
            {
                if ((await services.Library.GetFoldersAsync()).Count > 0)
                {
                    services.Library.RequestScan();
                }
            }
            catch (System.Data.Common.DbException error)
            {
                AppLog.Error("Could not start the library scan", error);
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Picks a folder, checks it is a sensible library folder (not a drive, Windows, the whole
    /// profile, or Aperture's own data), asks what it holds, then adds it and starts scanning.
    /// </summary>
    private async Task AddLibraryFolderAsync()
    {
        if (_services is null)
        {
            return;
        }

        var picker = new FolderPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            CommitButtonText = "Add to library",
        };

        PickFolderResult? result;
        try
        {
            result = await picker.PickSingleFolderAsync();
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            AppLog.Error("The folder picker failed to open", error);
            return;
        }

        if (result is null || string.IsNullOrEmpty(result.Path))
        {
            return;
        }

        var existing = (await _services.Library.GetFoldersAsync()).Select(f => f.Path);
        var problem = LibraryFolderRules.Check(result.Path, ProtectedFolders(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), existing, _services.DataLocation.Root);
        if (problem is not null)
        {
            await ShowMessageAsync("Choose a different folder", problem);
            return;
        }

        var kind = await AskFolderKindAsync(result.Path);
        if (kind is null)
        {
            return;
        }

        try
        {
            await _services.Library.AddFolderAsync(result.Path, kind.Value);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not add a library folder", error);
            await ShowMessageAsync("The folder wasn’t added", "Aperture couldn’t save it to its records. Details are in Aperture’s log.");
            return;
        }

        SettingsPage.ShowFolderMessage($"Added “{Path.GetFileName(result.Path.TrimEnd('\\'))}”. Scanning in the background; you can keep using Aperture.");
        await SettingsPage.RefreshFoldersAsync();
        if (_screen != Screen.Settings)
        {
            await ShowLibraryAsync();
        }
    }

    private static IEnumerable<string> ProtectedFolders()
    {
        foreach (var folder in (Environment.SpecialFolder[])[
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData])
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
            {
                yield return path;
            }
        }
    }

    private async Task<LibraryFolderKind?> AskFolderKindAsync(string path)
    {
        var choices = new RadioButtons { Header = "What’s in this folder?" };
        foreach (var kind in MediaCategories.FolderKinds)
        {
            choices.Items.Add(new RadioButton { Content = MediaCategories.Label(kind), Tag = kind });
        }

        choices.SelectedIndex = 0;
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = path,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(choices);
        content.Children.Add(new TextBlock
        {
            Text = "This only guides how files are sorted; you can correct any item later.",
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add to library",
            Content = content,
            PrimaryButtonText = "Add and scan",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return choices.SelectedItem is RadioButton { Tag: LibraryFolderKind chosen } ? chosen : LibraryFolderKind.Mixed;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyTextStyle"] },
            CloseButtonText = "OK",
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        await dialog.ShowAsync();
    }

    // ===== Opening files =====

    private async Task PickAndOpenAsync()
    {
        if (_services is null)
        {
            return;
        }

        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.Thumbnail,
            CommitButtonText = "Play",
        };
        foreach (var extension in MediaFileTypes.VideoExtensions.Concat(MediaFileTypes.AudioExtensions))
        {
            picker.FileTypeFilter.Add(extension);
        }

        PickFileResult? result;
        try
        {
            result = await picker.PickSingleFileAsync();
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            AppLog.Error("The file picker failed to open", error);
            return;
        }

        if (result is not null && !string.IsNullOrEmpty(result.Path))
        {
            await OpenFileAsync(result.Path);
        }
    }

    private async Task PickSubtitleAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.List,
            CommitButtonText = "Add subtitles",
        };
        foreach (var extension in MediaFileTypes.SubtitleExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        PickFileResult? result;
        try
        {
            result = await picker.PickSingleFileAsync();
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            AppLog.Error("The subtitle picker failed to open", error);
            return;
        }

        if (result is not null && !string.IsNullOrEmpty(result.Path))
        {
            await PlayerPage.AddSubtitleFileAsync(result.Path);
        }
    }

    private async void OnDragOver(object sender, DragEventArgs e)
    {
        if (_services is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var subtitles = _screen == Screen.Player && items.OfType<StorageFile>().Any(f => MediaFileTypes.IsSubtitleFile(f.Path));
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = subtitles ? "Add subtitles" : "Play in Aperture";
            e.DragUIOverride.IsGlyphVisible = false;
            DropText.Text = subtitles ? "Drop to add subtitles" : "Drop to play";
            DropOverlay.Opacity = 0.96;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Opacity = 0;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Opacity = 0;
        if (_services is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var subtitle = items.OfType<StorageFile>().FirstOrDefault(f => MediaFileTypes.IsSubtitleFile(f.Path));
        if (_screen == Screen.Player && subtitle is not null && !string.IsNullOrEmpty(subtitle.Path))
        {
            await PlayerPage.AddSubtitleFileAsync(subtitle.Path);
            return;
        }

        var file = items.OfType<StorageFile>().FirstOrDefault(f => MediaFileTypes.IsPlayableFile(f.Path))
            ?? items.OfType<StorageFile>().FirstOrDefault();
        if (file is not null && !string.IsNullOrEmpty(file.Path))
        {
            await OpenFileAsync(file.Path);
        }
    }

    // ===== Window modes =====

    private void ToggleFullscreen()
    {
        if (IsFullscreen)
        {
            SetWindowed();
        }
        else
        {
            if (IsMini)
            {
                SetWindowed();
            }

            RememberWindowedBounds();
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
    }

    private void ToggleMiniPlayer()
    {
        if (IsMini)
        {
            SetWindowed();
            return;
        }

        if (IsFullscreen)
        {
            SetWindowed();
        }

        RememberWindowedBounds();

        // The compact overlay stays above other windows until the user returns.
        var presenter = CompactOverlayPresenter.Create();
        presenter.InitialSize = CompactOverlaySize.Medium;
        AppWindow.SetPresenter(presenter);
    }

    private void RememberWindowedBounds()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped)
        {
            _windowedBounds = new Windows.Graphics.RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        }
    }

    /// <summary>
    /// Returns to a normal window where it was before full screen or the mini-player. If that
    /// place is no longer on any display (a monitor was unplugged, the resolution changed),
    /// the window moves onto the display it overlaps most and shrinks to fit.
    /// </summary>
    private void SetWindowed()
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_windowedBounds is not { } saved)
        {
            return;
        }

        var bounds = new PixelRect(saved.X, saved.Y, saved.Width, saved.Height);
        // FindAll's list cannot be enumerated with foreach (its projection has no IIterable
        // behind it, so GetEnumerator throws); index into it instead.
        var areas = new List<PixelRect>();
        var displays = DisplayArea.FindAll();
        for (var i = 0; i < displays.Count; i++)
        {
            var area = displays[i].WorkArea;
            areas.Add(new PixelRect(area.X, area.Y, area.Width, area.Height));
        }

        if (areas.Count == 0)
        {
            AppWindow.MoveAndResize(saved);
            return;
        }

        var scale = Scale();
        var fitted = WindowPlacement.FitInto(bounds, WindowPlacement.ChooseWorkArea(bounds, areas), (int)(MinimumWidth * scale), (int)(MinimumHeight * scale));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(fitted.X, fitted.Y, fitted.Width, fitted.Height));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            var mini = IsMini;
            AppTitleBar.Height = mini ? CompactTitleBarHeight : TitleBarHeight;
            AppWindow.TitleBar.PreferredHeightOption = mini ? TitleBarHeightOption.Standard : TitleBarHeightOption.Tall;
            PlayerPage.SetWindowMode(IsFullscreen, mini);
        }
    }

    // ===== Keyboard =====

    private async void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);
        var typing = IsTyping();

        if (control && !alt && e.Key == VirtualKey.O)
        {
            e.Handled = true;
            await PickAndOpenAsync();
            return;
        }

        if (control && !alt && e.Key == (VirtualKey)KeyNames.Codes.Comma)
        {
            e.Handled = true;
            ShowSettings();
            return;
        }

        if (control && !alt && e.Key == VirtualKey.L)
        {
            e.Handled = true;
            await ShowLibraryAsync();
            LibraryPage.FocusFirstItem();
            return;
        }

        if (control && !alt && e.Key == VirtualKey.F)
        {
            e.Handled = true;
            if (_screen is not (Screen.Home or Screen.Library))
            {
                await ShowHomeAsync();
            }

            SearchBox.Focus(FocusState.Keyboard);
            return;
        }

        if ((alt && e.Key == VirtualKey.Left) || (e.Key == VirtualKey.Back && !typing && _screen != Screen.Home))
        {
            e.Handled = true;
            await GoBackAsync();
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (_screen == Screen.Player && PlayerPage.CloseTopPanel())
            {
                e.Handled = true;
            }
            else if (IsFullscreen || IsMini)
            {
                e.Handled = true;
                SetWindowed();
            }
            else if (_screen is Screen.Home or Screen.Library && SearchBox.Text.Length > 0)
            {
                e.Handled = true;
                SearchBox.Text = string.Empty;
                if (_screen == Screen.Library)
                {
                    LibraryPage.FocusFirstItem();
                }
                else
                {
                    HomePage.FocusFirstItem();
                }
            }
            else if (_screen is Screen.Settings or Screen.Library)
            {
                e.Handled = true;
                await GoBackAsync();
            }

            return;
        }

        if (_screen != Screen.Player || typing)
        {
            return;
        }

        var modifiers = (control ? KeyModifiers.Control : KeyModifiers.None)
            | (shift ? KeyModifiers.Shift : KeyModifiers.None)
            | (alt ? KeyModifiers.Alt : KeyModifiers.None);
        if (_shortcuts.Resolve(new KeyChord((int)e.Key, modifiers)) is not { } command)
        {
            return;
        }

        // Held keys repeat only where that makes sense (seeking, volume); holding F does not flicker.
        if (e.KeyStatus.WasKeyDown && !PlayerCommands.Repeats(command))
        {
            e.Handled = !PlayerPage.IsFocusInPanel;
            return;
        }

        e.Handled = PlayerPage.HandleCommand(command);
    }

    /// <summary>True when the keyboard is typing into a text field, where letters and arrows belong to the field.</summary>
    private bool IsTyping() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox;

    /// <summary>
    /// Logs when the interface thread falls behind by more than half a second, so a slow
    /// machine or a stalled call shows up in the log instead of only as a sluggish window.
    /// </summary>
    private void WatchResponsiveness()
    {
        var timer = _responsiveness = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = clock.Elapsed;
        timer.Tick += (_, _) =>
        {
            var now = clock.Elapsed;
            var late = now - last - timer.Interval;
            last = now;
            if (late > TimeSpan.FromMilliseconds(500))
            {
                AppLog.Warning($"The interface was unresponsive for {late.TotalMilliseconds:0} ms ({_screen}, video: {_presenter?.Description ?? "none"}).");
            }
        };
        timer.Start();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ===== Closing =====

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingSaved)
        {
            return;
        }

        // Save the playback position before the window goes away, then close for real.
        args.Cancel = true;
        _closingSaved = true;
        await PlayerPage.CloseAsync();

        // A running scan stops here; everything it found is already saved and the next scan resumes.
        _services?.Library.Dispose();

        // Fixed order: the panel lets go of the swap chain, the video thread frees libmpv's
        // render context, and only then is libmpv itself destroyed.
        PlayerPage.Shutdown();
        _presenter?.Dispose();
        _engine?.Dispose();
        Close();
    }
}
