using System.Globalization;

namespace Aperture.Core.Playback;

/// <summary>
/// Subtitle and audio timing offsets: the step a key press moves them by, the limits, and
/// how they are described. A positive offset delays (subtitles or sound appear later).
/// </summary>
public static class SyncOffset
{
    /// <summary>One key press.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    /// <summary>The largest offset in either direction.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>Adds <paramref name="steps"/> steps, rounded to the step and kept within the limit.</summary>
    public static TimeSpan Nudge(TimeSpan current, int steps)
    {
        var ticks = Math.Round((current + (Step * steps)) / Step) * Step.Ticks;
        return TimeSpan.FromTicks((long)Math.Clamp(ticks, -Limit.Ticks, Limit.Ticks));
    }

    /// <summary>Clamps a stored or typed offset to the limit and rounds it to milliseconds.</summary>
    public static TimeSpan Normalize(TimeSpan value) =>
        TimeSpan.FromMilliseconds(Math.Round(Math.Clamp(value.TotalMilliseconds, -Limit.TotalMilliseconds, Limit.TotalMilliseconds)));

    /// <summary>"0 s", "+0.3 s", "−1.2 s" (true minus sign).</summary>
    public static string Label(TimeSpan offset)
    {
        var seconds = Math.Round(offset.TotalSeconds, 2);
        if (Math.Abs(seconds) < 0.005)
        {
            return "0 s";
        }

        var text = Math.Abs(seconds).ToString("0.0#", CultureInfo.InvariantCulture);
        return (seconds > 0 ? "+" : "−") + text + " s";
    }

    /// <summary>"Subtitles 0.3 s later", "Sound 1.2 s earlier", "Subtitles in sync".</summary>
    public static string Describe(string what, TimeSpan offset)
    {
        var seconds = Math.Round(offset.TotalSeconds, 2);
        if (Math.Abs(seconds) < 0.005)
        {
            return $"{what} in sync";
        }

        var amount = Math.Abs(seconds).ToString("0.0#", CultureInfo.InvariantCulture);
        return $"{what} {amount} s {(seconds > 0 ? "later" : "earlier")}";
    }
}
