using Aperture.Core.Playback;

namespace Aperture.Data.Tests;

public class SqliteItemStateStoreTests
{
    [Fact]
    public async Task Preferences_round_trip_per_file()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteItemStateStore(temp.Database);
        Assert.Equal(PlaybackPreferences.None, await store.GetPreferencesAsync(@"D:\a.mkv"));

        var chosen = new PlaybackPreferences(
            AudioTrackId: 2,
            SubtitleTrackId: null,
            SubtitlesOff: true,
            SubtitleDelay: TimeSpan.FromMilliseconds(-300),
            AudioDelay: TimeSpan.FromMilliseconds(120),
            Aspect: AspectMode.Fill,
            Speed: 1.5);
        await store.SavePreferencesAsync(@"D:\a.mkv", chosen);

        Assert.Equal(chosen, await store.GetPreferencesAsync(@"d:\A.MKV"));
        Assert.Equal(PlaybackPreferences.None, await store.GetPreferencesAsync(@"D:\b.mkv"));
    }

    [Fact]
    public async Task Watched_marks_set_and_clear_without_touching_preferences()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteItemStateStore(temp.Database);
        await store.SavePreferencesAsync(@"D:\a.mkv", new PlaybackPreferences(Speed: 1.25));
        await store.SetWatchedAsync(@"D:\a.mkv", true);
        await store.SetWatchedAsync(@"D:\b.mkv", false);

        var marks = await store.GetWatchedOverridesAsync();
        Assert.True(marks[@"d:\a.mkv"]);
        Assert.False(marks[@"D:\b.mkv"]);
        Assert.Equal(1.25, (await store.GetPreferencesAsync(@"D:\a.mkv")).Speed);

        await store.SetWatchedAsync(@"D:\a.mkv", null);
        Assert.False((await store.GetWatchedOverridesAsync()).ContainsKey(@"D:\a.mkv"));
    }

    [Fact]
    public async Task Damaged_values_are_clamped()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        await using (var connection = await temp.Database.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO item_state (path, subtitle_delay_ms, aspect, speed) VALUES ('x', 99999999, 'Sideways', 40);";
            await command.ExecuteNonQueryAsync();
        }

        var preferences = await new SqliteItemStateStore(temp.Database).GetPreferencesAsync("x");
        Assert.Equal(SyncOffset.Limit, preferences.SubtitleDelay);
        Assert.Null(preferences.Aspect);
        Assert.Equal(4, preferences.Speed);
    }
}
