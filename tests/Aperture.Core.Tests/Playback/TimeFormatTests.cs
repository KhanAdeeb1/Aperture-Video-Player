using Aperture.Core.Playback;

namespace Aperture.Core.Tests.Playback;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5.9, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(600, "10:00")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-4, "0:00")]
    public void Clock_formats_positions(double seconds, string expected) =>
        Assert.Equal(expected, TimeFormat.Clock(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(45, "45 sec left")]
    [InlineData(12 * 60, "12 min left")]
    [InlineData(65 * 60, "1 hr 5 min left")]
    [InlineData(120 * 60, "2 hr left")]
    public void Remaining_is_short_and_human(double seconds, string expected) =>
        Assert.Equal(expected, TimeFormat.Remaining(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(50, "50 sec")]
    [InlineData(42 * 60, "42 min")]
    [InlineData(118 * 60, "1 hr 58 min")]
    public void Length_is_compact(double seconds, string expected) =>
        Assert.Equal(expected, TimeFormat.Length(TimeSpan.FromSeconds(seconds)));
}
