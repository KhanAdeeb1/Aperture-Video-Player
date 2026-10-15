using Aperture.App.Services;
using Aperture.Core.Library;
using Aperture.Core.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>Settings › Media server. The server is a separate process; this only edits its settings and starts or stops it.</summary>
public sealed partial class SettingsView
{
    private readonly DispatcherTimer _serverTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ServerStatus? _serverStatus;
    private ServerSettings _serverSettings = ServerSettings.Default;
    private bool _serverBusy;
    private bool _loadingServer;
    private string _folderRowsShown = string.Empty;
    private string _deviceRowsShown = string.Empty;

    /// <summary>The user wants to choose ffmpeg.exe (the window shows the file picker).</summary>
    public event EventHandler? FfmpegPickRequested;

    private void InitializeMediaServer()
    {
        foreach (var count in ServerSettings.ConversionChoices)
        {
            ConversionsBox.Items.Add(new ComboBoxItem { Content = count == 1 ? "1 at a time" : $"{count} at a time", Tag = count });
        }

        // Checks the server only while Settings is on screen.
        _serverTimer.Tick += async (_, _) =>
        {
            if (Visibility == Visibility.Visible && !_serverBusy)
            {
                await RefreshServerAsync();
            }
        };
        _serverTimer.Start();
        _ = RefreshServerAsync();
    }

    /// <summary>Re-reads the server's settings, devices and status.</summary>
    public async Task RefreshServerAsync()
    {
        if (_services is null)
        {
            return;
        }

        IReadOnlyList<LibraryFolder> folders;
        IReadOnlySet<long> shared;
        IReadOnlyList<RemoteDevice> devices;
        bool hasPassphrase;
        try
        {
            _serverSettings = await _services.ServerStore.LoadSettingsAsync();
            folders = await _services.Library.GetFoldersAsync();
            shared = await _services.ServerStore.GetSharedFolderIdsAsync();
            devices = await _services.ServerStore.GetDevicesAsync();
            hasPassphrase = await _services.ServerStore.GetPassphraseHashAsync() is not null;
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not load the media server's settings", error);
            ServerStatusCaption.Text = "Aperture couldn’t read the media server’s settings. Details are in Aperture’s log.";
            return;
        }

        _serverStatus = await _services.MediaServer.GetStatusAsync();
        ShowServer(folders, shared, devices, hasPassphrase);
    }

    private void ShowServer(IReadOnlyList<LibraryFolder> folders, IReadOnlySet<long> shared, IReadOnlyList<RemoteDevice> devices, bool hasPassphrase)
    {
        _loadingServer = true;
        var status = _serverStatus;
        if (!MediaServerHost.IsInstalled)
        {
            ServerStatusCaption.Text = "This copy of Aperture doesn’t include the media server.";
            ServerStartButton.IsEnabled = false;
        }
        else if (!_serverBusy)
        {
            ServerStatusCaption.Text = status is null
                ? "Off."
                : status.ActiveStreams switch
                {
                    0 => "On.",
                    1 => "On. 1 device is playing.",
                    var n => $"On. {n} devices are playing.",
                } + (status.KeepingAwake ? " Keeping the computer awake." : string.Empty)
                  + (status.SetupAddresses.Count == 0 ? " This computer isn’t on a home network right now, so devices can’t reach it." : string.Empty);
            ServerStartButton.Content = status is null ? "Start" : "Stop";
            ServerStartButton.Style = (Style)Application.Current.Resources[status is null ? "ApertureAccentButtonStyle" : "ApertureButtonStyle"];
            ServerStartButton.IsEnabled = true;
        }

        ServerConnectRow.Visibility = status is { SetupAddresses.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null)
        {
            ServerSetupText.Text = string.Join("\n", status.SetupAddresses);
            CopySetupButton.Content = "Copy address";
            ServerFingerprintCaption.Visibility = Visibility.Visible;
            ServerFingerprintText.Text = SplitFingerprint(status.CertificateFingerprint);
        }

        PassphraseCaption.Text = hasPassphrase
            ? "Devices sign in with this. Changing it signs every device out."
            : $"No passphrase yet, so no device can sign in. Use at least {Passphrase.MinimumLength} characters; a few unrelated words work well.";

        // Rows are rebuilt only when something changed, so keyboard focus isn't lost on every check.
        var folderRows = string.Join("|", folders.Select(f => $"{f.Id}:{f.Path}:{shared.Contains(f.Id)}"));
        if (folderRows != _folderRowsShown)
        {
            _folderRowsShown = folderRows;
            SharedFolderRows.Children.Clear();
            foreach (var folder in folders)
            {
                SharedFolderRows.Children.Add(SharedFolderRow(folder, shared.Contains(folder.Id)));
            }
        }

        SharedFoldersCaption.Text = folders.Count == 0
            ? "Add library folders above first; then choose here which of them devices can see."
            : "Devices see only the library folders you turn on here. Nothing is shared until you choose, and files are only ever read.";

        var deviceRows = string.Join("|", devices.Select(d => $"{d.Id}:{d.Name}:{d.LastAddress}:{RelativeDate.Describe(d.LastSeenUtc, DateTimeOffset.Now)}"));
        if (deviceRows != _deviceRowsShown)
        {
            _deviceRowsShown = deviceRows;
            DeviceRows.Children.Clear();
            foreach (var device in devices)
            {
                DeviceRows.Children.Add(DeviceRow(device));
            }
        }

        DevicesCaption.Text = devices.Count switch
        {
            0 => "None yet.",
            1 => "1 device. A sign-in lasts 30 days from its last use.",
            var n => $"{n} devices. A sign-in lasts 30 days from its last use.",
        };
        SignOutAllButton.IsEnabled = devices.Count > 0;

        KeepAwakeToggle.IsOn = _serverSettings.KeepAwake;
        AllowVpnToggle.IsOn = _serverSettings.AllowVpn;
        StartWithWindowsToggle.IsOn = MediaServerHost.StartsWithWindows;
        Select(ConversionsBox, _serverSettings.MaxConversions);
        ConverterCaption.Text = status switch
        {
            { Converter: { } converter } => $"Files a device can play are sent as they are; others are converted with {converter}. The original is never changed.",
            { ConverterProblem: { } problem } => $"Files a device can play are sent as they are. To convert the rest, FFmpeg is needed: {problem}",
            _ => "Files a device can play are sent as they are. Others are converted on the fly with FFmpeg, which you install yourself; the original is never changed.",
        };
        AutomaticFfmpegButton.Visibility = _serverSettings.FfmpegPath is null ? Visibility.Collapsed : Visibility.Visible;
        if (!SavePortsButton.IsEnabled)
        {
            PortBox.Value = _serverSettings.Port;
            SetupPortBox.Value = _serverSettings.SetupPort;
        }

        _loadingServer = false;
    }

    private Grid SharedFolderRow(LibraryFolder folder, bool isShared)
    {
        var row = new Grid { Style = (Style)Application.Current.Resources["SettingRowStyle"], MinHeight = 48, Padding = new Thickness(32, 8, 16, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Path.GetFileName(folder.Path.TrimEnd('\\', '/'));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name.Length > 0 ? name : folder.Path, Style = (Style)Application.Current.Resources["BodyTextStyle"] });
        text.Children.Add(new TextBlock { Text = $"{MediaCategories.Label(folder.Kind)} · {folder.Path}", Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
        row.Children.Add(text);

        var toggle = new ToggleSwitch { IsOn = isShared, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"Share {name}");
        toggle.Toggled += async (_, _) =>
        {
            if (_services is null)
            {
                return;
            }

            try
            {
                await _services.ServerStore.SetFolderSharedAsync(folder.Id, toggle.IsOn);
            }
            catch (System.Data.Common.DbException error)
            {
                AppLog.Error("Could not change a shared folder", error);
                await RefreshServerAsync();
            }
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private Grid DeviceRow(RemoteDevice device)
    {
        var row = new Grid { Style = (Style)Application.Current.Resources["SettingRowStyle"], MinHeight = 48, Padding = new Thickness(32, 8, 16, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = device.Name, Style = (Style)Application.Current.Resources["BodyTextStyle"] });
        var detail = $"Last used {RelativeDate.Describe(device.LastSeenUtc, DateTimeOffset.Now)}";
        if (device.LastAddress is { Length: > 0 } address)
        {
            detail += $" from {address}";
        }

        text.Children.Add(new TextBlock { Text = detail, Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
        row.Children.Add(text);

        var signOut = new Button { Content = "Sign out", Style = (Style)Application.Current.Resources["ApertureButtonStyle"], VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(signOut, $"Sign out {device.Name}");
        signOut.Click += async (_, _) =>
        {
            if (_services is null)
            {
                return;
            }

            try
            {
                await _services.ServerStore.RemoveDeviceAsync(device.Id);
            }
            catch (System.Data.Common.DbException error)
            {
                AppLog.Error("Could not sign a device out", error);
            }

            await RefreshServerAsync();
        };
        Grid.SetColumn(signOut, 1);
        row.Children.Add(signOut);
        return row;
    }

    private async void OnServerStartStopClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _serverBusy)
        {
            return;
        }

        _serverBusy = true;
        ServerStartButton.IsEnabled = false;
        var stopping = _serverStatus is not null;
        ServerStatusCaption.Text = stopping ? "Stopping…" : "Starting…";
        var problem = stopping ? await _services.MediaServer.StopAsync() : await _services.MediaServer.StartAsync();
        _serverBusy = false;
        await RefreshServerAsync();
        if (problem is not null)
        {
            ServerStatusCaption.Text = problem;
        }
    }

    private void OnCopySetupClick(object sender, RoutedEventArgs e)
    {
        if (_serverStatus is not { SetupAddresses.Count: > 0 } status)
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(status.SetupAddresses[0]);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        CopySetupButton.Content = "Copied";
    }

    private void OnPassphraseChanged(object sender, RoutedEventArgs e) =>
        SavePassphraseButton.IsEnabled = PassphraseBox.Password.Length > 0 && PassphraseAgainBox.Password.Length > 0;

    private async void OnSavePassphraseClick(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        if (PassphraseBox.Password != PassphraseAgainBox.Password)
        {
            PassphraseCaption.Text = "The two passphrases don’t match. Nothing was changed.";
            return;
        }

        if (Passphrase.Check(PassphraseBox.Password) is { } problem)
        {
            PassphraseCaption.Text = problem;
            return;
        }

        try
        {
            // Hashing takes a moment on purpose, so it runs off the interface thread.
            var passphrase = PassphraseBox.Password;
            var hash = await Task.Run(() => Passphrase.Hash(passphrase));
            await _services.ServerStore.SetPassphraseHashAsync(hash);
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save the media server passphrase", error);
            PassphraseCaption.Text = "Aperture couldn’t save the passphrase. Details are in Aperture’s log.";
            return;
        }

        PassphraseBox.Password = string.Empty;
        PassphraseAgainBox.Password = string.Empty;
        await RefreshServerAsync();
        PassphraseCaption.Text = "Passphrase saved. Any device that was signed in needs it to sign in again.";
    }

    private async void OnConfirmSignOutAll(object sender, RoutedEventArgs e)
    {
        SignOutAllFlyout.Hide();
        if (_services is null)
        {
            return;
        }

        try
        {
            await _services.ServerStore.RemoveAllDevicesAsync();
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not sign devices out", error);
        }

        await RefreshServerAsync();
    }

    private async void OnServerSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingServer || _services is null)
        {
            return;
        }

        if (ReferenceEquals(sender, StartWithWindowsToggle) && StartWithWindowsToggle.IsOn != MediaServerHost.StartsWithWindows)
        {
            try
            {
                MediaServerHost.SetStartsWithWindows(StartWithWindowsToggle.IsOn);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                AppLog.Error("Could not change the sign-in entry for the media server", error);
                ServerStatusCaption.Text = "Windows didn’t allow Aperture to change its sign-in entry. Details are in Aperture’s log.";
            }
        }

        await SaveServerSettingsAsync(_serverSettings with
        {
            KeepAwake = KeepAwakeToggle.IsOn,
            AllowVpn = AllowVpnToggle.IsOn,
            StartWithWindows = MediaServerHost.StartsWithWindows,
            MaxConversions = Selected(ConversionsBox, _serverSettings.MaxConversions),
        });
    }

    private async Task SaveServerSettingsAsync(ServerSettings updated)
    {
        if (_services is null || updated == _serverSettings)
        {
            return;
        }

        try
        {
            await _services.ServerStore.SaveSettingsAsync(updated);
            _serverSettings = updated.Normalized();
        }
        catch (System.Data.Common.DbException error)
        {
            AppLog.Error("Could not save the media server's settings", error);
        }

        await RefreshServerAsync();
    }

    private void OnChooseFfmpegClick(object sender, RoutedEventArgs e) => FfmpegPickRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Uses the ffmpeg.exe the user picked (ffprobe.exe is expected next to it).</summary>
    public async Task SetFfmpegPathAsync(string path)
    {
        if (!string.Equals(Path.GetFileName(path), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            ConverterCaption.Text = "Choose the file named ffmpeg.exe. It needs ffprobe.exe in the same folder.";
            return;
        }

        await SaveServerSettingsAsync(_serverSettings with { FfmpegPath = path });
    }

    private async void OnAutomaticFfmpegClick(object sender, RoutedEventArgs e) =>
        await SaveServerSettingsAsync(_serverSettings with { FfmpegPath = null });

    private void OnPortChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingServer)
        {
            return;
        }

        var (port, setup) = ((int)PortBox.Value, (int)SetupPortBox.Value);
        SavePortsButton.IsEnabled = !double.IsNaN(PortBox.Value) && !double.IsNaN(SetupPortBox.Value)
            && (port != _serverSettings.Port || setup != _serverSettings.SetupPort);
    }

    private async void OnSavePortsClick(object sender, RoutedEventArgs e)
    {
        var (port, setup) = ((int)PortBox.Value, (int)SetupPortBox.Value);
        if (!ServerSettings.IsUsablePort(port) || !ServerSettings.IsUsablePort(setup) || port == setup)
        {
            PortsCaption.Text = "Choose two different ports from 1024 to 65535.";
            return;
        }

        SavePortsButton.IsEnabled = false;
        await SaveServerSettingsAsync(_serverSettings with { Port = port, SetupPort = setup });
        PortsCaption.Text = _serverStatus is null
            ? "Saved. The server uses them when it starts."
            : "Saved. Stop and start the server to use them.";
    }

    /// <summary>"AA:BB:…" (32 bytes) as two lines of 16 bytes: it has no spaces, so it wouldn't otherwise wrap.</summary>
    private static string SplitFingerprint(string fingerprint)
    {
        var bytes = fingerprint.Split(':');
        var half = (bytes.Length + 1) / 2;
        return bytes.Length < 2 ? fingerprint : string.Join(':', bytes[..half]) + ":\n" + string.Join(':', bytes[half..]);
    }
}
