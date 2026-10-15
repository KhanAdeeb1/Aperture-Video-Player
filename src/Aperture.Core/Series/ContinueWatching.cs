using Aperture.Core.Library;

namespace Aperture.Core.Series;

/// <summary>One card in Continue Watching.</summary>
/// <param name="Path">The file to play.</param>
/// <param name="History">Where the user stopped, for a part-watched file; null for an up-next episode.</param>
/// <param name="Episode">The episode, when the file is part of a show.</param>
/// <param name="IsUpNext">True when the user finished the previous episode and this is the next one.</param>
/// <param name="LastActivityUtc">When the user last watched this show or file, for ordering.</param>
public sealed record ContinueItem(string Path, HistoryEntry? History, SeriesEpisode? Episode, bool IsUpNext, DateTimeOffset LastActivityUtc);

/// <summary>
/// Builds Continue Watching. Films and other videos appear while part-watched, as before.
/// A show appears once: as its part-watched episode, or, once the user has finished an
/// episode, as the next one they haven't watched.
/// </summary>
public static class ContinueWatching
{
    /// <summary>Most items in the row.</summary>
    public const int Limit = 12;

    public static IReadOnlyList<ContinueItem> Build(
        IEnumerable<HistoryEntry> history,
        SeriesCatalog catalog,
        IReadOnlyDictionary<string, bool> watchedOverrides,
        int limit = Limit)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(watchedOverrides);

        var byPath = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in history)
        {
            if (!byPath.TryGetValue(entry.Path, out var existing) || entry.LastPlayedUtc > existing.LastPlayedUtc)
            {
                byPath[entry.Path] = entry;
            }
        }

        var items = new List<ContinueItem>();
        var shows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in byPath.Values.OrderByDescending(e => e.LastPlayedUtc))
        {
            var state = WatchStatus.Of(entry, Marked(watchedOverrides, entry.Path));
            if (catalog.EpisodeOf(entry.Path) is not { } episode)
            {
                if (state == WatchState.InProgress)
                {
                    items.Add(new ContinueItem(entry.Path, entry, null, false, entry.LastPlayedUtc));
                }

                continue;
            }

            // Only the episode watched most recently speaks for its show.
            if (shows.Contains(episode.SeriesKey))
            {
                continue;
            }

            if (state == WatchState.InProgress)
            {
                shows.Add(episode.SeriesKey);
                items.Add(new ContinueItem(entry.Path, entry, episode, false, entry.LastPlayedUtc));
            }
            else if (state == WatchState.Watched)
            {
                shows.Add(episode.SeriesKey);
                if (NextUnwatched(catalog, entry.Path, byPath, watchedOverrides) is { } next)
                {
                    items.Add(new ContinueItem(next.PreferredCopy(entry.Path).Path, null, next, true, entry.LastPlayedUtc));
                }
            }

            // An episode opened and closed straight away says nothing; an earlier one may.
        }

        return items.Take(limit).ToList();
    }

    /// <summary>The first episode after <paramref name="path"/> that no copy of has been watched.</summary>
    public static SeriesEpisode? NextUnwatched(
        SeriesCatalog catalog,
        string path,
        IReadOnlyDictionary<string, HistoryEntry> history,
        IReadOnlyDictionary<string, bool> watchedOverrides)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(watchedOverrides);
        var current = path;
        for (var guard = 0; guard < 500 && catalog.Next(current) is { } next; guard++)
        {
            var watched = next.Group.All.Any(copy =>
                WatchStatus.Of(history.TryGetValue(copy.Path, out var h) ? h : null, Marked(watchedOverrides, copy.Path)) == WatchState.Watched);
            if (!watched)
            {
                return next;
            }

            current = next.Item.Path;
        }

        return null;
    }

    private static bool? Marked(IReadOnlyDictionary<string, bool> overrides, string path) =>
        overrides.TryGetValue(path, out var marked) ? marked : null;
}
