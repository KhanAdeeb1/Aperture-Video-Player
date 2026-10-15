using Aperture.Core.Library;

namespace Aperture.Core.YouTube;

/// <summary>
/// Rules for what Aperture keeps about YouTube videos, in one place so the screens, the store
/// and the tests agree.
/// <list type="bullet">
/// <item>YouTube's API Services Developer Policies (III.E.4) let a client keep data it fetched
/// without the user's sign-in for at most 30 days, after which it must be refreshed or deleted.
/// Titles, channel names, thumbnails and lengths are refreshed when a key is available and
/// removed otherwise. The video id and Aperture's own position and dates are Aperture's records.</item>
/// <item>Continue Watching uses the same thresholds as local files (<see cref="WatchProgress"/>),
/// measured only from playback that happened inside Aperture.</item>
/// </list>
/// </summary>
public static class YouTubeDataRules
{
    /// <summary>The longest YouTube's details may be kept before being refreshed or removed.</summary>
    public static readonly TimeSpan DetailsLifetime = TimeSpan.FromDays(30);

    /// <summary>Details older than this are refreshed when possible, leaving a margin before the limit.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(25);

    /// <summary>The most entries shown in Continue Watching.</summary>
    public const int ContinueWatchingLimit = 20;

    /// <summary>True when the entry was started in Aperture and not finished.</summary>
    public static bool IsInProgress(YouTubeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.WatchedUtc is not null
            && entry.Duration > TimeSpan.Zero
            && entry.Position >= WatchProgress.MinimumStarted
            && !IsFinished(entry.Position, entry.Duration);
    }

    /// <summary>True at (or very near) the end, by the same rule as local files.</summary>
    public static bool IsFinished(TimeSpan position, TimeSpan duration) =>
        duration > TimeSpan.Zero
        && (position / duration >= WatchProgress.FinishedFraction || duration - position <= WatchProgress.FinishedTail);

    /// <summary>Where to start a video: the link's own time if it has one, else where the user stopped, else the beginning.</summary>
    public static TimeSpan StartPosition(YouTubeEntry? entry, TimeSpan linkStart) =>
        linkStart > TimeSpan.Zero ? linkStart
        : entry is not null && IsInProgress(entry) ? entry.Position
        : TimeSpan.Zero;

    /// <summary>Continue Watching: in-progress videos, most recent first.</summary>
    public static IReadOnlyList<YouTubeEntry> ContinueWatching(IEnumerable<YouTubeEntry> entries) =>
        entries.Where(IsInProgress)
            .OrderByDescending(e => e.WatchedUtc)
            .Take(ContinueWatchingLimit)
            .ToList();

    /// <summary>Saved videos, most recently saved first.</summary>
    public static IReadOnlyList<YouTubeEntry> Saved(IEnumerable<YouTubeEntry> entries) =>
        entries.Where(e => e.SavedUtc is not null).OrderByDescending(e => e.SavedUtc).ToList();

    /// <summary>True when an entry's YouTube details must be refreshed or removed now.</summary>
    public static bool DetailsExpired(YouTubeEntry entry, DateTimeOffset now) =>
        entry.DetailsUtc is { } fetched && now - fetched >= DetailsLifetime;

    /// <summary>True when the details are old enough to refresh while a key is available.</summary>
    public static bool DetailsDueForRefresh(YouTubeEntry entry, DateTimeOffset now) =>
        entry.DetailsUtc is null || now - entry.DetailsUtc.Value >= RefreshAfter;
}
