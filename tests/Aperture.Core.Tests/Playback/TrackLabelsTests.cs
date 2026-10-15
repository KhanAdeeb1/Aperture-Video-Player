using Aperture.Core.Playback;

namespace Aperture.Core.Tests.Playback;

public class TrackLabelsTests
{
    [Theory]
    [InlineData("eng", "English")]
    [InlineData("en", "English")]
    [InlineData("fre", "French")]
    [InlineData("fra", "French")]
    [InlineData("ger", "German")]
    [InlineData("jpn", "Japanese")]
    [InlineData("und", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("zzq", null)]
    public void LanguageName_reads_common_tags(string? tag, string? expected) =>
        Assert.Equal(expected, TrackLabels.LanguageName(tag));

    [Fact]
    public void Name_combines_language_and_title()
    {
        var track = new MediaTrack(2, TrackKind.Audio, Title: "Commentary", Language: "eng");
        Assert.Equal("English · Commentary", TrackLabels.Name(track));
    }

    [Fact]
    public void Name_drops_a_title_that_repeats_the_language()
    {
        var track = new MediaTrack(1, TrackKind.Subtitle, Title: "English", Language: "eng");
        Assert.Equal("English", TrackLabels.Name(track));
    }

    [Fact]
    public void Name_marks_forced_tracks() =>
        Assert.Equal("French (forced)", TrackLabels.Name(new MediaTrack(3, TrackKind.Subtitle, Language: "fre", IsForced: true)));

    [Fact]
    public void Name_falls_back_to_external_file_then_number()
    {
        var external = new MediaTrack(4, TrackKind.Subtitle, IsExternal: true, ExternalFileName: Path.Combine("subs", "Film.srt"));
        Assert.Equal("Film.srt", TrackLabels.Name(external));
        Assert.Equal("Track 5", TrackLabels.Name(new MediaTrack(5, TrackKind.Audio)));
    }

    [Fact]
    public void Detail_describes_codec_and_layout()
    {
        Assert.Equal("Dolby Digital · 5.1", TrackLabels.Detail(new MediaTrack(1, TrackKind.Audio, Codec: "ac3", Channels: 6)));
        Assert.Equal("SubRip · external", TrackLabels.Detail(new MediaTrack(2, TrackKind.Subtitle, Codec: "subrip", IsExternal: true)));
        Assert.Equal("H.264 · 1920×1080", TrackLabels.Detail(new MediaTrack(3, TrackKind.Video, Codec: "h264", Width: 1920, Height: 1080)));
        Assert.Equal("PCM", TrackLabels.Detail(new MediaTrack(4, TrackKind.Audio, Codec: "pcm_s16le")));
    }

    [Theory]
    [InlineData(1, "Mono")]
    [InlineData(2, "Stereo")]
    [InlineData(6, "5.1")]
    [InlineData(8, "7.1")]
    [InlineData(3, "3 channels")]
    public void ChannelLayout_names_layouts(int channels, string expected) =>
        Assert.Equal(expected, TrackLabels.ChannelLayout(channels));
}
