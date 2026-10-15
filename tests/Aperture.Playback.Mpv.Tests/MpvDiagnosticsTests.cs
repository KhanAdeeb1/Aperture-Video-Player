using Aperture.Core.Playback;

namespace Aperture.Playback.Mpv.Tests;

public class MpvDiagnosticsTests
{
    private static readonly Dictionary<string, string> Playing = new()
    {
        ["path"] = "/media/film.mkv",
        ["filename"] = "film.mkv",
        ["file-format"] = "mkv",
        ["file-size"] = "3670719",
        ["duration"] = "8.000000",
        ["current-tracks/video/image"] = "no",
        ["video-format"] = "hevc",
        ["video-codec"] = "HEVC (High Efficiency Video Coding)",
        ["video-params/w"] = "1920",
        ["video-params/h"] = "1080",
        ["video-params/pixelformat"] = "yuv420p10",
        ["video-params/colormatrix"] = "bt.709",
        ["container-fps"] = "24.000000",
        ["estimated-vf-fps"] = "22.5",
        ["hwdec-current"] = "no",
        ["video-bitrate"] = "2450000",
        ["frame-drop-count"] = "1",
        ["decoder-frame-drop-count"] = "2",
        ["audio-codec-name"] = "ac3",
        ["audio-params/channel-count"] = "6",
        ["audio-params/hr-channels"] = "5.1",
        ["audio-params/samplerate"] = "48000",
        ["speed"] = "1.250000",
        ["avsync"] = "-0.004",
    };

    private static PlaybackDiagnostics Build(Dictionary<string, string> values, string? renderer = null) =>
        MpvDiagnostics.Build(name => values.GetValueOrDefault(name), renderer);

    private static string Value(PlaybackDiagnostics diagnostics, string section, string label) =>
        diagnostics.Sections.Single(s => s.Title == section).Items.Single(i => i.Label == label).Value;

    [Fact]
    public void Nothing_open_gives_nothing() => Assert.Empty(Build([]).Sections);

    [Fact]
    public void Formats_video_values()
    {
        var diagnostics = Build(Playing);
        Assert.Equal("HEVC", Value(diagnostics, "Video", "Codec"));
        Assert.Equal("1920×1080", Value(diagnostics, "Video", "Resolution"));
        Assert.Equal("24 fps (measured 22.5)", Value(diagnostics, "Video", "Frame rate"));
        Assert.Equal("Software", Value(diagnostics, "Video", "Decoding"));
        Assert.Equal("2.5 Mbit/s", Value(diagnostics, "Video", "Bitrate"));
        Assert.Equal("3", Value(diagnostics, "Video", "Dropped frames"));
    }

    [Fact]
    public void Formats_audio_file_and_playback_values()
    {
        var diagnostics = Build(Playing, "Software");
        Assert.Equal("Dolby Digital", Value(diagnostics, "Audio", "Codec"));
        Assert.Equal("5.1", Value(diagnostics, "Audio", "Channels"));
        Assert.Equal("48 kHz", Value(diagnostics, "Audio", "Sample rate"));
        Assert.Equal("3.5 MB", Value(diagnostics, "File", "Size"));
        Assert.Equal("0:08", Value(diagnostics, "File", "Length"));
        Assert.Equal("1.25×", Value(diagnostics, "Playback", "Speed"));
        Assert.Equal("-4 ms", Value(diagnostics, "Playback", "A/V sync"));
        Assert.Equal("Software", Value(diagnostics, "Playback", "Renderer"));
    }

    [Fact]
    public void Missing_values_are_left_out_not_blank()
    {
        var diagnostics = Build(new Dictionary<string, string> { ["path"] = "/a.mp4", ["audio-codec-name"] = "aac" });
        Assert.DoesNotContain(diagnostics.Sections, s => s.Title == "Video");
        Assert.All(diagnostics.Sections.SelectMany(s => s.Items), i => Assert.False(string.IsNullOrWhiteSpace(i.Value)));
    }

    [Fact]
    public void Hardware_decoding_names_the_api()
    {
        var values = new Dictionary<string, string>(Playing) { ["hwdec-current"] = "d3d11va-copy" };
        Assert.Equal("Hardware (d3d11va-copy)", Value(Build(values), "Video", "Decoding"));
    }
}
