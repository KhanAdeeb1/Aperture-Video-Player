using Aperture.Core.Casting;

namespace Aperture.Core.Tests.Casting;

public class DlnaTimeTests
{
    [Theory]
    [InlineData("0:00:00", 0)]
    [InlineData("0:01:02", 62)]
    [InlineData("1:02:03.500", 3723.5)]
    [InlineData("12:00:00", 43200)]
    [InlineData("+0:00:10", 10)]
    [InlineData("0:00:05.1/3", 5)]
    public void Reads_renderer_times(string text, double seconds) => Assert.Equal(seconds, DlnaTime.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NOT_IMPLEMENTED")]
    [InlineData("1:60:00")]
    [InlineData("0:00")]
    [InlineData("a:b:c")]
    [InlineData("-1:00:00")]
    public void Refuses_anything_else(string? text) => Assert.Null(DlnaTime.Parse(text));

    [Fact]
    public void Writes_whole_seconds()
    {
        Assert.Equal("0:00:00", DlnaTime.Format(-3));
        Assert.Equal("0:01:02", DlnaTime.Format(62.9));
        Assert.Equal("2:00:00", DlnaTime.Format(7200));
        Assert.Equal("1:02:03.000", DlnaTime.FormatDuration(3723));
    }
}
