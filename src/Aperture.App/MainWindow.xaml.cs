using Aperture.App.Interop;
using Aperture.App.Services;
using Aperture.Core.Media;
using Aperture.Core.Settings;
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
/// The one Aperture window: owns navigation between Home, Settings and the player, the
/// window modes (windowed, full screen, mini-player), window-wide keyboard shortcuts,
/// drag and drop, and the title bar.
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

    private readonly AppServices? _services;
    private readonly MpvPlaybackEngine? _engine;
    private readonly VideoPresenter? _presenter;
    private string? _engineProblem;
    private readonly ThumbnailCache _thumbnails = new();
    private AppSettings _settings;
    private Screen _screen = Screen.Home;
    private Screen _screenBeforeSettings = Screen.Home;
    private bool _closingSaved;
    private Windows.Graphics.RectInt32? _windowedBounds;

    internal MainWindow(StartupResult startup)
    {
        InitializeComponent();
        _services = startup.Services;
        _settings = _services?.InitialSettings ?? AppSettings.Default;

        ConfigureWindow();

        (_engine, _presenter) = StartPlayback();
        PlayerPage.FullscreenToggleRequested += (_, _) => ToggleFullscreen();
        PlayerPage.MiniPlayerToggleRequested += (_, _) => ToggleMiniPlayer();
        PlayerPage.OpenRequested += async (_, _) => await PickAndOpenAsync();
        PlayerPage.DismissRequested += async (_, _) => await ShowHomeAsync();
        PlayerPage.StopRequested += async (_, _) => await ShowHomeAsync();
        PlayerPage.SubtitlePickRequested += async (_, _) => await PickSubtitleAsync();
        PlayerPage.OverlayVisibilityChanged += (_, visible) => SetChromeVisible(visible);

        HomePage.PlayRequested += async (_, path) => await OpenFileAsync(path);
        HomePage.OpenRequested += async (_, _) => await PickAndOpenAsync();

        if (_services is not null)
        {
            PlayerPage.Initialize(_engine, _presenter, _engineProblem, _services.History, () => _settings);
            HomePage.Initialize(_services.History, _thumbnails);
            SettingsPage.Initialize(_services.SettingsStore, _services.History, _settings, _services.DataLocation.Root);
            SettingsPage.SettingsChanged += (_, settings) => _settings = settings;
            SettingsPage.HistoryCleared += (_, _) => _thumbnails.Clear();
            _ = HomePage.RefreshAsync();
        }
        else
        {
            HomePage.Visibility = Visibility.Collapsed;
            SearchBox.Visibility = Visibility.Collapsed;
            SettingsButton.Visibility = Visibility.Collapsed;
            OpenButton.Visibility = Visibility.Collapsed;
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
        Settings,
        Player,
    }

    private bool IsFullscreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    private bool IsMini => AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;

    /// <summary>Opens a file in the player: from the picker, drag and drop, Home, or the command line.</summary>
    public async Task OpenFileAsync(string path)
    {
        if (_services is null)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        ShowScreen(Screen.Player);
        await EnsureVideoSurfaceAsync();
        AppTitleBar.Title = MediaFileTypes.TitleFromPath(fullPath);
        // Keyboard focus goes to the player so playback shortcuts work straight away.
        PlayerPage.Focus(FocusState.Programmatic);
        await PlayerPage.OpenAsync(fullPath);
        AppTitleBar.Title = PlayerPage.Title;
    }

    // ===== Playback engine =====

    /// <summary>
    /// Creates the libmpv engine and its video surface. Neither failing stops the app: Home,
    /// history and settings still work, and the player explains what went wrong.
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
        var presenter = VideoPresenter.Start(engine, kind, flipY: true, AppLog.FromEngine);
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
            PlayerPage.Initialize(null, null, _engineProblem, _services!.History, () => _settings);
        }
    }

    // ===== Window setup =====

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetCaptionButtonsVisible(true);

        var scale = NativeMethods.GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
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
        _screen = screen;
        HomePage.Visibility = screen == Screen.Home && _services is not null ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = screen == Screen.Settings ? Visibility.Visible : Visibility.Collapsed;
        PlayerPage.Visibility = screen == Screen.Player ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = screen == Screen.Player ? Visibility.Collapsed : Visibility.Visible;

        var onHome = screen == Screen.Home;
        AppTitleBar.IsBackButtonVisible = !onHome;
        AppTitleBar.Title = screen == Screen.Player ? PlayerPage.Title : string.Empty;
        Wordmark.Visibility = screen == Screen.Player ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = onHome && _services is not null ? Visibility.Visible : Visibility.Collapsed;
        GlobalActions.Visibility = screen == Screen.Player ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = screen == Screen.Settings || _services is null ? Visibility.Collapsed : Visibility.Visible;
        SetChromeVisible(true);
    }

    private async Task ShowHomeAsync()
    {
        if (IsMini || IsFullscreen)
        {
            SetWindowed();
        }

        if (_screen == Screen.Player)
        {
            await PlayerPage.CloseAsync();
        }

        ShowScreen(Screen.Home);
        await HomePage.RefreshAsync();
    }

    private void ShowSettings()
    {
        if (_services is null || _screen == Screen.Settings)
        {
            return;
        }

        _screenBeforeSettings = _screen == Screen.Player ? Screen.Home : _screen;
        if (_screen == Screen.Player)
        {
            _ = PlayerPage.CloseAsync();
        }

        ShowScreen(Screen.Settings);
    }

    private async Task GoBackAsync()
    {
        switch (_screen)
        {
            case Screen.Player:
                await ShowHomeAsync();
                break;
            case Screen.Settings when _screenBeforeSettings == Screen.Home:
                await ShowHomeAsync();
                break;
        }
    }

    private async void OnBackRequested(TitleBar sender, object args) => await GoBackAsync();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        HomePage.Search(sender.Text);

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
        foreach (var extension in MediaFileTypes.VideoExtensions)
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

        var file = items.OfType<StorageFile>().FirstOrDefault(f => MediaFileTypes.IsVideoFile(f.Path))
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

        _windowedBounds = new Windows.Graphics.RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        var presenter = CompactOverlayPresenter.Create();
        presenter.InitialSize = CompactOverlaySize.Medium;
        AppWindow.SetPresenter(presenter);
    }

    private void SetWindowed()
    {
        var wasMini = IsMini;
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (wasMini && _windowedBounds is { } bounds)
        {
            AppWindow.MoveAndResize(bounds);
        }
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
        var typing = FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox;

        if (control && e.Key == VirtualKey.O)
        {
            e.Handled = true;
            await PickAndOpenAsync();
            return;
        }

        if (control && e.Key == (VirtualKey)188)
        {
            // Ctrl+, (comma has no named VirtualKey).
            e.Handled = true;
            ShowSettings();
            return;
        }

        if (control && e.Key == VirtualKey.F)
        {
            e.Handled = true;
            if (_screen != Screen.Home)
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
            if (IsFullscreen || IsMini)
            {
                e.Handled = true;
                SetWindowed();
            }
            else if (_screen == Screen.Home && SearchBox.Text.Length > 0)
            {
                e.Handled = true;
                SearchBox.Text = string.Empty;
                HomePage.FocusFirstItem();
            }
            else if (_screen == Screen.Settings)
            {
                e.Handled = true;
                await GoBackAsync();
            }

            return;
        }

        if (_screen == Screen.Player && !typing && !alt)
        {
            e.Handled = PlayerPage.HandleKey(e.Key, control, shift);
        }
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

        // Fixed order: the panel lets go of the swap chain, the video thread frees libmpv's
        // render context, and only then is libmpv itself destroyed.
        PlayerPage.Shutdown();
        _presenter?.Dispose();
        _engine?.Dispose();
        Close();
    }
}
