namespace Aperture.Core.Library;

/// <summary>Which items a library view shows.</summary>
public enum WatchFilter
{
    All,
    Unwatched,
    InProgress,
    Watched,
}

/// <summary>How a library view is ordered.</summary>
public enum LibrarySort
{
    RecentlyAdded,
    Title,
    Year,
}

/// <summary>An item ready to show: its copies, and how far through it the user is.</summary>
/// <param name="Group">The item and any other copies.</param>
/// <param name="State">Watched, in progress or unwatched.</param>
/// <param name="History">Playback history of the best copy, when it has been played.</param>
public sealed record LibraryEntry(CopyGroup Group, WatchState State, HistoryEntry? History)
{
    public LibraryItem Item => Group.Best;
}

/// <summary>Search, filtering and ordering of the library, done in memory so typing stays instant.</summary>
public static class LibraryQuery
{
    /// <summary>
    /// Builds the visible list. <paramref name="history"/> and <paramref name="watchedOverrides"/>
    /// are keyed by path (case-insensitive).
    /// </summary>
    public static IReadOnlyList<LibraryEntry> Run(
        IReadOnlyList<CopyGroup> groups,
        IReadOnlyDictionary<string, HistoryEntry> history,
        IReadOnlyDictionary<string, bool> watchedOverrides,
        MediaCategory? category,
        WatchFilter filter,
        LibrarySort sort,
        string? search)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(watchedOverrides);

        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = new List<LibraryEntry>();
        foreach (var group in groups)
        {
            var item = group.Best;
            if (item.IsMissing || (category is { } c && item.Category != c) || !Matches(item, words))
            {
                continue;
            }

            // A copy that was played counts for the whole group.
            var played = group.All
                .Select(copy => history.TryGetValue(copy.Path, out var h) ? h : null)
                .Where(h => h is not null)
                .MaxBy(h => h!.LastPlayedUtc);
            bool? marked = group.All.Select(copy => watchedOverrides.TryGetValue(copy.Path, out var w) ? w : (bool?)null).FirstOrDefault(w => w is not null);
            var state = WatchStatus.Of(played, marked);
            var keep = filter switch
            {
                WatchFilter.Unwatched => state != WatchState.Watched,
                WatchFilter.InProgress => state == WatchState.InProgress,
                WatchFilter.Watched => state == WatchState.Watched,
                _ => true,
            };
            if (keep)
            {
                entries.Add(new LibraryEntry(group, state, played));
            }
        }

        IOrderedEnumerable<LibraryEntry> ordered = sort switch
        {
            LibrarySort.Title => entries.OrderBy(e => SortTitle(e.Item.Title), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Item.Season ?? 0).ThenBy(e => e.Item.Episode ?? 0).ThenBy(e => e.Item.Date),
            LibrarySort.Year => entries.OrderByDescending(e => e.Item.Year ?? e.Item.Date?.Year ?? 0)
                .ThenBy(e => SortTitle(e.Item.Title), StringComparer.CurrentCultureIgnoreCase),
            _ => entries.OrderByDescending(e => e.Item.AddedUtc)
                .ThenBy(e => SortTitle(e.Item.Title), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Item.Season ?? 0).ThenBy(e => e.Item.Episode ?? 0),
        };

        return ordered.ToList();
    }

    /// <summary>True when every word appears in the title, episode title, year or file name.</summary>
    public static bool Matches(LibraryItem item, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return true;
        }

        var fileName = System.IO.Path.GetFileName(item.Path.Replace('\\', '/'));
        return words.All(w =>
            item.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || (item.EpisodeTitle?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) == w)
            || fileName.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Title without a leading "The", "A" or "An", for alphabetical order.</summary>
    public static string SortTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        foreach (var article in (string[])["The ", "A ", "An "])
        {
            if (title.Length > article.Length && title.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return title[article.Length..];
            }
        }

        return title;
    }
}
