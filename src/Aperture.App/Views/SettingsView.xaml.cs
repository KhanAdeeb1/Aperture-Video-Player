using System.Reflection;
using Aperture.App.Services;
using Aperture.Core.Input;
using Aperture.Core.Library;
using Aperture.Core.Playback;
using Aperture.Core.Series;
using Aperture.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>The settings screen. Changes are saved as they are made.</summary>
public sealed partial class SettingsView : UserControl
{
    private AppServices? _services;
    private Func<ShortcutMap> _shortcuts = () => ShortcutMap.Default;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        foreach (var (label, mode) in new (string, ResumeMode)[] { ("Resume where I left off", ResumeMode.Resume), ("Ask each time", ResumeMode.Ask), ("Start from the beginning", ResumeMode.StartOver) })
        {
            ResumeBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        }

        foreach (var seconds in AppSettings.HideDelayChoices)
        {
            HideDelayBox.Items.Add(new ComboBoxItem { Content = $"{seconds} seconds", Tag = seconds });
        }

        foreach (var size in Enum.GetValues<SubtitleSize>())
        {
            SubtitleSizeBox.Items.Add(new ComboBoxItem { Content = SubtitleStyle.Label(size), Tag = size });
        }

        foreach (var color in Enum.GetValues<SubtitleColor>())
        {
            SubtitleColorBox.Items.Add(new ComboBoxItem { Content = SubtitleStyle.Label(color), Tag = color });
        }

        foreach (var background in Enum.GetValues<SubtitleBackground>())
        {
            SubtitleBackgroundBox.Items.Add(new ComboBoxItem { Content = SubtitleStyle.Label(background), Tag = background });
        }

        foreach (var seconds in AppSettings.CountdownChoices)
        {
            CountdownBox.Items.Add(new ComboBoxItem { Content = $"{seconds} seconds", Tag = seconds });
        }

        foreach (var mode in (SkipMode[])[SkipMode.Button, SkipMode.Automatic, SkipMode.Never])
        {
            IntroSkipBox.Items.Add(new ComboBoxItem { Content = EpisodeRules.Label(mode), Tag = mode });
            RecapSkipBox.Items.Add(new ComboBoxItem { Content = EpisodeRules.Label(mode), Tag = mode });
        }

        var version = typeof(SettingsView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        VersionText.Text = $"Aperture {(plus > 0 ? version[..plus] : version)}";
    }

    /// <summary>Raised after a change has been applied and saved.</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>Raised after the keyboard shortcuts were changed and saved.</summary>
    internal event EventHandler<ShortcutMap>? ShortcutsChanged;

    /// <summary>Raised after history has been cleared.</summary>
    public event EventHandler? HistoryCleared;

    /// <summary>The user asked to add a library folder (the window shows the folder picker).</summary>
    public event EventHandler? AddFolderRequested;

    /// <summary>The YouTube key, sign-in or records changed.</summary>
    public event EventHandler? YouTubeChanged;

    /// <summary>The user deleted YouTube data; the player's browsing data goes too.</summary>
    public event EventHandler? YouTubeDataCleared;

    internal void Initialize(AppServices services, Func<ShortcutMap> shortcuts, bool subtitleBoxSupported)
    {
        _services = services;
        _shortcuts = shortcuts;
        DataFolderText.Text = services.DataLocation.Root;
        if (!subtitleBoxSupported)
        {
            SubtitleBackgroundCaption.Text = "This copy of the playback engine can only draw an outline; the dark box needs a newer libmpv.";
        }

        Show(services.Settings.Current);
        ShowKeyState();
        ShowYouTubeState();
        services.YouTube.Account.Changed += (_, _) => DispatcherQueue.TryEnqueue(ShowYouTubeState);
        services.Library.Changed += (_, _) => DispatcherQueue.TryEnqueue(async () => await RefreshFoldersAsync());
        services.Library.ProgressChanged += (_, progress) =>
        {
            if (progress is null)
            {
                DispatcherQueue.TryEnqueue(async () => await RefreshFoldersAsync());
            }
        };
        services.Analysis.ProgressChanged += (_, progress) => DispatcherQueue.TryEnqueue(() => ShowAnalysisProgress(progress));
        _ = RefreshFoldersAsync();
    }

    private void ShowAnalysisProgress(Aperture.Media.Analysis.AnalysisProgress? progress) =>
        FindSegmentsCaption.Text = progress is null
            ? "Compares episodes of each show by their sound and picture. Files are only read, never changed. When off, only episodes you open are checked."
            : $"Checking {progress.Current} ({progress.Done + 1} of {progress.Total}). Files are only read, never changed.";

    /// <summary>Reloads the folder rows (after adding, removing or scanning).</summary>
    public async Task RefreshFoldersAsync()
    {
        if (_services is null)
        {
            return;
        }

        IReadOnlyList<LibraryFolder> folders;
        try
        {
            folders = await _services.Library.GetFoldersAsync();
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not load library folders", error);
            return;
        }

        FolderRows.Children.Clear();
        foreach (var folder in folders)
        {
            FolderRows.Children.Add(FolderRow(folder));
        }
    }

    /// <summary>Shows why a folder could not be added (or confirms it was).</summary>
    public void ShowFolderMessage(string message) => FolderCaption.Text = message;

    /// <summary>Shows the current settings again (after the player changed one, such as the subtitle size).</summary>
    public void Refresh()
    {
        if (_services is not null)
        {
            Show(_services.Settings.Current);
        }
    }

    private void Show(AppSettings settings)
    {
        _loading = true;
        Select(ResumeBox, settings.Resume);
        Select(HideDelayBox, settings.ControlsHideDelaySeconds);
        PitchToggle.IsOn = settings.PreservePitch;
        Select(SubtitleSizeBox, settings.SubtitleStyle.Size);
        Select(SubtitleColorBox, settings.SubtitleStyle.Color);
        Select(SubtitleBackgroundBox, settings.SubtitleStyle.Background);
        AutoplayToggle.IsOn = settings.AutoplayNextEpisode;
        Select(CountdownBox, settings.NextEpisodeCountdownSeconds);
        CountdownBox.IsEnabled = settings.AutoplayNextEpisode;
        Select(IntroSkipBox, settings.IntroSkip);
        Select(RecapSkipBox, settings.RecapSkip);
        CreditsToggle.IsOn = settings.OfferSkipCredits;
        FindSegmentsToggle.IsOn = settings.FindSegments;
        YouTubeAutoplayToggle.IsOn = settings.YouTubeAutoplay;
        RememberToggle.IsOn = settings.RememberHistory;
        OnlineToggle.IsOn = settings.OnlineMetadata;
        KeyRow.Visibility = settings.OnlineMetadata ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    private static void Select<T>(ComboBox box, T value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value));

    private static T Selected<T>(ComboBox box, T fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: T value } ? value : fallback;

    private async void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is null)
        {
            return;
        }

        var current = _services.Settings.Current;
        var updated = current with
        {
            Resume = Selected(ResumeBox, current.Resume),
            ControlsHideDelaySeconds = Selected(HideDelayBox, current.ControlsHideDelaySeconds),
            PreservePitch = PitchToggle.IsOn,
            Subtitles = new SubtitleStyle(
                Selected(SubtitleSizeBox, current.SubtitleStyle.Size),
                Selected(SubtitleColorBox, current.SubtitleStyle.Color),
                Selected(SubtitleBackgroundBox, current.SubtitleStyle.Background)),
            AutoplayNextEpisode = AutoplayToggle.IsOn,
            NextEpisodeCountdownSeconds = Selected(CountdownBox, current.NextEpisodeCountdownSeconds),
            IntroSkip = Selected(IntroSkipBox, current.IntroSkip),
            RecapSkip = Selected(RecapSkipBox, current.RecapSkip),
            OfferSkipCredits = CreditsToggle.IsOn,
            FindSegments = FindSegmentsToggle.IsOn,
            YouTubeAutoplay = YouTubeAutoplayToggle.IsOn,
            RememberHistory = RememberToggle.IsOn,
            OnlineMetadata = OnlineToggle.IsOn,
        };
        if (updated == current)
        {
            return;
        }

        KeyRow.Visibility = updated.OnlineMetadata ? Visibility.Visible : Visibility.Collapsed;
        CountdownBox.IsEnabled = updated.AutoplayNextEpisode;
        _services.Settings.Current = updated;
        try
        {
            await _services.SettingsStore.SaveAsync(updated);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save settings", error);
        }

        SettingsChanged?.Invoke(this, updated);

        // Turning online details on looks up what the library already has.
        if (updated.OnlineMetadata && !current.OnlineMetadata && HasKey())
        {
            _services.Library.RequestScan();
        }
    }

    // ===== Keyboard shortcuts =====

    private async void OnShortcutsClick(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        var dialog = new ShortcutsDialog(_shortcuts()) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (ReferenceEquals(dialog.Map, _shortcuts()))
        {
            return;
        }

        try
        {
            await _services.ShortcutStore.SaveShortcutsAsync(dialog.Map.ToStorage());
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save keyboard shortcuts", error);
        }

        var changed = PlayerCommands.All.Count(dialog.Map.IsCustomized);
        ShortcutsCaption.Text = changed == 0 ? "Using the standard keys." : changed == 1 ? "1 key changed from the standard set." : $"{changed} keys changed from the standard set.";
        ShortcutsChanged?.Invoke(this, dialog.Map);
    }

    // ===== Library folders =====

    private Grid FolderRow(LibraryFolder folder)
    {
        var row = new Grid { Style = (Style)Application.Current.Resources["SettingRowStyle"] };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = Path.GetFileName(folder.Path.TrimEnd('\\', '/'));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name.Length > 0 ? name : folder.Path, Style = (Style)Application.Current.Resources["BodyStrongTextStyle"] });
        var available = Directory.Exists(folder.Path);
        var parts = new List<string> { MediaCategories.Label(folder.Kind) };
        parts.Add(folder.ItemCount == 1 ? "1 item" : $"{folder.ItemCount:N0} items");
        if (!available)
        {
            parts.Add("not available right now");
        }
        else if (folder.ScanIncomplete)
        {
            parts.Add("last scan didn’t finish");
        }
        else if (folder.LastScanCompletedUtc is { } scanned)
        {
            parts.Add($"scanned {RelativeDate.Describe(scanned, DateTimeOffset.Now)}");
        }

        text.Children.Add(new TextBlock
        {
            Text = $"{folder.Path}\n{string.Join(" · ", parts)}",
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            IsTextSelectionEnabled = true,
        });
        row.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 1);

        var scan = IconButton("", $"Scan {name} for changes");
        scan.IsEnabled = available;
        scan.Click += (_, _) => _services?.Library.RequestScan(folder.Id);
        actions.Children.Add(scan);

        var remove = IconButton("", $"Remove {name} from the library");
        var confirm = new Button
        {
            Content = "Remove from library",
            Style = (Style)Application.Current.Resources["ApertureAccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = new StackPanel
            {
                MaxWidth = 300,
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"Remove “{name}”?", Style = (Style)Application.Current.Resources["BodyStrongTextStyle"] },
                    new TextBlock
                    {
                        Text = "Aperture forgets this folder and its items. The files stay exactly where they are, and playback history is kept.",
                        Style = (Style)Application.Current.Resources["BodyTextStyle"],
                    },
                    confirm,
                },
            },
        };
        confirm.Click += async (_, _) =>
        {
            flyout.Hide();
            if (_services is not null)
            {
                try
                {
                    await _services.Library.RemoveFolderAsync(folder.Id);
                    ShowFolderMessage($"“{name}” was removed from the library. Its files weren’t touched.");
                }
                catch (System.Data.Common.DbException error)
                {
                    AppLog.Error("Could not remove a library folder", error);
                }

                await RefreshFoldersAsync();
            }
        };
        remove.Flyout = flyout;
        actions.Children.Add(remove);
        row.Children.Add(actions);
        return row;
    }

    private static Button IconButton(string glyph, string name)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, Style = (Style)Application.Current.Resources["IconGlyphStyle"] },
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);

    // ===== Online details key =====

    private bool HasKey() => _services?.Secrets.Read(AppServices.TmdbKeyName) is { Length: > 0 };

    private void ShowKeyState()
    {
        var saved = HasKey();
        RemoveKeyButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.PlaceholderText = saved ? "A key is saved. Paste a new one to replace it." : "Paste your API key or read access token";
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e) => SaveKeyButton.IsEnabled = KeyBox.Password.Trim().Length > 0;

    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        _services.Secrets.Write(AppServices.TmdbKeyName, KeyBox.Password.Trim());
        KeyBox.Password = string.Empty;
        ShowKeyState();
        KeyCaption.Text = HasKey()
            ? "Key saved in Windows Credential Manager. Artwork is looked up after each scan."
            : "Windows couldn’t save the key. Details are in Aperture’s log.";
        if (HasKey() && _services.Settings.Current.OnlineMetadata)
        {
            _services.Library.RequestScan();
        }
    }

    private void OnRemoveKeyClick(object sender, RoutedEventArgs e)
    {
        _services?.Secrets.Write(AppServices.TmdbKeyName, null);
        ShowKeyState();
        KeyCaption.Text = "Key removed. Artwork already downloaded stays until you remove the folder.";
    }

    // ===== YouTube =====

    /// <summary>Scrolls to the YouTube settings (from the YouTube page's "Set up" button).</summary>
    public void BringYouTubeIntoView() =>
        YouTubeHeader.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });

    private void ShowYouTubeState()
    {
        if (_services is null)
        {
            return;
        }

        var youTube = _services.YouTube;
        RemoveYouTubeKeyButton.Visibility = youTube.HasApiKey ? Visibility.Visible : Visibility.Collapsed;
        YouTubeKeyBox.PlaceholderText = youTube.HasApiKey ? "A key is saved. Paste a new one to replace it." : "Paste your API key";

        var client = youTube.Account.Client;
        YouTubeClientIdBox.Text = client?.ClientId ?? string.Empty;
        YouTubeClientSecretBox.PlaceholderText = client?.ClientSecret is { Length: > 0 } ? "Saved" : string.Empty;
        SaveYouTubeClientButton.IsEnabled = false;
        if (youTube.Account.IsSignedIn)
        {
            YouTubeAccountCaption.Text = "Signed in. Aperture can read your playlists and liked videos; it can’t change anything on YouTube.";
            YouTubeSignInButton.Content = "Sign out";
            YouTubeSignInButton.IsEnabled = true;
        }
        else
        {
            YouTubeAccountCaption.Text = client is null
                ? "Optional. Shows your playlists and liked videos. Set up a Google sign-in client below first."
                : "Optional. Shows your playlists and liked videos, read-only. Opens Google’s sign-in page in your browser.";
            YouTubeSignInButton.Content = "Sign in…";
            YouTubeSignInButton.IsEnabled = client is not null;
        }

        YouTubeClientExpander.IsExpanded = client is null && YouTubeClientExpander.IsExpanded;
    }

    private void OnYouTubeKeyChanged(object sender, RoutedEventArgs e) => SaveYouTubeKeyButton.IsEnabled = YouTubeKeyBox.Password.Trim().Length > 0;

    private void OnSaveYouTubeKeyClick(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        _services.YouTube.SetApiKey(YouTubeKeyBox.Password);
        YouTubeKeyBox.Password = string.Empty;
        YouTubeKeyCaption.Text = _services.YouTube.HasApiKey
            ? "Key saved in Windows Credential Manager. Search and popular videos are available in the YouTube section."
            : "Windows couldn’t save the key. Details are in Aperture’s log.";
        ShowYouTubeState();
        YouTubeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoveYouTubeKeyClick(object sender, RoutedEventArgs e)
    {
        _services?.YouTube.SetApiKey(null);
        YouTubeKeyCaption.Text = "Key removed. Links you paste still play; search needs a key.";
        ShowYouTubeState();
        YouTubeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnYouTubeClientChanged(object sender, RoutedEventArgs e) =>
        SaveYouTubeClientButton.IsEnabled = YouTubeClientIdBox.Text.Trim().Length > 0;

    private void OnSaveYouTubeClientClick(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        var secret = YouTubeClientSecretBox.Password.Length > 0 ? YouTubeClientSecretBox.Password : _services.YouTube.Account.Client?.ClientSecret;
        _services.YouTube.Account.SetClient(YouTubeClientIdBox.Text, secret);
        YouTubeClientSecretBox.Password = string.Empty;
        ShowYouTubeState();
    }

    private async void OnYouTubeSignInClick(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        var account = _services.YouTube.Account;
        YouTubeSignInButton.IsEnabled = false;
        try
        {
            if (account.IsSignedIn)
            {
                var revoked = await account.SignOutAsync(CancellationToken.None);
                YouTubeAccountCaption.Text = revoked
                    ? "Signed out, and Google was asked to remove Aperture’s access."
                    : "Signed out here. Google couldn’t be reached to remove Aperture’s access; you can remove it in your Google Account.";
            }
            else
            {
                YouTubeAccountCaption.Text = "Waiting for you to sign in in your browser…";
                await account.SignInAsync(async uri => await Windows.System.Launcher.LaunchUriAsync(uri), CancellationToken.None);
                ShowYouTubeState();
            }
        }
        catch (Aperture.YouTube.YouTubeApiException error)
        {
            AppLog.Warning($"YouTube sign-in failed: {error.Message}");
            ShowYouTubeState();
            YouTubeAccountCaption.Text = error.Problem == Aperture.YouTube.YouTubeProblem.Offline ? error.Explain() : error.Message;
        }
        finally
        {
            YouTubeSignInButton.IsEnabled = account.Client is not null;
        }

        YouTubeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnConfirmClearYouTube(object sender, RoutedEventArgs e)
    {
        ClearYouTubeFlyout.Hide();
        if (_services is null)
        {
            return;
        }

        try
        {
            await _services.YouTube.ClearAllAsync(CancellationToken.None);
            ClearYouTubeCaption.Text = "YouTube data deleted and signed out. Nothing on YouTube itself changed.";
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not delete YouTube data", error);
            ClearYouTubeCaption.Text = "Aperture couldn’t delete its YouTube records. Details are in Aperture’s log.";
        }

        ShowYouTubeState();
        YouTubeDataCleared?.Invoke(this, EventArgs.Empty);
        YouTubeChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===== History and data =====

    private async void OnConfirmClear(object sender, RoutedEventArgs e)
    {
        ClearFlyout.Hide();
        if (_services is null)
        {
            return;
        }

        await _services.History.ClearAsync();
        ClearHistoryCaption.Text = "History cleared. Your video files were not touched.";
        HistoryCleared?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            ShellActions.OpenFolder(_services.DataLocation.Root);
        }
    }
}
