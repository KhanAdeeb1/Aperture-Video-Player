using Aperture.Core.Playback;

namespace Aperture.Core.Tests.Playback;

public class SyncOffsetTests
{
    [Fact]
    public void Nudge_moves_in_tenth_seconds_and_stops_at_the_limit()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), SyncOffset.Nudge(TimeSpan.Zero, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(-300), SyncOffset.Nudge(TimeSpan.Zero, -3));
        Assert.Equal(TimeSpan.FromMilliseconds(200), SyncOffset.Nudge(TimeSpan.FromMilliseconds(130), 1));
        Assert.Equal(SyncOffset.Limit, SyncOffset.Nudge(SyncOffset.Limit, 1));
        Assert.Equal(-SyncOffset.Limit, SyncOffset.Nudge(-SyncOffset.Limit, -5));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(300, "+0.3 s")]
    [InlineData(-1200, "−1.2 s")]
    [InlineData(1250, "+1.25 s")]
    public void Labels(int milliseconds, string expected) =>
        Assert.Equal(expected, SyncOffset.Label(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void Describe_says_which_way()
    {
        Assert.Equal("Subtitles 0.3 s later", SyncOffset.Describe("Subtitles", TimeSpan.FromMilliseconds(300)));
        Assert.Equal("Sound 1.2 s earlier", SyncOffset.Describe("Sound", TimeSpan.FromMilliseconds(-1200)));
        Assert.Equal("Subtitles in sync", SyncOffset.Describe("Subtitles", TimeSpan.Zero));
    }

    [Fact]
    public void Normalize_clamps_stored_values() =>
        Assert.Equal(SyncOffset.Limit, SyncOffset.Normalize(TimeSpan.FromHours(1)));

    [Fact]
    public void Subtitle_style_scales_and_colours()
    {
        Assert.Equal(1.0, SubtitleStyle.Default.Scale);
        Assert.Equal(1.5, (SubtitleStyle.Default with { Size = SubtitleSize.ExtraLarge }).Scale);
        Assert.Equal("#F5DC5A", (SubtitleStyle.Default with { Color = SubtitleColor.Yellow }).ColorHex);
    }
}
