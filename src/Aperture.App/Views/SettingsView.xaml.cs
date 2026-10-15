using System.Reflection;
using Aperture.App.Services;
using Aperture.Core.Library;
using Aperture.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>The settings screen. Changes are saved as they are made.</summary>
internal sealed partial class SettingsView : UserControl
{
    private ISettingsStore? _store;
    private IPlaybackHistory? _history;
    private string? _dataFolder;
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        foreach (var seconds in AppSettings.HideDelayChoices)
        {
            HideDelayBox.Items.Add(new ComboBoxItem { Content = $"{seconds} seconds", Tag = seconds });
        }

        var version = typeof(SettingsView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        VersionText.Text = $"Aperture {(plus > 0 ? version[..plus] : version)}";
    }

    /// <summary>The settings currently in effect.</summary>
    public AppSettings Current { get; private set; } = AppSettings.Default;

    /// <summary>Raised after a change has been applied and saved.</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>Raised after history has been cleared.</summary>
    public event EventHandler? HistoryCleared;

    public void Initialize(ISettingsStore store, IPlaybackHistory history, AppSettings settings, string dataFolder)
    {
        _store = store;
        _history = history;
        _dataFolder = dataFolder;
        DataFolderText.Text = dataFolder;
        Show(settings);
    }

    private void Show(AppSettings settings)
    {
        _loading = true;
        Current = settings;
        ResumeToggle.IsOn = settings.ResumePlayback;
        RememberToggle.IsOn = settings.RememberHistory;
        HideDelayBox.SelectedItem = HideDelayBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == settings.ControlsHideDelaySeconds);
        _loading = false;
    }

    private async void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _store is null)
        {
            return;
        }

        var delay = HideDelayBox.SelectedItem is ComboBoxItem { Tag: int seconds } ? seconds : Current.ControlsHideDelaySeconds;
        var updated = new AppSettings(ResumeToggle.IsOn, RememberToggle.IsOn, delay);
        if (updated == Current)
        {
            return;
        }

        Current = updated;
        try
        {
            await _store.SaveAsync(updated);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            AppLog.Error("Could not save settings", error);
        }

        SettingsChanged?.Invoke(this, updated);
    }

    private async void OnConfirmClear(object sender, RoutedEventArgs e)
    {
        ClearFlyout.Hide();
        if (_history is null)
        {
            return;
        }

        await _history.ClearAsync();
        ClearHistoryCaption.Text = "History cleared. Your video files were not touched.";
        HistoryCleared?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (_dataFolder is not null)
        {
            ShellActions.OpenFolder(_dataFolder);
        }
    }
}
