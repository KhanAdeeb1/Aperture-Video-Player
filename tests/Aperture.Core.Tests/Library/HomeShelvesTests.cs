using Aperture.Core.Library;

namespace Aperture.Core.Tests.Library;

public class HomeShelvesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static HistoryEntry Entry(string title, double position, double duration, int minutesAgo) =>
        new($"C:\\Videos\\{title}.mp4", title, TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(duration), Now.AddMinutes(-minutesAgo));

    [Fact]
    public void Empty_history_gives_empty_shelves() =>
        Assert.True(HomeShelves.From([]).IsEmpty);

    [Fact]
    public void Each_file_appears_in_one_shelf_most_recent_first()
    {
        var shelves = HomeShelves.From(
        [
            Entry("Finished", 3600, 3600, 1),
            Entry("Halfway", 1800, 3600, 5),
            Entry("Just started", 3, 3600, 2),
            Entry("Older halfway", 900, 3600, 60),
        ]);

        Assert.Equal(["Halfway", "Older halfway"], shelves.ContinueWatching.Select(e => e.Title));
        Assert.Equal(["Finished", "Just started"], shelves.RecentlyPlayed.Select(e => e.Title));
    }

    [Fact]
    public void Continue_watching_is_capped()
    {
        var many = Enumerable.Range(0, 20).Select(i => Entry($"Clip {i}", 600, 3600, i));
        var shelves = HomeShelves.From(many);
        Assert.Equal(HomeShelves.ContinueWatchingLimit, shelves.ContinueWatching.Count);
        Assert.Equal(20 - HomeShelves.ContinueWatchingLimit, shelves.RecentlyPlayed.Count);
    }

    [Fact]
    public void Search_matches_every_word_in_any_order_ignoring_case()
    {
        var history = new[] { Entry("Harbour Lights", 0, 60, 1), Entry("Night Drive", 0, 60, 2), Entry("Lights Out", 0, 60, 3) };

        Assert.Equal(["Harbour Lights", "Lights Out"], HomeShelves.Search(history, "lights").Select(e => e.Title));
        Assert.Equal(["Harbour Lights"], HomeShelves.Search(history, "LIGHTS harbour").Select(e => e.Title));
        Assert.Empty(HomeShelves.Search(history, "   "));
        Assert.Empty(HomeShelves.Search(history, "ocean"));
    }
}
