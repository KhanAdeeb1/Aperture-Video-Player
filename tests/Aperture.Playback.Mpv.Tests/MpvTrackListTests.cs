using Aperture.Core.Playback;

namespace Aperture.Playback.Mpv.Tests;

public class MpvTrackListTests
{
    // Shape of libmpv's track-list property as JSON (trimmed from a real libmpv 0.37 reply).
    private const string Sample = """
        [
          {"id":1,"type":"video","src-id":0,"image":false,"albumart":false,"default":true,"forced":false,"external":false,"selected":true,"main-selection":0,"ff-index":0,"codec":"h264","demux-w":1280,"demux-h":720,"demux-fps":24.000000},
          {"id":1,"type":"audio","src-id":1,"title":"Original","lang":"eng","default":true,"forced":false,"external":false,"selected":true,"codec":"aac","demux-channel-count":2,"demux-samplerate":48000},
          {"id":2,"type":"audio","src-id":2,"title":"Commentary","lang":"jpn","default":false,"external":false,"selected":false,"codec":"ac3","demux-channel-count":6,"demux-samplerate":48000},
          {"id":1,"type":"sub","src-id":3,"lang":"eng","default":false,"forced":true,"external":false,"selected":false,"codec":"subrip"},
          {"id":2,"type":"sub","title":"extra-subtitles.ass","default":false,"external":true,"external-filename":"/media/extra-subtitles.ass","selected":true,"codec":"ass"},
          {"id":9,"type":"attachment"}
        ]
        """;

    [Fact]
    public void Reads_every_track_of_known_type()
    {
        var tracks = MpvTrackList.Parse(Sample);
        Assert.Equal(5, tracks.Count);
        Assert.Equal(1, tracks.Count(t => t.Kind == TrackKind.Video));
        Assert.Equal(2, tracks.Count(t => t.Kind == TrackKind.Audio));
        Assert.Equal(2, tracks.Count(t => t.Kind == TrackKind.Subtitle));
    }

    [Fact]
    public void Reads_track_details()
    {
        var tracks = MpvTrackList.Parse(Sample);
        var video = tracks.Single(t => t.Kind == TrackKind.Video);
        Assert.Equal((1280, 720), (video.Width, video.Height));
        Assert.Equal(24.0, video.FramesPerSecond);
        Assert.True(video.IsSelected);

        var commentary = tracks.Single(t => t.Kind == TrackKind.Audio && t.Id == 2);
        Assert.Equal("jpn", commentary.Language);
        Assert.Equal("Commentary", commentary.Title);
        Assert.Equal(6, commentary.Channels);
        Assert.False(commentary.IsSelected);

        var external = tracks.Single(t => t.Kind == TrackKind.Subtitle && t.Id == 2);
        Assert.True(external.IsExternal);
        Assert.Equal("/media/extra-subtitles.ass", external.ExternalFileName);
        Assert.True(tracks.Single(t => t.Kind == TrackKind.Subtitle && t.Id == 1).IsForced);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"id\":1}")]
    public void Bad_input_gives_no_tracks(string? json) => Assert.Empty(MpvTrackList.Parse(json));
}
