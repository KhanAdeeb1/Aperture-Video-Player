using Aperture.Core.Casting;
using Aperture.Core.Server;

namespace Aperture.Core.Tests.Casting;

public class TvStreamPlannerTests
{
    // A typical DLNA TV: MP4, MKV and MPEG-TS, H.264 profiles only.
    private static readonly RendererFormats Tv = RendererFormats.Parse(
        "http-get:*:video/mp4:DLNA.ORG_PN=AVC_MP4_HP_HD_AAC,http-get:*:video/x-matroska:*,http-get:*:video/mpeg:DLNA.ORG_PN=MPEG_TS_HD_NA,http-get:*:audio/mpeg:*");

    // An older TV: MPEG-TS only.
    private static readonly RendererFormats Old = RendererFormats.Parse("http-get:*:video/vnd.dlna.mpeg-tts:*,http-get:*:audio/mpeg:*");

    // A TV that names HEVC in its profiles.
    private static readonly RendererFormats Hevc = RendererFormats.Parse("http-get:*:video/x-matroska:DLNA.ORG_PN=HEVC_MKV_MAIN,http-get:*:video/mp4:*");

    private static SourceMedia Video(string container, string video, string? audio = "aac", int height = 1080, int depth = 8) =>
        new(container, video, height * 16 / 9, height, depth, 8_000_000, audio, 2, TimeSpan.FromMinutes(90));

    [Fact]
    public void An_h264_mp4_the_TV_accepts_is_sent_as_it_is()
    {
        var plan = TvStreamPlanner.Decide(Video("mp4", "h264"), ".mp4", Tv, canConvert: true);

        Assert.True(plan.IsDirect);
        Assert.Equal("video/mp4", plan.Mime);
        Assert.Contains("original file", plan.Plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Dolby_sound_plays_directly_but_DTS_is_converted_while_the_video_is_copied()
    {
        Assert.True(TvStreamPlanner.Decide(Video("matroska", "h264", "ac3"), ".mkv", Tv, canConvert: true).IsDirect);

        var plan = TvStreamPlanner.Decide(Video("matroska", "h264", "dts"), ".mkv", Tv, canConvert: true);
        Assert.Equal(StreamMode.Remux, plan.Plan.Mode);
        Assert.True(plan.Plan.CopyVideo);
        Assert.False(plan.Plan.CopyAudio);
        Assert.True(plan.Plan.TransportStream);
        Assert.Equal("video/mpeg", plan.Mime);
        Assert.Contains("DTS", plan.Plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_the_TV_doesnt_list_is_repackaged_as_MPEG_TS_in_the_TVs_own_spelling()
    {
        var plan = TvStreamPlanner.Decide(Video("matroska", "h264"), ".mkv", Old, canConvert: true);

        Assert.Equal(StreamMode.Remux, plan.Plan.Mode);
        Assert.True(plan.Plan.CopyAudio);
        Assert.Equal("video/vnd.dlna.mpeg-tts", plan.Mime);
        Assert.Contains("doesn't accept MKV", plan.Plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void HEVC_is_trusted_only_when_the_TV_names_it()
    {
        var unnamed = TvStreamPlanner.Decide(Video("matroska", "hevc"), ".mkv", Tv, canConvert: true);
        Assert.Equal(StreamMode.Convert, unnamed.Plan.Mode);
        Assert.Equal(1080, unnamed.Plan.TargetHeight);
        Assert.Contains("HEVC", unnamed.Plan.Reason, StringComparison.Ordinal);

        Assert.True(TvStreamPlanner.Decide(Video("matroska", "hevc"), ".mkv", Hevc, canConvert: true).IsDirect);
    }

    [Fact]
    public void Ten_bit_h264_is_converted()
    {
        var plan = TvStreamPlanner.Decide(Video("mp4", "h264", depth: 10), ".mp4", Tv, canConvert: true);

        Assert.Equal(StreamMode.Convert, plan.Plan.Mode);
        Assert.Contains("10-bit", plan.Plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Conversion_never_upscales()
    {
        var plan = TvStreamPlanner.Decide(Video("matroska", "vp9", "opus", height: 480), ".mkv", Tv, canConvert: true);

        Assert.Equal(StreamMode.Convert, plan.Plan.Mode);
        Assert.Equal(480, plan.Plan.TargetHeight);
    }

    [Fact]
    public void After_the_TV_failed_the_original_the_file_is_converted()
    {
        var plan = TvStreamPlanner.Decide(Video("mp4", "h264"), ".mp4", Tv, canConvert: true, avoidDirect: true);

        Assert.Equal(StreamMode.Convert, plan.Plan.Mode);
        Assert.Contains("couldn't play the original", plan.Plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_FFmpeg_the_extension_decides_and_anything_else_says_what_is_needed()
    {
        var mp4 = TvStreamPlanner.Decide(null, ".mp4", Tv, canConvert: false);
        Assert.True(mp4.IsDirect);
        Assert.Contains("Without FFmpeg", mp4.Plan.Reason, StringComparison.Ordinal);

        var avi = TvStreamPlanner.Decide(null, ".avi", Tv, canConvert: false);
        Assert.Equal(StreamMode.Unavailable, avi.Plan.Mode);
        Assert.Contains("needs FFmpeg", avi.Plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_TV_that_accepts_anything_gets_the_original_and_a_failure_falls_back()
    {
        var any = RendererFormats.Parse("http-get:*:*:*");
        Assert.True(any.AcceptsAll);
        Assert.Equal("video/x-matroska", TvStreamPlanner.Decide(Video("matroska", "h264"), ".mkv", any, canConvert: true).Mime);
        Assert.True(TvStreamPlanner.Decide(Video("mp4", "h264"), ".mp4", RendererFormats.Unknown, canConvert: true).IsDirect);
    }

    [Fact]
    public void Sound_files_play_directly_or_not_at_all()
    {
        var mp3 = new SourceMedia("mp3", null, null, null, null, 320_000, "mp3", 2, TimeSpan.FromMinutes(40));
        Assert.Equal("audio/mpeg", TvStreamPlanner.Decide(mp3, ".mp3", Tv, canConvert: true).Mime);

        var flac = new SourceMedia("flac", null, null, null, null, 900_000, "flac", 2, TimeSpan.FromMinutes(4));
        var plan = TvStreamPlanner.Decide(flac, ".flac", Tv, canConvert: true);
        Assert.Equal(StreamMode.Unavailable, plan.Plan.Mode);
    }

    [Fact]
    public void Protocol_lists_are_read_leniently()
    {
        var formats = RendererFormats.Parse(" http-get:*:VIDEO/MP4:* , rtsp-rtp-udp:*:video/h264:*, junk ,http-get:*:audio/L16;rate=44100;channels=2:* ");

        Assert.False(formats.AcceptsAll);
        Assert.Contains("video/mp4", formats.Mimes);
        Assert.DoesNotContain("video/h264", formats.Mimes);
        Assert.True(RendererFormats.Parse(null).AcceptsAll);
        Assert.True(RendererFormats.Parse(string.Empty).AcceptsAll);
    }
}
