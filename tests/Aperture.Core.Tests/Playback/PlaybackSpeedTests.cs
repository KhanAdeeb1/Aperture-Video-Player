using Aperture.Core.Playback;

namespace Aperture.Core.Tests.Playback;

public class PlaybackSpeedTests
{
    [Theory]
    [InlineData(1.0, 1.25)]
    [InlineData(1.1, 1.25)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.1, 0.25)]
    public void Faster_steps_up_and_stops_at_the_top(double current, double expected) =>
        Assert.Equal(expected, PlaybackSpeed.Faster(current));

    [Theory]
    [InlineData(1.0, 0.75)]
    [InlineData(1.1, 1.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(3.0, 2.0)]
    public void Slower_steps_down_and_stops_at_the_bottom(double current, double expected) =>
        Assert.Equal(expected, PlaybackSpeed.Slower(current));

    [Theory]
    [InlineData(1.0, "Normal")]
    [InlineData(1.25, "1.25×")]
    [InlineData(0.5, "0.5×")]
    [InlineData(2.0, "2×")]
    public void Label_formats_speeds(double speed, string expected) =>
        Assert.Equal(expected, PlaybackSpeed.Label(speed));
}
