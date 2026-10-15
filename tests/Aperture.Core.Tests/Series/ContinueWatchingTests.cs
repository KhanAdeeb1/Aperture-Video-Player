using Aperture.Core.Library;
using Aperture.Core.Series;
using static Aperture.Core.Tests.Series.SeriesTestData;

namespace Aperture.Core.Tests.Series;

public class ContinueWatchingTests
{
    private static readonly Dictionary<string, bool> NoMarks = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_finished_episode_puts_the_next_one_up()
    {
        var e1 = Episode("Night Shift", 1, 1);
        var e2 = Episode("Night Shift", 1, 2);
        var catalog = SeriesCatalog.Build(CopyGroup.Build([e1, e2]));

        var items = ContinueWatching.Build([Played(e1, 41.5, T0)], catalog, NoMarks);

        var item = Assert.Single(items);
        Assert.Equal(e2.Path, item.Path);
        Assert.True(item.IsUpNext);
        Assert.Null(item.History);
    }

    [Fact]
    public void A_show_appears_once_as_its_most_recent_episode()
    {
        var e1 = Episode("Night Shift", 1, 1);
        var e2 = Episode("Night Shift", 1, 2);
        var e3 = Episode("Night Shift", 1, 3);
        var catalog = SeriesCatalog.Build(CopyGroup.Build([e1, e2, e3]));
        HistoryEntry[] history = [Played(e1, 41.5, T0), Played(e2, 12, T0.AddHours(1))];

        var item = Assert.Single(ContinueWatching.Build(history, catalog, NoMarks));

        Assert.Equal(e2.Path, item.Path);
        Assert.False(item.IsUpNext);
        Assert.Equal(TimeSpan.FromMinutes(12), item.History!.Position);
    }

    [Fact]
    public void Up_next_skips_episodes_already_watched_or_marked()
    {
        var e1 = Episode("Night Shift", 1, 1);
        var e2 = Episode("Night Shift", 1, 2);
        var e3 = Episode("Night Shift", 1, 3);
        var e4 = Episode("Night Shift", 1, 4);
        var catalog = SeriesCatalog.Build(CopyGroup.Build([e1, e2, e3, e4]));
        var marks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [e3.Path] = true };

        var items = ContinueWatching.Build([Played(e1, 42, T0.AddHours(2)), Played(e2, 42, T0)], catalog, marks);

        Assert.Equal(e4.Path, Assert.Single(items).Path);
    }

    [Fact]
    public void A_finished_show_leaves_the_row_and_films_stay_while_part_watched()
    {
        var e1 = Episode("Night Shift", 1, 1);
        var film = new LibraryItem { Path = @"D:\Films\Arrival.mkv", Title = "Arrival", Category = MediaCategory.Movie, Duration = TimeSpan.FromMinutes(116) };
        var catalog = SeriesCatalog.Build(CopyGroup.Build([e1, film]));

        var items = ContinueWatching.Build([Played(e1, 42, T0.AddHours(1)), Played(film, 30, T0)], catalog, NoMarks);

        var only = Assert.Single(items);
        Assert.Equal(film.Path, only.Path);
        Assert.Null(only.Episode);
    }

    [Fact]
    public void Shows_are_ordered_by_when_they_were_last_watched()
    {
        var a1 = Episode("Night Shift", 1, 1);
        var a2 = Episode("Night Shift", 1, 2);
        var b1 = Episode("Lantern Row", 1, 1);
        var catalog = SeriesCatalog.Build(CopyGroup.Build([a1, a2, b1]));

        var items = ContinueWatching.Build([Played(a1, 42, T0), Played(b1, 10, T0.AddDays(1))], catalog, NoMarks);

        Assert.Equal([b1.Path, a2.Path], items.Select(i => i.Path));
    }
}
