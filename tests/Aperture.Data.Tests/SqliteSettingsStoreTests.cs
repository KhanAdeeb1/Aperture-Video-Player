using Aperture.Core.Playback;
using Aperture.Core.Settings;

namespace Aperture.Data.Tests;

public class SqliteSettingsStoreTests
{
    [Fact]
    public async Task A_new_database_returns_defaults()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        Assert.Equal(AppSettings.Default, await new SqliteSettingsStore(temp.Database).LoadAsync());
    }

    [Fact]
    public async Task Saved_settings_round_trip()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSettingsStore(temp.Database);
        var changed = new AppSettings(
            Resume: ResumeMode.Ask,
            RememberHistory: false,
            ControlsHideDelaySeconds: 5,
            PreservePitch: false,
            Subtitles: new SubtitleStyle(SubtitleSize.Large, SubtitleColor.Yellow, SubtitleBackground.Box),
            Volume: 0.35,
            Muted: true,
            OnlineMetadata: true,
            Sound: new SoundOptions(EvenVolume: true),
            SeekPreviews: false);

        await store.SaveAsync(changed);
        await store.SaveAsync(changed);

        Assert.Equal(changed, await new SqliteSettingsStore(temp.Database).LoadAsync());
    }

    [Fact]
    public async Task Shortcuts_round_trip_and_replace_previous_customisations()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSettingsStore(temp.Database);
        await store.SaveShortcutsAsync(new Dictionary<string, string> { ["Mute"] = "U", ["FrameBack"] = "" });
        await store.SaveShortcutsAsync(new Dictionary<string, string> { ["Mute"] = "Ctrl+U" });

        var loaded = await store.LoadShortcutsAsync();
        Assert.Equal("Ctrl+U", Assert.Single(loaded).Value);

        // Saving shortcuts leaves ordinary settings alone.
        await store.SaveAsync(AppSettings.Default with { ControlsHideDelaySeconds = 10 });
        await store.SaveShortcutsAsync(new Dictionary<string, string>());
        Assert.Equal(10, (await store.LoadAsync()).ControlsHideDelaySeconds);
        Assert.Empty(await store.LoadShortcutsAsync());
    }
}
