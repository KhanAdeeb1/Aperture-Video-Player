using Aperture.Core.Library;

namespace Aperture.Core.Tests.Library;

public class LibraryFolderRulesTests
{
    private static readonly string[] System = [@"C:\Windows", @"C:\Program Files", @"C:\Users\adi\AppData"];

    private static string? Check(string folder, params string[] existing) =>
        LibraryFolderRules.Check(folder, System, @"C:\Users\adi", existing, @"C:\Users\adi\AppData\Local\Aperture");

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:")]
    [InlineData(@"\\nas\media")]
    [InlineData("/")]
    public void Whole_drives_are_refused(string folder) => Assert.Contains("whole drives", Check(folder), StringComparison.Ordinal);

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files\Something")]
    [InlineData(@"C:\Users\adi\AppData\Local\Videos")]
    public void System_folders_are_refused(string folder) => Assert.Contains("Windows", Check(folder), StringComparison.Ordinal);

    [Fact]
    public void A_folder_containing_a_system_folder_is_refused() =>
        Assert.NotNull(LibraryFolderRules.Check(@"C:\Users", System, @"C:\Users\adi", [], null));

    [Fact]
    public void The_whole_profile_is_refused_but_folders_in_it_are_fine()
    {
        Assert.Contains("user folder", Check(@"C:\Users\adi\"), StringComparison.Ordinal);
        Assert.Null(Check(@"C:\Users\adi\Videos"));
        Assert.Null(Check(@"\\nas\media\Films"));
        Assert.Null(Check(@"D:\Films"));
    }

    [Fact]
    public void Duplicates_and_nested_folders_are_refused()
    {
        Assert.Contains("already in your library", Check(@"d:\films", @"D:\Films"), StringComparison.Ordinal);
        Assert.Contains("inside another", Check(@"D:\Films\Noir", @"D:\Films"), StringComparison.Ordinal);
        Assert.Null(Check(@"D:\Films 2", @"D:\Films"));
    }

    [Fact]
    public void IsInside_respects_folder_boundaries()
    {
        Assert.True(LibraryFolderRules.IsInside(@"D:\Films\a.mkv", @"D:\Films"));
        Assert.False(LibraryFolderRules.IsInside(@"D:\Films2\a.mkv", @"D:\Films"));
        Assert.False(LibraryFolderRules.IsInside(@"D:\Films", @"D:\Films"));
    }
}

public class CopyGroupTests
{
    private static LibraryItem Item(string path, string title, int? year, int? height, long size, string? fingerprint = null, MediaCategory category = MediaCategory.Movie) => new()
    {
        Path = path,
        Title = title,
        Year = year,
        Category = category,
        Width = height is null ? null : height * 16 / 9,
        Height = height,
        SizeBytes = size,
        Duration = TimeSpan.FromMinutes(100),
        Fingerprint = fingerprint,
        Confidence = year is null ? MatchConfidence.Uncertain : MatchConfidence.Certain,
    };

    [Fact]
    public void Copies_of_a_film_group_and_the_best_quality_leads()
    {
        var groups = CopyGroup.Build(
        [
            Item(@"D:\a\Arrival Light 720p.mkv", "Arrival Light", 2016, 720, 2_000_000_000),
            Item(@"D:\b\Arrival.Light.2016.2160p.mkv", "Arrival  light", 2016, 2160, 20_000_000_000),
            Item(@"D:\c\Arrival Light 1080p.mkv", "Arrival Light", 2016, 1080, 8_000_000_000),
            Item(@"D:\d\Other Film.mkv", "Other Film", 2016, 1080, 1),
        ]);

        Assert.Equal(2, groups.Count);
        var arrival = groups.Single(g => g.HasCopies);
        Assert.Equal("4K", arrival.Best.Quality);
        Assert.Equal(["1080p", "720p"], arrival.Others.Select(o => o.Quality));
    }

    [Fact]
    public void Same_title_different_year_is_a_different_film() =>
        Assert.Equal(2, CopyGroup.Build([Item("a.mkv", "Solaris", 1972, 1080, 1), Item("b.mkv", "Solaris", 2002, 1080, 1)]).Count);

    [Fact]
    public void Identical_contents_group_even_under_different_names()
    {
        var groups = CopyGroup.Build(
        [
            Item("x/holiday.mp4", "holiday", null, 1080, 500, "abc", MediaCategory.Personal),
            Item("y/copy of holiday.mp4", "copy of holiday", null, 1080, 500, "abc", MediaCategory.Personal),
            Item("z/other.mp4", "other", null, 1080, 500, "def", MediaCategory.Personal),
        ]);
        Assert.Equal(2, groups.Count);
        var pair = groups.Single(g => g.HasCopies);
        Assert.True(pair.IsIdenticalToBest(pair.Others[0]));
        Assert.StartsWith("Identical copy", pair.Describe(pair.Others[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Uncertain_films_without_years_do_not_group_by_title() =>
        Assert.Equal(2, CopyGroup.Build([Item("a.mkv", "Clip", null, 1080, 1), Item("b.mkv", "Clip", null, 720, 2)]).Count);

    [Theory]
    [InlineData(3840, 2160, "4K")]
    [InlineData(1920, 800, "1080p")]
    [InlineData(1280, 536, "720p")]
    [InlineData(720, 576, "SD")]
    [InlineData(1080, 1920, "1080p")]
    public void Quality_labels(int w, int h, string expected) => Assert.Equal(expected, MediaQuality.Label(w, h));
}

public class LibraryQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static LibraryItem Item(string path, string title, MediaCategory category, int addedDaysAgo, int? year = null) => new()
    {
        Path = path,
        Title = title,
        Category = category,
        Year = year,
        AddedUtc = Now.AddDays(-addedDaysAgo),
        Confidence = MatchConfidence.Certain,
    };

    private static readonly LibraryItem[] Items =
    [
        Item(@"D:\m\The Long Harbour.mkv", "The Long Harbour", MediaCategory.Movie, 3, 2019),
        Item(@"D:\m\Arrival Light.mkv", "Arrival Light", MediaCategory.Movie, 1, 2016),
        Item(@"D:\t\hw1.mkv", "Harbour Watch", MediaCategory.Episode, 2) with { Season = 1, Episode = 1, EpisodeTitle = "Pilot" },
        Item(@"D:\p\beach.mp4", "Beach day", MediaCategory.Personal, 5) with { IsMissing = true },
    ];

    private static IReadOnlyList<LibraryEntry> Run(MediaCategory? category = null, WatchFilter filter = WatchFilter.All, LibrarySort sort = LibrarySort.RecentlyAdded, string? search = null, Dictionary<string, HistoryEntry>? history = null, Dictionary<string, bool>? marks = null) =>
        LibraryQuery.Run(CopyGroup.Build(Items), history ?? new(StringComparer.OrdinalIgnoreCase), marks ?? new(StringComparer.OrdinalIgnoreCase), category, filter, sort, search);

    [Fact]
    public void Missing_files_are_hidden_and_newest_come_first() =>
        Assert.Equal(["Arrival Light", "Harbour Watch", "The Long Harbour"], Run().Select(e => e.Item.Title));

    [Fact]
    public void Title_sort_ignores_leading_articles() =>
        Assert.Equal(["Arrival Light", "Harbour Watch", "The Long Harbour"], Run(sort: LibrarySort.Title).Select(e => e.Item.Title));

    [Fact]
    public void Category_and_search_filter()
    {
        Assert.Equal(["Arrival Light", "The Long Harbour"], Run(MediaCategory.Movie).Select(e => e.Item.Title));
        Assert.Equal(["Harbour Watch", "The Long Harbour"], Run(search: "harbour").Select(e => e.Item.Title));
        Assert.Equal(["Harbour Watch"], Run(search: "pilot").Select(e => e.Item.Title));
        Assert.Equal(["The Long Harbour"], Run(search: "2019").Select(e => e.Item.Title));
    }

    [Fact]
    public void Watch_states_combine_history_and_marks()
    {
        var history = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"d:\m\arrival light.mkv"] = new(@"D:\m\Arrival Light.mkv", "Arrival Light", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(100), Now),
            [@"D:\t\hw1.mkv"] = new(@"D:\t\hw1.mkv", "hw1", TimeSpan.FromMinutes(44), TimeSpan.FromMinutes(45), Now),
        };
        var marks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [@"D:\m\The Long Harbour.mkv"] = true };

        Assert.Equal(["Arrival Light"], Run(filter: WatchFilter.InProgress, history: history, marks: marks).Select(e => e.Item.Title));
        Assert.Equal(["Harbour Watch", "The Long Harbour"], Run(filter: WatchFilter.Watched, history: history, marks: marks).Select(e => e.Item.Title));
        Assert.Equal(["Arrival Light"], Run(filter: WatchFilter.Unwatched, history: history, marks: marks).Select(e => e.Item.Title));
    }

    [Fact]
    public void Marking_unwatched_overrides_a_finished_history()
    {
        var finished = new HistoryEntry("a", "a", TimeSpan.FromMinutes(99), TimeSpan.FromMinutes(100), Now);
        Assert.Equal(WatchState.Watched, WatchStatus.Of(finished, null));
        Assert.Equal(WatchState.Unwatched, WatchStatus.Of(finished, false));
        Assert.Equal(WatchState.Watched, WatchStatus.Of(null, true));
    }

    [Fact]
    public void Episode_subtitles_read_naturally()
    {
        Assert.Equal("S1 E1 · Pilot", Items[2].Subtitle);
        Assert.Equal("2019", Items[0].Subtitle);
    }
}
