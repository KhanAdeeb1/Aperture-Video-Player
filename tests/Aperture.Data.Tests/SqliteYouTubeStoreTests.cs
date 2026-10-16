using Aperture.Core.Settings;
using Aperture.Core.YouTube;

namespace Aperture.Data.Tests;

public class SqliteYouTubeStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_792_000_000_000);
    private static readonly YouTubeVideo Zoo = new("jNQXAC9IVRw", "Me at the zoo", "jawed", "https://i.ytimg.com/vi/jNQXAC9IVRw/mqdefault.jpg", TimeSpan.FromSeconds(19));

    [Fact]
    public async Task Progress_and_details_round_trip()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteYouTubeStore(temp.Database);

        await store.RecordProgressAsync(Zoo.Id, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(19), T0);
        await store.SetDetailsAsync(Zoo, T0);
        await store.RecordProgressAsync(Zoo.Id, TimeSpan.FromSeconds(14), TimeSpan.Zero, T0.AddMinutes(1));

        var entry = await store.GetAsync(Zoo.Id);
        Assert.NotNull(entry);
        Assert.Equal(TimeSpan.FromSeconds(14), entry.Position);
        Assert.Equal(TimeSpan.FromSeconds(19), entry.Duration); // an unknown length doesn't erase a known one
        Assert.Equal(T0.AddMinutes(1), entry.WatchedUtc);
        Assert.Equal(Zoo with { Status = null }, entry.Video);
        Assert.Equal(T0, entry.DetailsUtc);
        Assert.Null(entry.SavedUtc);
    }

    [Fact]
    public async Task Details_are_only_stored_for_videos_aperture_has_a_record_of()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteYouTubeStore(temp.Database);
        await store.SetDetailsAsync(Zoo, T0);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task Clearing_details_keeps_apertures_own_record()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteYouTubeStore(temp.Database);
        await store.RecordProgressAsync(Zoo.Id, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(19), T0);
        await store.SetDetailsAsync(Zoo, T0);

        await store.ClearDetailsAsync([Zoo.Id]);

        var entry = await store.GetAsync(Zoo.Id);
        Assert.NotNull(entry);
        Assert.Null(entry.Video);
        Assert.Null(entry.DetailsUtc);
        Assert.Equal(TimeSpan.FromSeconds(12), entry.Position);
    }

    [Fact]
    public async Task Saving_and_forgetting_remove_rows_only_when_nothing_else_needs_them()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteYouTubeStore(temp.Database);

        await store.SetSavedAsync(Zoo.Id, T0);
        Assert.Equal(T0, (await store.GetAsync(Zoo.Id))!.SavedUtc);
        await store.RecordProgressAsync(Zoo.Id, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(19), T0);

        await store.SetSavedAsync(Zoo.Id, null);
        Assert.NotNull(await store.GetAsync(Zoo.Id)); // still in progress

        await store.ForgetProgressAsync(Zoo.Id);
        Assert.Null(await store.GetAsync(Zoo.Id)); // neither watched nor saved

        await store.SetSavedAsync(Zoo.Id, T0);
        await store.RecordProgressAsync(Zoo.Id, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(19), T0);
        await store.ForgetProgressAsync(Zoo.Id);
        var saved = await store.GetAsync(Zoo.Id);
        Assert.NotNull(saved);
        Assert.Null(saved.WatchedUtc);
        Assert.Equal(TimeSpan.Zero, saved.Position);
    }

    [Fact]
    public async Task Clear_all_removes_everything()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteYouTubeStore(temp.Database);
        await store.SetSavedAsync(Zoo.Id, T0);
        await store.RecordProgressAsync("aaaaaaaaaaa", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(19), T0);
        await store.ClearAllAsync();
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task Youtube_settings_round_trip_and_default_off()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var settings = new SqliteSettingsStore(temp.Database);
        var loaded = await settings.LoadAsync();
        Assert.False(loaded.YouTubeAutoplay);
        Assert.False(loaded.YouTubeAccepted);
        // Independent of the episode autoplay setting.
        Assert.True(loaded.AutoplayNextEpisode);

        var changed = AppSettings.Default with { YouTubeAutoplay = true, YouTubeAccepted = true, AutoplayNextEpisode = false };
        await settings.SaveAsync(changed);
        Assert.Equal(changed, await new SqliteSettingsStore(temp.Database).LoadAsync());
    }
}
