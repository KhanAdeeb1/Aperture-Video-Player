using Aperture.Core.Library;

namespace Aperture.Core.Tests.Library;

public class MediaNameParserTests
{
    private const string Root = @"D:\Media";

    private static ParsedName Parse(string relative, LibraryFolderKind kind = LibraryFolderKind.Mixed) =>
        MediaNameParser.Parse(Root + "\\" + relative, Root, kind);

    [Theory]
    [InlineData(@"Arrival.Light.2016.1080p.BluRay.x264-GRP.mkv", "Arrival Light", 2016)]
    [InlineData(@"The Long Harbour (2019).mp4", "The Long Harbour", 2019)]
    [InlineData(@"Paper Moons [2003] 720p.mkv", "Paper Moons", 2003)]
    [InlineData(@"Glass_Garden_1998_DVDRip.avi", "Glass Garden", 1998)]
    [InlineData(@"1917 (2019).mkv", "1917", 2019)]
    [InlineData(@"Night Ferry (2021)\movie.mkv", "Night Ferry", 2021)]
    [InlineData(@"Film.2010.mkv", "Film", 2010)]
    public void Films_with_years(string name, string title, int year)
    {
        var parsed = Parse(name);
        Assert.Equal(MediaCategory.Movie, parsed.Category);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(MatchConfidence.Certain, parsed.Confidence);
    }

    [Fact]
    public void A_film_without_a_year_in_a_movies_folder_is_likely_not_certain()
    {
        var parsed = Parse(@"Quiet Rooms.mkv", LibraryFolderKind.Movies);
        Assert.Equal(MediaCategory.Movie, parsed.Category);
        Assert.Equal("Quiet Rooms", parsed.Title);
        Assert.Null(parsed.Year);
        Assert.Equal(MatchConfidence.Likely, parsed.Confidence);
    }

    [Theory]
    [InlineData(@"Harbour.Watch.S02E05.The.Tide.720p.WEB-DL.mkv", "Harbour Watch", 2, 5, "The Tide")]
    [InlineData(@"Harbour Watch - s1e03 - Low Water.mp4", "Harbour Watch", 1, 3, "Low Water")]
    [InlineData(@"Harbour Watch 1x04.mkv", "Harbour Watch", 1, 4, null)]
    [InlineData(@"Harbour Watch\Season 3\05 - North Wall.mkv", "Harbour Watch", 3, 5, "North Wall")]
    [InlineData(@"Harbour Watch\Season 3\Harbour.Watch.S03E06.mkv", "Harbour Watch", 3, 6, null)]
    [InlineData(@"Harbour Watch (2018)\S01\E07.mkv", "Harbour Watch", 1, 7, null)]
    [InlineData(@"Harbour Watch Season 2 Episode 9.mp4", "Harbour Watch", 2, 9, null)]
    public void Episodes(string name, string show, int season, int episode, string? episodeTitle)
    {
        var parsed = Parse(name);
        Assert.Equal(MediaCategory.Episode, parsed.Category);
        Assert.Equal(show, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Equal(episodeTitle, parsed.EpisodeTitle);
        Assert.NotEqual(MatchConfidence.Uncertain, parsed.Confidence);
    }

    [Fact]
    public void A_bare_dash_number_is_an_uncertain_episode_unless_the_folder_says_shows()
    {
        var mixed = Parse(@"[Studio] Lantern Road - 05 [1080p].mkv", LibraryFolderKind.Shows);
        Assert.Equal(MediaCategory.Episode, mixed.Category);
        Assert.Equal("Lantern Road", mixed.Title);
        Assert.Equal(5, mixed.Episode);
        Assert.Equal(1, mixed.Season);
        Assert.Equal(MatchConfidence.Likely, mixed.Confidence);
    }

    [Theory]
    [InlineData(@"2024-07-14 Beach day.mp4", "Beach day", 2024, 7, 14)]
    [InlineData(@"VID_20230102_183011.mp4", "Video from 2 January 2023", 2023, 1, 2)]
    [InlineData(@"PXL_20240501_101500123.mp4", "Video from 1 May 2024", 2024, 5, 1)]
    public void Personal_videos_with_dates(string name, string title, int y, int m, int d)
    {
        var parsed = Parse(name);
        Assert.Equal(MediaCategory.Personal, parsed.Category);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(new DateOnly(y, m, d), parsed.Date);
    }

    [Fact]
    public void A_plain_name_is_an_uncertain_personal_video()
    {
        var parsed = Parse(@"garden tour.mp4");
        Assert.Equal(MediaCategory.Personal, parsed.Category);
        Assert.Equal("garden tour", parsed.Title);
        Assert.Equal(MatchConfidence.Uncertain, parsed.Confidence);
    }

    [Fact]
    public void Audio_files_are_podcasts_named_after_their_folder()
    {
        var parsed = Parse(@"Design Notes\Episode 12 - Quiet Interfaces.mp3");
        Assert.Equal(MediaCategory.Podcast, parsed.Category);
        Assert.Equal("Design Notes", parsed.Title);
        Assert.Equal(12, parsed.Episode);
        Assert.Equal("Quiet Interfaces", parsed.EpisodeTitle);
    }

    [Fact]
    public void Podcast_folder_kind_wins_over_names()
    {
        var parsed = Parse(@"Design Notes\2024-03-01 Colour.mp4", LibraryFolderKind.Podcasts);
        Assert.Equal(MediaCategory.Podcast, parsed.Category);
        Assert.Equal("Design Notes", parsed.Title);
        Assert.Equal("Colour", parsed.EpisodeTitle);
        Assert.Equal(new DateOnly(2024, 3, 1), parsed.Date);
    }

    [Fact]
    public void Folders_above_the_library_root_are_never_used()
    {
        var parsed = MediaNameParser.Parse(@"D:\Movies (2020)\clip.mp4", @"D:\Movies (2020)", LibraryFolderKind.Mixed);
        Assert.Equal("clip", parsed.Title);
        Assert.Null(parsed.Year);
    }

    [Fact]
    public void Forward_slash_paths_work_too()
    {
        var parsed = MediaNameParser.Parse("/media/tv/Harbour Watch/Season 1/02.mkv", "/media/tv", LibraryFolderKind.Shows);
        Assert.Equal(("Harbour Watch", 1, 2), (parsed.Title, parsed.Season!.Value, parsed.Episode!.Value));
    }

    [Theory]
    [InlineData(@"Night.Shift.S01E01E02.720p.mkv", 1, 1, 2)]
    [InlineData(@"Night.Shift.S01E01-E02.mkv", 1, 1, 2)]
    [InlineData(@"Night.Shift.S01E01-02.mkv", 1, 1, 2)]
    [InlineData(@"Night Shift - S02E09E10E11.mkv", 2, 9, 11)]
    public void Files_holding_several_episodes_record_the_last(string name, int season, int episode, int last)
    {
        var parsed = Parse(name);
        Assert.Equal(MediaCategory.Episode, parsed.Category);
        Assert.Equal((season, episode, last), (parsed.Season!.Value, parsed.Episode!.Value, parsed.EpisodeEnd!.Value));
    }

    [Theory]
    [InlineData(@"Night.Shift.S01E05.mkv")]
    [InlineData(@"Night.Shift.S01E05-1080p.mkv")]
    [InlineData(@"Night.Shift.S01E05-2019.mkv")]
    [InlineData(@"Night.Shift.S01E05-E03.mkv")]
    public void A_single_episode_has_no_last_episode(string name)
    {
        var parsed = Parse(name);
        Assert.Equal(5, parsed.Episode);
        Assert.Null(parsed.EpisodeEnd);
    }
}
