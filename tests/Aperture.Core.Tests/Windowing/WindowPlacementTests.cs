using Aperture.Core.Windowing;

namespace Aperture.Core.Tests.Windowing;

public class WindowPlacementTests
{
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);
    private static readonly PixelRect Right = new(1920, 0, 2560, 1400);

    [Fact]
    public void A_window_that_fits_is_unchanged()
    {
        var bounds = new PixelRect(100, 100, 1280, 800);
        Assert.Equal(bounds, WindowPlacement.FitInto(bounds, Primary, 640, 400));
    }

    [Fact]
    public void A_window_left_on_an_unplugged_display_comes_back()
    {
        var bounds = new PixelRect(2200, 300, 1280, 800);
        var area = WindowPlacement.ChooseWorkArea(bounds, [Primary]);
        Assert.Equal(new PixelRect(640, 240, 1280, 800), WindowPlacement.FitInto(bounds, area, 640, 400));
    }

    [Fact]
    public void A_window_larger_than_the_display_is_shrunk()
    {
        var fitted = WindowPlacement.FitInto(new PixelRect(-50, -20, 2560, 1400), Primary, 640, 400);
        Assert.Equal(Primary, fitted);
    }

    [Fact]
    public void The_display_with_most_of_the_window_wins()
    {
        var bounds = new PixelRect(1800, 100, 1000, 600);
        Assert.Equal(Right, WindowPlacement.ChooseWorkArea(bounds, [Primary, Right]));
        Assert.Equal(Primary, WindowPlacement.ChooseWorkArea(new PixelRect(5000, 5000, 100, 100), [Primary, Right]));
    }
}
