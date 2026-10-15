using Aperture.Core.Library;
using Aperture.Core.Series;
using static Aperture.Core.Tests.Series.SeriesTestData;

namespace Aperture.Core.Tests.Series;

public class SeriesCatalogTests
{
    private static SeriesCatalog Build(params LibraryItem[] items) => SeriesCatalog.Build(CopyGroup.Build(items));

    [Fact]
    public void Episodes_are_ordered_by_season_and_number_not_by_name_or_row()
    {
        var catalog = Build(
            Episode("Night Shift", 2, 1, id: 1),
            Episode("Night Shift", 1, 10, id: 2),
            Episode("Night Shift", 1, 2, id: 3),
            Episode("Night Shift", 1, 1, id: 4));

        var show = Assert.Single(catalog.All);
        Assert.Equal(["S1 E1", "S1 E2", "S1 E10", "S2 E1"], show.Episodes.Select(e => e.Label));
        Assert.Equal([1, 2], show.Seasons);
    }

    [Fact]
    public void Next_and_previous_cross_seasons_and_stop_at_the_ends()
    {
        var e1 = Episode("Night Shift", 1, 1);
        var e2 = Episode("Night Shift", 1, 2);
        var s2 = Episode("Night Shift", 2, 1);
        var catalog = Build(s2, e2, e1);

        Assert.Equal(e2.Path, catalog.Next(e1.Path)!.Item.Path);
        Assert.Equal(s2.Path, catalog.Next(e2.Path)!.Item.Path);
        Assert.Null(catalog.Next(s2.Path));
        Assert.Equal(e2.Path, catalog.Previous(s2.Path)!.Item.Path);
        Assert.Null(catalog.Previous(e1.Path));
    }

    [Fact]
    public void A_gap_in_numbering_is_skipped_over()
    {
        var e1 = Episode("Night Shift", 1, 1);
        var e4 = Episode("Night Shift", 1, 4);
        var catalog = Build(e1, e4);
        Assert.Equal(e4.Path, catalog.Next(e1.Path)!.Item.Path);
    }

    [Fact]
    public void A_file_holding_two_episodes_continues_after_the_second()
    {
        var double12 = Episode("Night Shift", 1, 1, end: 2);
        var e3 = Episode("Night Shift", 1, 3);
        var catalog = Build(e3, double12);

        Assert.Equal("S1 E1–2", catalog.EpisodeOf(double12.Path)!.Label);
        Assert.Equal(e3.Path, catalog.Next(double12.Path)!.Item.Path);
        Assert.Equal(double12.Path, catalog.Previous(e3.Path)!.Item.Path);
    }

    [Fact]
    public void Specials_form_their_own_chain_and_never_interrupt_the_season()
    {
        var e1 = Episode("Night Shift", 1, 1);
        var e2 = Episode("Night Shift", 1, 2);
        var special1 = Episode("Night Shift", 0, 1);
        var special2 = Episode("Night Shift", 0, 2);
        var catalog = Build(special1, e2, special2, e1);

        Assert.Equal(e2.Path, catalog.Next(e1.Path)!.Item.Path);
        Assert.Null(catalog.Next(e2.Path));
        Assert.Equal(special2.Path, catalog.Next(special1.Path)!.Item.Path);
        Assert.Equal("Special 1", catalog.EpisodeOf(special1.Path)!.Label);
        Assert.True(catalog.All.Single().Episodes[^1].IsSpecial);
    }

    [Fact]
    public void Shows_with_the_same_name_and_different_years_stay_apart()
    {
        var old = Episode("Doctor Who", 1, 1, path: @"D:\TV\Doctor Who 1963\S01E01.mkv", year: 1963);
        var revival = Episode("Doctor Who", 1, 1, path: @"D:\TV\Doctor Who 2005\S01E01.mkv", year: 2005);
        var revival2 = Episode("Doctor Who", 1, 2, path: @"D:\TV\Doctor Who 2005\S01E02.mkv", year: 2005);
        var catalog = Build(old, revival, revival2);

        Assert.Equal(2, catalog.All.Count);
        Assert.Null(catalog.Next(old.Path));
        Assert.Equal(revival2.Path, catalog.Next(revival.Path)!.Item.Path);
    }

    [Fact]
    public void Next_prefers_the_copy_from_the_same_folder()
    {
        var hd1 = Episode("Night Shift", 1, 1, path: @"D:\TV\Night Shift 1080p\S01E01.mkv");
        var hd2 = Episode("Night Shift", 1, 2, path: @"D:\TV\Night Shift 1080p\S01E02.mkv");
        var sd1 = Episode("Night Shift", 1, 1, path: @"E:\Old\Night Shift\S01E01.mkv") with { Width = 720, Height = 480, SizeBytes = 300_000_000 };
        var sd2 = Episode("Night Shift", 1, 2, path: @"E:\Old\Night Shift\S01E02.mkv") with { Width = 720, Height = 480, SizeBytes = 300_000_000 };
        var catalog = Build(hd1, hd2, sd1, sd2);

        var next = catalog.Next(sd1.Path)!;
        Assert.Equal(hd2.Path, next.Item.Path);
        Assert.Equal(sd2.Path, next.PreferredCopy(sd1.Path).Path);
        Assert.Equal(hd2.Path, next.PreferredCopy(hd1.Path).Path);
    }

    [Fact]
    public void Navigation_is_the_same_after_a_rescan_changes_row_order_and_ids()
    {
        var items = Enumerable.Range(1, 8).Select(n => Episode("Night Shift", n <= 4 ? 1 : 2, ((n - 1) % 4) + 1, id: n)).ToList();
        var before = Build([.. items]);

        // A rescan: rows come back in another order, with new ids, and one file renamed.
        var renamed = items[2] with { Path = @"D:\TV\Night Shift\Season 1\03 - Lantern.mkv" };
        var rescanned = items.Select((item, i) => i == 2 ? renamed : item)
            .Select((item, i) => item with { Id = 100 - i })
            .Reverse()
            .ToArray();
        var after = Build(rescanned);

        Assert.Equal(before.All.Single().Episodes.Select(e => e.Label), after.All.Single().Episodes.Select(e => e.Label));
        Assert.Equal(renamed.Path, after.Next(items[1].Path)!.Item.Path);
        Assert.Equal(items[3].Path, after.Next(renamed.Path)!.Item.Path);
        Assert.Equal(items[4].Path, after.Next(items[3].Path)!.Item.Path);
    }

    [Fact]
    public void Films_and_missing_files_are_not_episodes()
    {
        var film = new LibraryItem { Path = @"D:\Films\Arrival.mkv", Title = "Arrival", Category = MediaCategory.Movie };
        var missing = Episode("Night Shift", 1, 2) with { IsMissing = true };
        var present = Episode("Night Shift", 1, 1);
        var catalog = Build(film, missing, present);

        Assert.Null(catalog.EpisodeOf(film.Path));
        Assert.Null(catalog.EpisodeOf(missing.Path));
        Assert.Null(catalog.Next(present.Path));
    }
}
