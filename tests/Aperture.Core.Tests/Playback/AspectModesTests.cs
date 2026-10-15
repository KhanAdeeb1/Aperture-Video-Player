using Aperture.Core.Playback;

namespace Aperture.Core.Tests.Playback;

public class AspectModesTests
{
    [Fact]
    public void All_lists_every_mode_once() =>
        Assert.Equal(Enum.GetValues<AspectMode>().OrderBy(m => m), AspectModes.All.OrderBy(m => m));

    [Fact]
    public void Next_cycles_through_every_mode_and_wraps()
    {
        var mode = AspectMode.Original;
        var seen = new List<AspectMode>();
        for (var i = 0; i < AspectModes.All.Count; i++)
        {
            seen.Add(mode);
            mode = AspectModes.Next(mode);
        }

        Assert.Equal(AspectModes.All, seen);
        Assert.Equal(AspectMode.Original, mode);
    }

    [Fact]
    public void Every_mode_has_a_label() =>
        Assert.All(AspectModes.All, m => Assert.False(string.IsNullOrWhiteSpace(AspectModes.Label(m))));
}
