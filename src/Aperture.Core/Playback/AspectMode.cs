namespace Aperture.Core.Playback;

/// <summary>How the picture is fitted to the window.</summary>
public enum AspectMode
{
    /// <summary>The shape the file asks for (including non-square pixels), letterboxed to fit.</summary>
    Original,

    /// <summary>Fill the window, cropping the edges that do not fit.</summary>
    Fill,

    /// <summary>Stretch to the window's shape, distorting the picture.</summary>
    Stretch,

    /// <summary>Force 16:9.</summary>
    Widescreen,

    /// <summary>Force 4:3.</summary>
    Standard,

    /// <summary>Force 2.39:1 (cinema scope).</summary>
    Scope,
}

/// <summary>Names and order of <see cref="AspectMode"/> values as Aperture shows them.</summary>
public static class AspectModes
{
    /// <summary>All modes in menu order.</summary>
    public static IReadOnlyList<AspectMode> All { get; } =
        [AspectMode.Original, AspectMode.Fill, AspectMode.Stretch, AspectMode.Widescreen, AspectMode.Standard, AspectMode.Scope];

    /// <summary>The label for a mode.</summary>
    public static string Label(AspectMode mode) => mode switch
    {
        AspectMode.Original => "Original",
        AspectMode.Fill => "Fill window",
        AspectMode.Stretch => "Stretch",
        AspectMode.Widescreen => "16:9",
        AspectMode.Standard => "4:3",
        AspectMode.Scope => "2.39:1",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>The mode after <paramref name="mode"/>, wrapping round.</summary>
    public static AspectMode Next(AspectMode mode)
    {
        var index = -1;
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == mode)
            {
                index = i;
                break;
            }
        }

        return All[(index + 1) % All.Count];
    }
}
