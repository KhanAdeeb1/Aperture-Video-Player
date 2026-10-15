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
        var changed = new AppSettings(ResumePlayback: false, RememberHistory: false, ControlsHideDelaySeconds: 5);

        await store.SaveAsync(changed);
        await store.SaveAsync(changed);

        Assert.Equal(changed, await new SqliteSettingsStore(temp.Database).LoadAsync());
    }
}
