using Aperture.Core.Media;

namespace Aperture.Core.Tests.Media;

public sealed class SubtitleFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aperture-subs-" + Guid.NewGuid().ToString("N"));

    public SubtitleFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Finds_named_variants_beside_a_film_with_their_languages()
    {
        var video = Touch("Film.2019.1080p.mkv");
        Touch("Film.2019.1080p.srt");
        Touch("Film.2019.1080p.English.forced.srt");
        Touch("Film.2019.eng.sdh.srt");
        Touch("Film.2019.1080p.nfo");
        var found = SubtitleFinder.Find(video);
        Assert.Equal(3, found.Count);
        Assert.Contains(found, m => m.Path.EndsWith("Film.2019.1080p.srt", StringComparison.Ordinal) && m.Language is null);
        Assert.Contains(found, m => m.Language == "eng" && m.IsForced && !m.IsHearingImpaired);
        Assert.Contains(found, m => m.Language == "eng" && m.IsHearingImpaired && m.Title == "SDH");
    }

    [Fact]
    public void Season_folders_match_by_episode()
    {
        var video = Touch("Show.S01E02.1080p.WEB.mkv");
        Touch("Show.S01E01.1080p.WEB.mkv");
        Touch("Show.S01E02.srt");
        Touch("Show.S01E01.srt");
        Touch("Show.srt");
        Touch("show 1x02 french.srt");
        var found = SubtitleFinder.Find(video).Select(m => Path.GetFileName(m.Path)).ToList();
        Assert.Equal(["show 1x02 french.srt", "Show.S01E02.srt"], found.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Release_Subs_folders_are_searched()
    {
        var video = Touch("Lighthouse.2019.1080p.WEB.mp4");
        Touch(Path.Combine("Subs", "Lighthouse.2019.1080p.WEB", "2_English.srt"));
        Touch(Path.Combine("Subs", "Lighthouse.2019.1080p.WEB", "13_Spanish.srt"));
        Touch(Path.Combine("Subs", "Other.Film", "2_English.srt"));
        Touch(Path.Combine("subtitles", "Lighthouse.2019.1080p.WEB.de.srt"));
        var found = SubtitleFinder.Find(video);
        Assert.Equal(["eng", "ger", "spa"], found.Select(m => m.Language!).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(found, m => m.Path.Contains("Other.Film", StringComparison.Ordinal));
    }

    [Fact]
    public void Anything_beside_the_only_video_belongs_to_it()
    {
        var video = Touch("Movie.mkv");
        Touch("English.srt");
        Touch("movie.idx");
        Touch("movie.sub");
        var found = SubtitleFinder.Find(video).Select(m => Path.GetFileName(m.Path)).ToList();
        Assert.Equal(["English.srt", "movie.idx"], found);
    }

    [Fact]
    public void Unrelated_subtitles_beside_several_videos_are_left_alone()
    {
        var video = Touch("Alpha.mkv");
        Touch("Beta.mkv");
        Touch("Beta.srt");
        Touch("English.srt");
        Assert.Empty(SubtitleFinder.Find(video));
    }

    [Fact]
    public void A_missing_folder_finds_nothing() =>
        Assert.Empty(SubtitleFinder.Find(Path.Combine(_root, "gone", "Film.mkv")));

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }
}
