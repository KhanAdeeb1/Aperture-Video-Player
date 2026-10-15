using System.Globalization;
using Aperture.Core.Library;

namespace Aperture.Core.Tests.Library;

public class RelativeDateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 20, 0, 0, TimeSpan.FromHours(1)); // a Thursday

    [Theory]
    [InlineData(-2, "Just now")]
    [InlineData(-60, "Today")]
    [InlineData(-60 * 24, "Yesterday")]
    [InlineData(-60 * 24 * 3, "Monday")]
    [InlineData(-60 * 24 * 10, "5 Oct")]
    [InlineData(-60 * 24 * 400, "10 Sep 2025")]
    public void Describes_dates_naturally(int minutes, string expected) =>
        Assert.Equal(expected, RelativeDate.Describe(Now.AddMinutes(minutes), Now, CultureInfo.InvariantCulture));
}
