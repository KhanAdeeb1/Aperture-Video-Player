using Aperture.Core.Media;

namespace Aperture.Core.Tests.Media;

public class MediaFileTypesTests
{
    [Theory]
    [InlineData("movie.mp4", true)]
    [InlineData("MOVIE.MKV", true)]
    [InlineData("clip.webm", true)]
    [InlineData("notes.txt", false)]
    [InlineData("song.mp3", false)]
    [InlineData("no-extension", false)]
    public void Recognises_video_extensions(string path, bool expected) =>
        Assert.Equal(expected, MediaFileTypes.IsVideoFile(path));

    [Fact]
    public void Extensions_are_lower_case_with_a_dot() =>
        Assert.All(MediaFileTypes.VideoExtensions, e =>
        {
            Assert.StartsWith(".", e, StringComparison.Ordinal);
            Assert.Equal(e.ToLowerInvariant(), e);
        });

    [Fact]
    public void Title_drops_the_folder_and_extension() =>
        Assert.Equal("Harbour Lights", MediaFileTypes.TitleFromPath(Path.Combine("videos", "Harbour Lights.mp4")));

    [Theory]
    [InlineData("film.srt", true)]
    [InlineData("film.en.ASS", true)]
    [InlineData("film.vtt", true)]
    [InlineData("film.mkv", false)]
    [InlineData("film", false)]
    public void Subtitle_files_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, MediaFileTypes.IsSubtitleFile(path));
}
