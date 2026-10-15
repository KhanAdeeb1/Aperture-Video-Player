using Aperture.Core.Library;

namespace Aperture.Core.Series;

/// <summary>One episode of a show: the item and its copies, and where it sits in the show.</summary>
/// <param name="Group">The episode's best copy and any others.</param>
/// <param name="SeriesKey">The show it belongs to.</param>
public sealed record SeriesEpisode(CopyGroup Group, string SeriesKey)
{
    public LibraryItem Item => Group.Best;

    public int Season => Item.Season ?? 1;

    public int? Episode => Item.Episode;

    /// <summary>The last episode the file holds (the same as <see cref="Episode"/> for a single episode).</summary>
    public int? LastEpisode => Item.EpisodeEnd ?? Item.Episode;

    /// <summary>Season 0, where specials, extras and films of a show are usually kept.</summary>
    public bool IsSpecial => Item.Season == 0;

    /// <summary>"S1 E3", or the file's own name when it has no episode number.</summary>
    public string Label => Episode is { } e ? LibraryItem.EpisodeLabel(Season, e, Item.EpisodeEnd) : Item.EpisodeTitle ?? System.IO.Path.GetFileNameWithoutExtension(Item.Path);

    /// <summary>
    /// The copy to play when arriving from <paramref name="currentPath"/>: one in the same folder
    /// (the same release, usually the same quality and audio) when there is one, otherwise the best.
    /// </summary>
    public LibraryItem PreferredCopy(string? currentPath)
    {
        if (currentPath is null || !Group.HasCopies)
        {
            return Group.Best;
        }

        var folder = DirectoryOf(currentPath);
        return Group.All.FirstOrDefault(c => string.Equals(DirectoryOf(c.Path), folder, StringComparison.OrdinalIgnoreCase)) ?? Group.Best;
    }

    private static string DirectoryOf(string path)
    {
        var cut = path.Replace('\\', '/').LastIndexOf('/');
        return cut > 0 ? path[..cut] : string.Empty;
    }
}

/// <summary>A show and its episodes in watching order.</summary>
/// <param name="Key">Stable identity, used for per-show preferences.</param>
/// <param name="Title">The show's name as the library shows it.</param>
/// <param name="Episodes">Numbered episodes by season and episode, then unnumbered ones, then specials.</param>
public sealed record SeriesInfo(string Key, string Title, IReadOnlyList<SeriesEpisode> Episodes)
{
    /// <summary>Distinct regular seasons, in order.</summary>
    public IReadOnlyList<int> Seasons => Episodes.Where(e => !e.IsSpecial).Select(e => e.Season).Distinct().Order().ToList();
}

/// <summary>
/// The library's shows, built fresh from the current library items every time it is needed.
/// Order comes only from each item's season and episode numbers (as parsed from names or
/// corrected by the user), never from database row order, so it is the same after any rescan.
/// </summary>
public sealed class SeriesCatalog
{
    private readonly Dictionary<string, (SeriesInfo Series, SeriesEpisode Episode)> _byPath = new(StringComparer.OrdinalIgnoreCase);

    private SeriesCatalog(IReadOnlyList<SeriesInfo> series)
    {
        All = series;
        foreach (var show in series)
        {
            foreach (var episode in show.Episodes)
            {
                foreach (var copy in episode.Group.All)
                {
                    _byPath.TryAdd(copy.Path, (show, episode));
                }
            }
        }
    }

    public static SeriesCatalog Empty { get; } = new([]);

    /// <summary>Every show, by title.</summary>
    public IReadOnlyList<SeriesInfo> All { get; }

    /// <summary>Groups the episode items of a library into shows.</summary>
    public static SeriesCatalog Build(IEnumerable<CopyGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var episodes = groups.Where(g => g.Best.Category == MediaCategory.Episode && !g.Best.IsMissing).ToList();

        // The same title with two different years is two shows ("Doctor Who (1963)" and "(2005)").
        // Episodes without a year join the year most of that title's episodes have.
        var keys = new Dictionary<CopyGroup, string>();
        foreach (var byTitle in episodes.GroupBy(g => LibraryItem.Normalize(g.Best.Title)))
        {
            var years = byTitle.Where(g => g.Best.Year is not null).GroupBy(g => g.Best.Year!.Value).OrderByDescending(y => y.Count()).ThenBy(y => y.Key).ToList();
            foreach (var group in byTitle)
            {
                keys[group] = years.Count <= 1 ? byTitle.Key : $"{byTitle.Key}|{group.Best.Year ?? years[0].Key}";
            }
        }

        var series = episodes
            .GroupBy(g => keys[g])
            .Select(show =>
            {
                var ordered = show.Select(g => new SeriesEpisode(g, show.Key))
                    .OrderBy(e => e.IsSpecial)
                    .ThenBy(e => e.Episode is null)
                    .ThenBy(e => e.Season)
                    .ThenBy(e => e.Episode ?? 0)
                    .ThenBy(e => e.LastEpisode ?? 0)
                    .ThenBy(e => e.Item.Path, NaturalComparer.Instance)
                    .ToList();
                var title = show.GroupBy(g => g.Best.Title).OrderByDescending(t => t.Count()).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key;
                return new SeriesInfo(show.Key, title, ordered);
            })
            .OrderBy(s => LibraryQuery.SortTitle(s.Title), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new SeriesCatalog(series);
    }

    /// <summary>The show a file belongs to, or null when it is not a library episode.</summary>
    public SeriesInfo? SeriesOf(string path) => _byPath.TryGetValue(path, out var found) ? found.Series : null;

    /// <summary>The episode a file is (any copy of it), or null.</summary>
    public SeriesEpisode? EpisodeOf(string path) => _byPath.TryGetValue(path, out var found) ? found.Episode : null;

    /// <summary>
    /// The episode after the one in <paramref name="path"/>: the next number in the season, or the
    /// first of the next season. A file holding two episodes continues after the second. Specials
    /// continue with the next special. Null at the end of the show, or when the episode has no
    /// number to go on.
    /// </summary>
    public SeriesEpisode? Next(string path)
    {
        if (!_byPath.TryGetValue(path, out var found) || found.Episode.Episode is null)
        {
            return null;
        }

        var current = found.Episode;
        return Chain(found.Series, current.IsSpecial)
            .FirstOrDefault(e => !ReferenceEquals(e, current) && Compare(e.Season, e.Episode!.Value, current.Season, current.LastEpisode!.Value) > 0);
    }

    /// <summary>The episode before the one in <paramref name="path"/>, or null at the start.</summary>
    public SeriesEpisode? Previous(string path)
    {
        if (!_byPath.TryGetValue(path, out var found) || found.Episode.Episode is null)
        {
            return null;
        }

        var current = found.Episode;
        return Chain(found.Series, current.IsSpecial)
            .LastOrDefault(e => !ReferenceEquals(e, current) && Compare(e.Season, e.LastEpisode!.Value, current.Season, current.Episode!.Value) < 0);
    }

    private static IEnumerable<SeriesEpisode> Chain(SeriesInfo series, bool specials) =>
        series.Episodes.Where(e => e.IsSpecial == specials && e.Episode is not null);

    private static int Compare(int seasonA, int episodeA, int seasonB, int episodeB) =>
        seasonA != seasonB ? seasonA.CompareTo(seasonB) : episodeA.CompareTo(episodeB);
}

/// <summary>Orders "Episode 2" before "Episode 10", ignoring case.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byLength = numberX.Length.CompareTo(numberY.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byDigits = string.CompareOrdinal(numberX, numberY);
                if (byDigits != 0)
                {
                    return byDigits;
                }

                continue;
            }

            var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byChar != 0)
            {
                return byChar;
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
