namespace Aperture.Core.Library;

/// <summary>
/// Rules that decide whether a file is worth resuming. Kept in one place so the home
/// screen and the player agree.
/// </summary>
public static class WatchProgress
{
    /// <summary>Below this position a file counts as not started.</summary>
    public static readonly TimeSpan MinimumStarted = TimeSpan.FromSeconds(10);

    /// <summary>Within this much of the end (or past <see cref="FinishedFraction"/>) a file counts as finished.</summary>
    public static readonly TimeSpan FinishedTail = TimeSpan.FromSeconds(20);

    /// <summary>Fraction of the duration after which a file counts as finished.</summary>
    public const double FinishedFraction = 0.95;

    /// <summary>True when the entry was started but not finished.</summary>
    public static bool IsInProgress(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Duration > TimeSpan.Zero
            && entry.Position >= MinimumStarted
            && !IsFinished(entry);
    }

    /// <summary>True when the entry was watched to (or very near) the end.</summary>
    public static bool IsFinished(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Duration <= TimeSpan.Zero)
        {
            return false;
        }

        return entry.Progress >= FinishedFraction || entry.Remaining <= FinishedTail;
    }

    /// <summary>Where playback should start for this entry, or zero to start from the beginning.</summary>
    public static TimeSpan ResumePosition(HistoryEntry? entry) =>
        entry is not null && IsInProgress(entry) ? entry.Position : TimeSpan.Zero;
}
