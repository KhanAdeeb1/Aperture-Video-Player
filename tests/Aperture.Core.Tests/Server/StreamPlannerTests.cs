using Aperture.Core.Server;

namespace Aperture.Core.Tests.Server;

public class StreamPlannerTests
{
    // An iPhone: H.264 and HEVC, HLS, no WebM.
    private static readonly ClientCapabilities IPhone = ClientCapabilities.From(["h264", "hevc"], ["aac", "mp3", "ac3", "eac3", "alac", "flac"], hls: true, webm: false);

    // Chrome on Android: no HEVC, plays WebM and HLS.
    private static readonly ClientCapabilities Android = ClientCapabilities.From(["h264", "vp8", "vp9", "av1"], ["aac", "mp3", "opus", "vorbis", "flac"], hls: true, webm: true);

    // A desktop browser without HLS.
    private static readonly ClientCapabilities Firefox = ClientCapabilities.From(["h264", "vp9", "av1"], ["aac", "mp3", "opus", "flac"], hls: false, webm: true);

    // A browser without H.264 at all (Chromium builds without proprietary codecs).
    private static readonly ClientCapabilities OpenCodecsOnly = ClientCapabilities.From(["vp8", "vp9"], ["opus", "vorbis"], hls: false, webm: true);

    private static SourceMedia Video(string container, string video, string? audio = "aac", int height = 1080, int depth = 8, long? bps = 8_000_000) =>
        new(container, video, height * 16 / 9, height, depth, bps, audio, 2, TimeSpan.FromMinutes(90));

    [Fact]
    public void A_file_the_device_plays_is_sent_as_it_is()
    {
        var plan = StreamPlanner.Decide(Video("mp4", "h264"), IPhone, 50_000_000, canConvert: true);

        Assert.Equal(StreamMode.DirectPlay, plan.Mode);
        Assert.Contains("H.264 and AAC in MP4", plan.Reason, StringComparison.Ordinal);
        Assert.Null(plan.Warning);
    }

    [Fact]
    public void An_h264_mkv_is_repackaged_without_reencoding()
    {
        var plan = StreamPlanner.Decide(Video("matroska", "h264"), IPhone, 50_000_000, canConvert: true);

        Assert.Equal(StreamMode.Remux, plan.Mode);
        Assert.True(plan.CopyVideo);
        Assert.True(plan.CopyAudio);
        Assert.False(plan.Progressive);
        Assert.Contains("MKV", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Remuxing_converts_sound_the_stream_cannot_carry()
    {
        var plan = StreamPlanner.Decide(Video("matroska", "h264", audio: "dts"), IPhone, null, canConvert: true);

        Assert.Equal(StreamMode.Remux, plan.Mode);
        Assert.True(plan.CopyVideo);
        Assert.False(plan.CopyAudio);
        Assert.Contains("DTS sound is converted", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Video_the_device_cannot_decode_is_converted_and_never_upscaled()
    {
        var plan = StreamPlanner.Decide(Video("matroska", "hevc", height: 2160, depth: 10, bps: 40_000_000), Android, 200_000_000, canConvert: true);

        Assert.Equal(StreamMode.Convert, plan.Mode);
        Assert.Equal(1080, plan.TargetHeight);
        Assert.Contains("can't play HEVC", plan.Reason, StringComparison.Ordinal);

        var small = StreamPlanner.Decide(Video("avi", "mpeg4", height: 480, bps: 1_500_000), Android, null, canConvert: true);
        Assert.Equal(480, small.TargetHeight);
    }

    [Fact]
    public void Ten_bit_h264_is_converted()
    {
        var plan = StreamPlanner.Decide(Video("mp4", "h264", depth: 10), IPhone, null, canConvert: true);

        Assert.Equal(StreamMode.Convert, plan.Mode);
        Assert.Contains("10-bit", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slow_connection_gets_a_smaller_stream()
    {
        var plan = StreamPlanner.Decide(Video("mp4", "h264", bps: 20_000_000), IPhone, 5_000_000, canConvert: true);

        Assert.Equal(StreamMode.Convert, plan.Mode);
        Assert.Equal(480, plan.TargetHeight);
        Assert.Equal(1800, plan.VideoBitrateKbps);
        Assert.Contains("your connection measured 5 Mbps", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_ffmpeg_a_playable_file_still_plays_with_a_warning()
    {
        var plan = StreamPlanner.Decide(Video("mp4", "h264", bps: 20_000_000), IPhone, 5_000_000, canConvert: false);

        Assert.Equal(StreamMode.DirectPlay, plan.Mode);
        Assert.Contains("may pause to load", plan.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_ffmpeg_an_unplayable_file_says_why()
    {
        var plan = StreamPlanner.Decide(Video("matroska", "hevc"), Android, null, canConvert: false);

        Assert.Equal(StreamMode.Unavailable, plan.Mode);
        Assert.Contains("HEVC", plan.Reason, StringComparison.Ordinal);
        Assert.Contains("FFmpeg", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_without_h264_is_not_offered_a_conversion_it_cannot_play()
    {
        var plan = StreamPlanner.Decide(Video("matroska", "ffv1"), OpenCodecsOnly, null, canConvert: true);

        Assert.Equal(StreamMode.Unavailable, plan.Mode);
        Assert.Contains("H.264", plan.Reason, StringComparison.Ordinal);

        var h264 = StreamPlanner.Decide(Video("matroska", "h264"), OpenCodecsOnly, null, canConvert: true);
        Assert.Equal("This device can't play H.264 video, which is also what Aperture converts to.", h264.Reason);
    }

    [Fact]
    public void WebM_plays_directly_where_it_is_supported()
    {
        var webm = Video("webm", "vp9", audio: "opus");

        Assert.Equal(StreamMode.DirectPlay, StreamPlanner.Decide(webm, OpenCodecsOnly, null, canConvert: false).Mode);
        Assert.Equal(StreamMode.Convert, StreamPlanner.Decide(webm, IPhone, null, canConvert: true).Mode);
    }

    [Fact]
    public void Browsers_without_hls_get_a_continuous_stream()
    {
        var plan = StreamPlanner.Decide(Video("matroska", "h264"), Firefox, null, canConvert: true);

        Assert.Equal(StreamMode.Remux, plan.Mode);
        Assert.True(plan.Progressive);
    }

    [Fact]
    public void Sound_alone_is_converted_to_aac_when_needed()
    {
        var flac = new SourceMedia("flac", null, null, null, null, 900_000, "flac", 2, TimeSpan.FromMinutes(40));
        var noFlac = ClientCapabilities.From(["h264"], ["aac", "mp3"], hls: true, webm: false);

        Assert.Equal(StreamMode.DirectPlay, StreamPlanner.Decide(flac, IPhone, null, canConvert: true).Mode);
        var plan = StreamPlanner.Decide(flac, noFlac, null, canConvert: true);
        Assert.Equal(StreamMode.Convert, plan.Mode);
        Assert.True(plan.AudioOnly);
    }

    [Fact]
    public void A_device_that_failed_on_the_original_gets_a_conversion()
    {
        var plan = StreamPlanner.Decide(Video("mp4", "h264"), IPhone, null, canConvert: true, avoidDirectPlay: true);

        Assert.Equal(StreamMode.Convert, plan.Mode);
        Assert.Contains("couldn't play the original", plan.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2160, null, 1080, 8000)]
    [InlineData(1080, 12_000_000L, 1080, 8000)]
    [InlineData(1080, 6_000_000L, 720, 4000)]
    [InlineData(1080, 500_000L, 360, 900)]
    [InlineData(576, null, 480, 1800)]
    [InlineData(241, null, 240, 600)]
    public void Chooses_the_best_rung_that_fits(int height, long? bandwidth, int expectedHeight, int expectedKbps) =>
        Assert.Equal((expectedHeight, expectedKbps), StreamPlanner.ChooseRung(height, bandwidth));

    [Fact]
    public void Capabilities_keep_only_known_codec_names()
    {
        var client = ClientCapabilities.From(["H264", "<script>", "vp9"], ["AAC", "nonsense"], hls: true, webm: false);

        Assert.Equal(["h264", "vp9"], client.VideoCodecs.Order());
        Assert.Equal(["aac"], client.AudioCodecs);
    }

    [Fact]
    public void Describes_a_source_for_diagnostics()
    {
        Assert.Equal("H.264 1920×1080, AAC stereo, MP4, 8 Mbps", Video("mp4", "h264").Describe());
        Assert.Equal("900 kbps", StreamPlanner.FormatRate(900_000));
        Assert.Equal("5.1", StreamPlanner.ChannelName(6));
    }
}
