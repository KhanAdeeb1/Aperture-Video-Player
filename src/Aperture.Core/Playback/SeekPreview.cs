namespace Aperture.Core.Playback;

/// <summary>
/// Where seek previews are taken. Pointer positions are rounded to a grid so moving along the
/// timeline reuses pictures already made instead of decoding a new one for every pixel; the
/// grid is finer for short videos and never coarser than about 400 pictures per file.
/// </summary>
public static class SeekPreview
{
    /// <summary>The smallest distance between two previews.</summary>
    public static readonly TimeSpan MinimumStep = TimeSpan.FromSeconds(2);

    /// <summary>At most this many distinct previews per file.</summary>
    public const int MaximumSlots = 400;

    /// <summary>Previews are not offered for anything shorter (there is little to find).</summary>
    public static readonly TimeSpan ShortestVideo = TimeSpan.FromSeconds(10);

    /// <summary>The distance between previews for a file of <paramref name="duration"/>.</summary>
    public static TimeSpan Step(TimeSpan duration)
    {
        var even = TimeSpan.FromSeconds(Math.Ceiling(duration.TotalSeconds / MaximumSlots));
        return even > MinimumStep ? even : MinimumStep;
    }

    /// <summary>The time the preview for <paramref name="position"/> is taken at (on the grid, inside the file).</summary>
    public static TimeSpan Slot(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var step = Step(duration);
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
        var slot = TimeSpan.FromTicks((long)Math.Round((double)clamped.Ticks / step.Ticks) * step.Ticks);

        // The very end has no frame to show; stay a moment inside the file.
        var last = duration - TimeSpan.FromSeconds(1);
        return slot > last ? (last > TimeSpan.Zero ? last : TimeSpan.Zero) : slot;
    }

    /// <summary>
    /// The preview picture's size in pixels for a picture of <paramref name="aspect"/> (width ÷
    /// height), as large as fits in the box. Unknown shapes are treated as 16:9.
    /// </summary>
    public static (int Width, int Height) Size(double aspect, int boxWidth, int boxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(boxWidth, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(boxHeight, 2);
        if (!double.IsFinite(aspect) || aspect <= 0)
        {
            aspect = 16.0 / 9;
        }

        double width = boxWidth;
        var height = width / aspect;
        if (height > boxHeight)
        {
            height = boxHeight;
            width = height * aspect;
        }

        return (Math.Max(2, (int)Math.Round(width)), Math.Max(2, (int)Math.Round(height)));
    }
}
