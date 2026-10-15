using System.Globalization;
using Aperture.Core.Playback;

namespace Aperture.Playback.Mpv.Tests;

/// <summary>
/// Plays every generated representative file through libmpv and checks what actually
/// happens. Each test also writes a row to APERTURE_TEST_RESULTS so CI can publish the
/// measured results per platform.
/// </summary>
[Collection("libmpv")]
public class RepresentativeFileTests
{
    [MediaTheory]
    [InlineData("h264-aac-timedtext.mp4", "h264", 1280, 720, "aac")]
    [InlineData("hevc10-ac3-51.mkv", "hevc", 1920, 1080, "ac3")]
    [InlineData("vp9-opus.webm", "vp9", 1280, 720, "opus")]
    [InlineData("av1-opus.mkv", "av1", 1280, 720, "opus")]
    [InlineData("mpeg2-anamorphic.ts", "mpeg2video", 720, 576, "mp2")]
    [InlineData("mpeg4-mp3.avi", "mpeg4", 640, 480, "mp3")]
    [InlineData("wmv-wma.wmv", "wmv2", 640, 360, "wmav2")]
    [InlineData("prores-pcm.mov", "prores", 1280, 720, "pcm_s16le")]
    [InlineData("ffv1-flac.mkv", "ffv1", 640, 360, "flac")]
    [InlineData("theora-vorbis.ogv", "theora", 640, 360, "vorbis")]
    [InlineData("portrait.mp4", "h264", 540, 960, "aac")]
    [InlineData("multi-track.mkv", "h264", 1280, 720, "aac")]
    public async Task Plays_seeks_and_pauses(string file, string videoCodec, int width, int height, string audioCodec)
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf(file), TimeSpan.Zero);
        var details = string.Empty;
        try
        {
            Assert.Equal(PlaybackStatus.Playing, engine.Status);
            Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("video-params/dw") is not null, 5), "no video parameters");
            details = TestMedia.Describe(engine);
            Assert.Equal(videoCodec, engine.ReadProperty("video-format"));
            Assert.Equal(width.ToString(CultureInfo.InvariantCulture), engine.ReadProperty("video-params/w"));
            Assert.Equal(height.ToString(CultureInfo.InvariantCulture), engine.ReadProperty("video-params/h"));
            Assert.Equal(audioCodec, engine.ReadProperty("audio-codec-name"));
            Assert.InRange(engine.Duration.TotalSeconds, 7.5, 8.5);

            Assert.True(await TestMedia.WaitAsync(() => engine.Position > TimeSpan.FromSeconds(0.3)), "playback did not advance");

            engine.Seek(TimeSpan.FromSeconds(5));
            Assert.True(await TestMedia.WaitAsync(() => Math.Abs(engine.Position.TotalSeconds - 5) < 0.6, 5), $"seek landed at {engine.Position.TotalSeconds:0.00}");

            engine.Pause();
            Assert.True(await TestMedia.WaitAsync(() => engine.Status == PlaybackStatus.Paused, 5));
            await Task.Delay(300); // the frame being decoded when pause arrived may still land
            var paused = engine.Position;
            await Task.Delay(500);
            Assert.InRange((engine.Position - paused).TotalSeconds, -0.05, 0.05);

            engine.Play();
            Assert.True(await TestMedia.WaitAsync(() => engine.Status == PlaybackStatus.Ended, 10), "did not reach the end");
            TestMedia.Record(file, "Plays", $"{details}; seek, pause and end OK");
        }
        catch
        {
            TestMedia.Record(file, "**FAILED**", $"{details}; status {engine.Status}, {engine.Failure?.Kind} {engine.Failure?.Detail}");
            throw;
        }
    }

    [MediaFact]
    public async Task Anamorphic_video_is_shown_at_its_display_shape()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("mpeg2-anamorphic.ts"), TimeSpan.Zero);

        // The file is loaded before its first frame is decoded; the display size comes with that frame.
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("video-params/dh") is not null), "no video parameters");
        var width = double.Parse(engine.ReadProperty("video-params/dw")!, CultureInfo.InvariantCulture);
        var height = double.Parse(engine.ReadProperty("video-params/dh")!, CultureInfo.InvariantCulture);
        Assert.InRange(width / height, 1.76, 1.79);
    }

    [MediaFact]
    public async Task Audio_only_file_plays_without_a_picture()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("audio-only.mp4"), TimeSpan.Zero);
        var details = TestMedia.Describe(engine);
        TestMedia.Record("audio-only.mp4", engine.Status.ToString(), details);
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
        Assert.DoesNotContain(engine.Tracks, t => t.Kind == TrackKind.Video);
    }

    [MediaFact]
    public async Task File_cut_in_half_plays_what_exists_then_ends()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("cut-in-half.mkv"), TimeSpan.Zero);
        var details = TestMedia.Describe(engine);
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
        engine.Rate = 4;
        var ended = await TestMedia.WaitAsync(() => engine.Status is PlaybackStatus.Ended or PlaybackStatus.Failed, 15);
        TestMedia.Record("cut-in-half.mkv", engine.Status.ToString(), $"{details}; stopped at {engine.Position.TotalSeconds:0.0}s");
        Assert.True(ended);
        Assert.Equal(PlaybackStatus.Ended, engine.Status);
    }

    [MediaTheory]
    [InlineData("damaged.mp4", PlaybackFailureKind.Damaged)]
    [InlineData("empty.mp4", PlaybackFailureKind.Damaged)]
    [InlineData("truncated-no-index.mp4", PlaybackFailureKind.Damaged)]
    [InlineData("subtitles-only.mkv", PlaybackFailureKind.Unsupported)]
    [InlineData("not-a-video.txt", PlaybackFailureKind.Unsupported)]
    [InlineData("missing.mp4", PlaybackFailureKind.NotFound)]
    public async Task Problem_files_fail_with_a_specific_reason(string file, PlaybackFailureKind expected)
    {
        using var engine = await TestMedia.CreateEngineAsync();
        var open = engine.OpenAsync(TestMedia.PathOf(file), TimeSpan.Zero);
        Assert.True(await Task.WhenAny(open, Task.Delay(15000)) == open, "opening never finished");
        TestMedia.Record(file, $"{engine.Status}: {engine.Failure?.Kind}", engine.Failure?.Detail ?? string.Empty);
        Assert.Equal(PlaybackStatus.Failed, engine.Status);
        Assert.Equal(expected, engine.Failure?.Kind);
    }

    [MediaFact]
    public async Task A_failed_file_does_not_stop_the_next_one()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("damaged.mp4"), TimeSpan.Zero);
        Assert.Equal(PlaybackStatus.Failed, engine.Status);
        await engine.OpenAsync(TestMedia.PathOf("vp9-opus.webm"), TimeSpan.Zero);
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
        Assert.Null(engine.Failure);
    }
}
