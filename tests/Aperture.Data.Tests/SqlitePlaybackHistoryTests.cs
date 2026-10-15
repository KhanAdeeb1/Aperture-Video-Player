using Aperture.Core.Library;

namespace Aperture.Data.Tests;

public class SqlitePlaybackHistoryTests
{
    private static readonly DateTimeOffset Played = new(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);

    private static HistoryEntry Entry(string name, int minutesLater = 0, double position = 60) =>
        new($"C:\\Videos\\{name}.mp4", name, TimeSpan.FromSeconds(position), TimeSpan.FromMinutes(42), Played.AddMinutes(minutesLater));

    [Fact]
    public async Task Saved_entries_round_trip_most_recent_first()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var history = new SqlitePlaybackHistory(temp.Database);

        await history.SaveAsync(Entry("Older"));
        await history.SaveAsync(Entry("Newer", minutesLater: 5));

        var all = await history.GetAllAsync();
        Assert.Equal(["Newer", "Older"], all.Select(e => e.Title));
        Assert.Equal(Entry("Newer", minutesLater: 5), all[0]);
    }

    [Fact]
    public async Task Saving_the_same_path_updates_it_ignoring_case()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var history = new SqlitePlaybackHistory(temp.Database);

        await history.SaveAsync(Entry("Clip"));
        await history.SaveAsync(Entry("Clip", minutesLater: 1, position: 300) with { Path = "C:\\VIDEOS\\clip.MP4" });

        var all = await history.GetAllAsync();
        Assert.Single(all);
        Assert.Equal(TimeSpan.FromSeconds(300), all[0].Position);
        Assert.NotNull(await history.FindAsync("c:\\videos\\CLIP.mp4"));
    }

    [Fact]
    public async Task Remove_and_clear_forget_entries()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var history = new SqlitePlaybackHistory(temp.Database);
        await history.SaveAsync(Entry("A"));
        await history.SaveAsync(Entry("B"));

        await history.RemoveAsync(Entry("A").Path);
        Assert.Null(await history.FindAsync(Entry("A").Path));
        Assert.Single(await history.GetAllAsync());

        await history.ClearAsync();
        Assert.Empty(await history.GetAllAsync());
    }
}
