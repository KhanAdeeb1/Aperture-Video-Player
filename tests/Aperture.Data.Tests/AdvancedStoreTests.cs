using Aperture.Core.Bookmarks;
using Aperture.Core.Playback;
using Aperture.Core.Series;
using Aperture.Core.Settings;

namespace Aperture.Data.Tests;

/// <summary>Milestone 9 storage: bookmarks, per-file sound options, show choices and new settings.</summary>
public class AdvancedStoreTests
{
    [Fact]
    public async Task Bookmarks_are_kept_per_file_in_time_order()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteBookmarkStore(temp.Database);
        var late = await store.AddAsync(@"D:\Film.mkv", TimeSpan.FromMinutes(42), "The lighthouse scene");
        var early = await store.AddAsync(@"D:\Film.mkv", TimeSpan.FromSeconds(75.5), "  first\nline  ");
        await store.AddAsync(@"D:\Other.mkv", TimeSpan.FromMinutes(1), string.Empty);

        var list = await store.ListAsync(@"d:\film.MKV");
        Assert.Equal([early.Id, late.Id], list.Select(b => b.Id));
        Assert.Equal("first line", list[0].Note);
        Assert.Equal(TimeSpan.FromSeconds(75.5), list[0].Position);

        await store.UpdateNoteAsync(late.Id, "Changed");
        await store.DeleteAsync(early.Id);
        var after = await store.ListAsync(@"D:\Film.mkv");
        Assert.Equal("Changed", Assert.Single(after).Note);
    }

    [Fact]
    public async Task Bookmark_notes_are_limited()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteBookmarkStore(temp.Database);
        var added = await store.AddAsync(@"D:\a.mkv", TimeSpan.FromSeconds(-5), new string('x', 2000));
        Assert.Equal(TimeSpan.Zero, added.Position);
        Assert.Equal(Bookmark.MaximumNoteLength, (await store.ListAsync(@"D:\a.mkv"))[0].Note.Length);
    }

    [Fact]
    public async Task Sound_options_round_trip_per_file_and_stay_unset_otherwise()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteItemStateStore(temp.Database);
        await store.SavePreferencesAsync(@"D:\a.mkv", new PlaybackPreferences(Sound: new SoundOptions(ClearDialogue: true)));
        await store.SavePreferencesAsync(@"D:\b.mkv", new PlaybackPreferences(Speed: 1.5));
        Assert.Equal(new SoundOptions(ClearDialogue: true), (await store.GetPreferencesAsync(@"D:\a.mkv")).Sound);
        Assert.Null((await store.GetPreferencesAsync(@"D:\b.mkv")).Sound);
    }

    [Fact]
    public async Task Show_choices_round_trip_and_reset()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSeriesStore(temp.Database);
        Assert.Equal(ShowChoices.None, await store.GetShowChoicesAsync("harbour|"));
        var choices = new ShowChoices("jpn", "eng", false, 1.25, new SoundOptions(EvenVolume: true));
        await store.SaveShowChoicesAsync("harbour|", choices);
        Assert.Equal(choices, await store.GetShowChoicesAsync("harbour|"));
        await store.SaveShowChoicesAsync("harbour|", ShowChoices.None);
        Assert.Equal(ShowChoices.None, await store.GetShowChoicesAsync("harbour|"));
    }

    [Fact]
    public async Task Sound_and_preview_settings_round_trip()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSettingsStore(temp.Database);
        var loaded = await store.LoadAsync();
        Assert.Equal(SoundOptions.Off, loaded.SoundDefaults);
        Assert.True(loaded.SeekPreviews);

        await store.SaveAsync(AppSettings.Default with { Sound = new SoundOptions(true, true), SeekPreviews = false });
        var again = await store.LoadAsync();
        Assert.Equal(new SoundOptions(true, true), again.SoundDefaults);
        Assert.False(again.SeekPreviews);
    }
}
