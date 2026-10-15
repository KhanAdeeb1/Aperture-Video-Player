namespace Aperture.Core.Windowing;

/// <summary>A rectangle in physical pixels, as Windows reports window and display bounds.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>The area two rectangles share, or zero when they do not overlap.</summary>
    public long OverlapArea(PixelRect other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return width > 0 && height > 0 ? (long)width * height : 0;
    }
}

/// <summary>
/// Keeps restored window positions usable after display changes: a monitor unplugged, a
/// resolution or scale change, or a laptop undocked while the player was full screen or in
/// the mini-player.
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Returns <paramref name="bounds"/> moved and, if needed, shrunk so the whole window lies in
    /// <paramref name="workArea"/>. Sizes never go below the minimum unless the work area itself
    /// is smaller.
    /// </summary>
    public static PixelRect FitInto(PixelRect bounds, PixelRect workArea, int minimumWidth, int minimumHeight)
    {
        var width = Math.Clamp(bounds.Width, Math.Min(minimumWidth, workArea.Width), workArea.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(minimumHeight, workArea.Height), workArea.Height);
        var x = Math.Clamp(bounds.X, workArea.X, workArea.Right - width);
        var y = Math.Clamp(bounds.Y, workArea.Y, workArea.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// Chooses the work area a window belongs on: the one it overlaps most, or the first
    /// (primary) when it overlaps none because its display has gone.
    /// </summary>
    public static PixelRect ChooseWorkArea(PixelRect bounds, IReadOnlyList<PixelRect> workAreas)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (workAreas.Count == 0)
        {
            throw new ArgumentException("At least one work area is needed.", nameof(workAreas));
        }

        var best = workAreas[0];
        long bestOverlap = 0;
        foreach (var area in workAreas)
        {
            var overlap = bounds.OverlapArea(area);
            if (overlap > bestOverlap)
            {
                best = area;
                bestOverlap = overlap;
            }
        }

        return best;
    }
}
