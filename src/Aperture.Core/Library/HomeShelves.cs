namespace Aperture.Core.Library;

/// <summary>The rows shown on the home screen, derived from playback history.</summary>
/// <param name="ContinueWatching">Started, unfinished files, most recent first.</param>
/// <param name="RecentlyPlayed">Every other remembered file, most recent first.</param>
public sealed record HomeShelves(
    IReadOnlyList<HistoryEntry> ContinueWatching,
    IReadOnlyList<HistoryEntry> RecentlyPlayed)
{
    /// <summary>Most items shown in the Continue Watching row.</summary>
    public const int ContinueWatchingLimit = 12;

    /// <summary>True when there is nothing to show; the home screen shows its first-launch state.</summary>
    public bool IsEmpty => ContinueWatching.Count == 0 && RecentlyPlayed.Count == 0;

    /// <summary>Splits history into shelves. A file appears in exactly one shelf.</summary>
    public static HomeShelves From(IEnumerable<HistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var ordered = history.OrderByDescending(e => e.LastPlayedUtc).ToList();

        var continueWatching = ordered
            .Where(WatchProgress.IsInProgress)
            .Take(ContinueWatchingLimit)
            .ToList();

        var shown = continueWatching.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recentlyPlayed = ordered.Where(e => !shown.Contains(e.Path)).ToList();

        return new HomeShelves(continueWatching, recentlyPlayed);
    }

    /// <summary>
    /// Filters history by a search query. Every word in the query must appear in the title
    /// or the file name, ignoring case and order.
    /// </summary>
    public static IReadOnlyList<HistoryEntry> Search(IEnumerable<HistoryEntry> history, string query)
    {
        ArgumentNullException.ThrowIfNull(history);
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return history
            .Where(e => words.All(w =>
                e.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || System.IO.Path.GetFileName(e.Path).Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(e => e.LastPlayedUtc)
            .ToList();
    }
}
