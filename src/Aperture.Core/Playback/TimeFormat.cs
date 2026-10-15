namespace Aperture.Core.Playback;

/// <summary>Formats media times the way Aperture shows them: 4:05, 1:02:03.</summary>
public static class TimeFormat
{
    /// <summary>Formats a position or duration. Negative values are treated as zero.</summary>
    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var totalSeconds = (long)Math.Floor(value.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        return hours > 0
            ? FormattableString.Invariant($"{hours}:{minutes:00}:{seconds:00}")
            : FormattableString.Invariant($"{minutes}:{seconds:00}");
    }

    /// <summary>
    /// A short, human description of time left, used under artwork:
    /// "45 sec left", "12 min left", "1 hr 5 min left".
    /// </summary>
    public static string Remaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
        {
            var seconds = Math.Max(0, (int)Math.Round(remaining.TotalSeconds));
            return FormattableString.Invariant($"{seconds} sec left");
        }

        var totalMinutes = (int)Math.Round(remaining.TotalMinutes);
        if (totalMinutes < 60)
        {
            return FormattableString.Invariant($"{totalMinutes} min left");
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0
            ? FormattableString.Invariant($"{hours} hr left")
            : FormattableString.Invariant($"{hours} hr {minutes} min left");
    }

    /// <summary>A compact length label: "42 min", "1 hr 58 min", "50 sec".</summary>
    public static string Length(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return FormattableString.Invariant($"{Math.Max(0, (int)Math.Round(duration.TotalSeconds))} sec");
        }

        var totalMinutes = (int)Math.Round(duration.TotalMinutes);
        if (totalMinutes < 60)
        {
            return FormattableString.Invariant($"{totalMinutes} min");
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0
            ? FormattableString.Invariant($"{hours} hr")
            : FormattableString.Invariant($"{hours} hr {minutes} min");
    }
}
