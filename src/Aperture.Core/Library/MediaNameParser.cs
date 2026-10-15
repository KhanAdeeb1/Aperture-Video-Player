using System.Globalization;
using System.Text.RegularExpressions;

namespace Aperture.Core.Library;

/// <summary>What a file's name and folders say about it.</summary>
/// <param name="Category">What the file seems to be.</param>
/// <param name="Title">Movie title, show or podcast name, or the video's name.</param>
/// <param name="Year">Release year, for films.</param>
/// <param name="Season">Season number, for episodes.</param>
/// <param name="Episode">Episode number, for episodes and podcasts.</param>
/// <param name="EpisodeTitle">The episode's own title, when the name carries one.</param>
/// <param name="Date">A recording or air date in the name.</param>
/// <param name="Confidence">How clear the name was.</param>
public sealed record ParsedName(
    MediaCategory Category,
    string Title,
    int? Year = null,
    int? Season = null,
    int? Episode = null,
    string? EpisodeTitle = null,
    DateOnly? Date = null,
    MatchConfidence Confidence = MatchConfidence.Uncertain);

/// <summary>
/// Reads titles, years, seasons, episodes and dates from file and folder names, the way
/// files are commonly named ("Film.Name.2019.1080p.mkv", "Show - S02E05 - Title.mkv",
/// "Show/Season 2/05 Title.mkv", "2024-07-14 Beach.mp4"). It never opens the file.
/// </summary>
public static partial class MediaNameParser
{
    private static readonly HashSet<string> CameraPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "IMG", "VID", "PXL", "MOV", "MVI", "DSC", "DSCF", "GOPR", "GH01", "GX01", "DJI", "MAH", "CIMG", "Screen Recording",
    };

    /// <summary>
    /// Parses <paramref name="path"/>, which lies inside the library folder
    /// <paramref name="folderRoot"/> (folders above it are not used as names).
    /// </summary>
    public static ParsedName Parse(string path, string folderRoot, LibraryFolderKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileName = FileNameWithoutExtension(path);
        var folders = FoldersBetween(path, folderRoot ?? string.Empty);
        var name = Tidy(fileName);
        var isAudio = Media.MediaFileTypes.IsAudioFile(path);

        var episode = MatchEpisode(name, folders);
        var date = MatchDate(fileName);

        var category = kind switch
        {
            LibraryFolderKind.Movies => MediaCategory.Movie,
            LibraryFolderKind.Shows => MediaCategory.Episode,
            LibraryFolderKind.Personal => MediaCategory.Personal,
            LibraryFolderKind.Podcasts => MediaCategory.Podcast,
            _ when isAudio || folders.Any(f => f.Contains("podcast", StringComparison.OrdinalIgnoreCase)) => MediaCategory.Podcast,
            _ when episode is not null && episode.Season is not null => MediaCategory.Episode,
            _ when date is not null || LooksLikeCamera(fileName) => MediaCategory.Personal,
            _ when FindYear(name) is not null => MediaCategory.Movie,
            _ when folders.Count > 0 && FindYear(Tidy(folders[^1])) is not null => MediaCategory.Movie,
            _ when episode is not null => MediaCategory.Episode,
            _ => MediaCategory.Personal,
        };

        return category switch
        {
            MediaCategory.Movie => ParseMovie(name, folders, kind == LibraryFolderKind.Movies),
            MediaCategory.Episode => ParseEpisode(name, folders, episode, kind == LibraryFolderKind.Shows),
            MediaCategory.Podcast => ParsePodcast(name, folders, episode, date),
            _ => ParsePersonal(name, fileName, date, kind == LibraryFolderKind.Personal),
        };
    }

    // ===== Categories =====

    private static ParsedName ParseMovie(string name, List<string> folders, bool toldMovies)
    {
        var (title, year) = TitleAndYear(name);
        if (year is null && folders.Count > 0)
        {
            // "Film (2019)/film.mkv": the folder often carries the year the file lacks.
            var (folderTitle, folderYear) = TitleAndYear(Tidy(folders[^1]));
            if (folderYear is not null && (title.Length == 0 || SameTitle(title, folderTitle) || LooksGeneric(title)))
            {
                title = folderTitle;
                year = folderYear;
            }
        }

        if (title.Length == 0)
        {
            title = name;
        }

        var confidence = year is not null ? MatchConfidence.Certain : toldMovies ? MatchConfidence.Likely : MatchConfidence.Uncertain;
        return new ParsedName(MediaCategory.Movie, title, year, Confidence: confidence);
    }

    private static ParsedName ParseEpisode(string name, List<string> folders, EpisodeMatch? match, bool toldShows)
    {
        var showFolder = ShowFolder(folders);
        if (match is null)
        {
            var title = showFolder ?? CutAtTags(name);
            return new ParsedName(MediaCategory.Episode, title, EpisodeTitle: showFolder is null ? null : CutAtTags(name), Confidence: MatchConfidence.Uncertain);
        }

        var show = match.Show.Length > 0 ? match.Show : showFolder ?? string.Empty;
        var (showTitle, year) = TitleAndYear(show);
        if (showTitle.Length == 0)
        {
            showTitle = showFolder ?? name;
        }

        var confidence = match.Strong ? MatchConfidence.Certain : toldShows ? MatchConfidence.Likely : match.Confidence;
        return new ParsedName(
            MediaCategory.Episode,
            showTitle,
            year,
            match.Season ?? 1,
            match.Episode,
            NullIfEmpty(match.EpisodeTitle),
            Confidence: confidence);
    }

    private static ParsedName ParsePodcast(string name, List<string> folders, EpisodeMatch? match, DateOnly? date)
    {
        var show = folders.Count > 0 ? Tidy(folders[^1]) : null;
        if (show is not null && show.Contains("podcast", StringComparison.OrdinalIgnoreCase) && folders.Count > 1)
        {
            // "Podcasts/Design Notes/episode.mp3": the show is the folder under "Podcasts".
            show = Tidy(folders[^1]);
        }

        if (match is not null)
        {
            var title = match.Show.Length > 0 ? match.Show : show ?? name;
            return new ParsedName(MediaCategory.Podcast, title, Episode: match.Episode, EpisodeTitle: NullIfEmpty(match.EpisodeTitle), Date: date, Confidence: MatchConfidence.Likely);
        }

        var cleaned = CutAtTags(RemoveDate(name));
        return new ParsedName(
            MediaCategory.Podcast,
            show ?? cleaned,
            EpisodeTitle: show is null ? null : NullIfEmpty(cleaned),
            Date: date,
            Confidence: show is null ? MatchConfidence.Uncertain : MatchConfidence.Likely);
    }

    private static ParsedName ParsePersonal(string name, string rawFileName, DateOnly? date, bool toldPersonal)
    {
        var rest = CutAtTags(RemoveDate(name));
        if (LooksLikeCamera(rawFileName) || rest.Length == 0 || rest.All(c => char.IsDigit(c) || c is ' ' or '-'))
        {
            rest = date is { } d ? "Video from " + d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : name;
        }

        return new ParsedName(MediaCategory.Personal, rest, Date: date, Confidence: toldPersonal || date is not null ? MatchConfidence.Likely : MatchConfidence.Uncertain);
    }

    // ===== Episodes =====

    private sealed record EpisodeMatch(string Show, int? Season, int Episode, string EpisodeTitle, bool Strong, MatchConfidence Confidence);

    private static EpisodeMatch? MatchEpisode(string name, List<string> folders)
    {
        var m = SeasonEpisodePattern().Match(name);
        if (m.Success)
        {
            return new EpisodeMatch(Before(name, m), Int(m.Groups["s"]), Int(m.Groups["e"])!.Value, After(name, m), Strong: true, MatchConfidence.Certain);
        }

        m = CrossPattern().Match(name);
        if (m.Success)
        {
            return new EpisodeMatch(Before(name, m), Int(m.Groups["s"]), Int(m.Groups["e"])!.Value, After(name, m), Strong: false, MatchConfidence.Likely);
        }

        m = WordsPattern().Match(name);
        if (m.Success)
        {
            return new EpisodeMatch(Before(name, m), Int(m.Groups["s"]), Int(m.Groups["e"])!.Value, After(name, m), Strong: true, MatchConfidence.Certain);
        }

        var season = folders.Select(f => SeasonFolderPattern().Match(f)).LastOrDefault(x => x.Success);
        m = LeadingEpisodePattern().Match(name);
        if (m.Success && season is not null)
        {
            return new EpisodeMatch(string.Empty, Int(season.Groups["s"]), Int(m.Groups["e"])!.Value, After(name, m), Strong: false, MatchConfidence.Likely);
        }

        m = EpisodeWordPattern().Match(name);
        if (m.Success)
        {
            return new EpisodeMatch(Before(name, m), season is null ? null : Int(season.Groups["s"]), Int(m.Groups["e"])!.Value, After(name, m), Strong: false, MatchConfidence.Likely);
        }

        m = DashNumberPattern().Match(name);
        if (m.Success)
        {
            // "[Group] Show - 05 [1080p]": common for series released without seasons.
            return new EpisodeMatch(Before(name, m), season is null ? null : Int(season.Groups["s"]), Int(m.Groups["e"])!.Value, After(name, m), Strong: false, MatchConfidence.Uncertain);
        }

        return null;
    }

    private static string? ShowFolder(List<string> folders)
    {
        for (var i = folders.Count - 1; i >= 0; i--)
        {
            if (!SeasonFolderPattern().IsMatch(folders[i]) && !folders[i].Equals("Specials", StringComparison.OrdinalIgnoreCase))
            {
                return TitleAndYear(Tidy(folders[i])).Title;
            }
        }

        return null;
    }

    // ===== Titles, years, dates =====

    private static (string Title, int? Year) TitleAndYear(string name)
    {
        var year = FindYear(name);
        if (year is null)
        {
            return (CutAtTags(name), null);
        }

        var title = CutAtTags(name[..year.Value.Index]);
        return title.Length == 0 ? (CutAtTags(name), null) : (title, year.Value.Year);
    }

    private static (int Index, int Year)? FindYear(string name)
    {
        (int, int)? found = null;
        foreach (Match m in YearPattern().Matches(name))
        {
            // A year at the very start is usually part of the title ("1917 (2019)", "2001 A Space Odyssey").
            if (m.Index == 0 && !m.Value.StartsWith('('))
            {
                continue;
            }

            var year = int.Parse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture);
            if (year >= 1900 && year <= DateTime.UtcNow.Year + 1)
            {
                found = (m.Index, year);
            }
        }

        return found;
    }

    private static DateOnly? MatchDate(string rawName)
    {
        var m = DatePattern().Match(rawName);
        if (!m.Success)
        {
            m = CompactDatePattern().Match(rawName);
        }

        if (m.Success
            && int.TryParse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture, out var y)
            && int.TryParse(m.Groups["m"].ValueSpan, CultureInfo.InvariantCulture, out var mo)
            && int.TryParse(m.Groups["d"].ValueSpan, CultureInfo.InvariantCulture, out var d)
            && y is >= 1970 and <= 2100 && mo is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, mo))
        {
            return new DateOnly(y, mo, d);
        }

        return null;
    }

    private static string RemoveDate(string name)
    {
        var without = CompactDatePattern().Replace(DatePattern().Replace(name, " "), " ");
        return CameraPrefixPattern().Replace(without, " ").Trim(' ', '-', '_', '.');
    }

    private static bool LooksLikeCamera(string rawName)
    {
        var m = CameraPrefixPattern().Match(rawName);
        return m.Success && CameraPrefixes.Contains(m.Groups["p"].Value);
    }

    private static bool LooksGeneric(string title) =>
        title.Length <= 3 || title.Equals("movie", StringComparison.OrdinalIgnoreCase) || title.Equals("film", StringComparison.OrdinalIgnoreCase);

    private static bool SameTitle(string a, string b) => LibraryItem.Normalize(a) == LibraryItem.Normalize(b);

    /// <summary>Cuts the name before release tags (1080p, BluRay, x264...) and tidies what is left.</summary>
    private static string CutAtTags(string name)
    {
        var m = TagPattern().Match(name);
        var kept = m.Success ? name[..m.Index] : name;
        return kept.Trim(' ', '-', '_', '.', '(', '[', ',', '–').Trim();
    }

    /// <summary>Removes leading [Group] tags, turns dots and underscores into spaces, and collapses spaces.</summary>
    private static string Tidy(string name)
    {
        var text = LeadingGroupPattern().Replace(name, string.Empty);
        var dots = text.Count(c => c == '.');
        if (text.Contains('_', StringComparison.Ordinal) || (dots >= 1 && !text.Contains(' ', StringComparison.Ordinal)) || dots >= 3)
        {
            text = DotOrUnderscorePattern().Replace(text, " ");
        }

        text = BracketYearPattern().Replace(text, " (${y}) ");
        text = BracketTagPattern().Replace(text, " ");
        return SpacesPattern().Replace(text, " ").Trim(' ', '-', '_', '.');
    }

    private static string Before(string name, Match m) => CutAtTags(name[..m.Index]);

    private static string After(string name, Match m) => CutAtTags(name[(m.Index + m.Length)..]);

    private static int? Int(Group group) =>
        group.Success && int.TryParse(group.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string FileNameWithoutExtension(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>Folder names from the library root down to the file, root excluded.</summary>
    private static List<string> FoldersBetween(string path, string root)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).ToList();
        var rootParts = root.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var skip = 0;
        while (skip < rootParts.Length && skip < parts.Count - 1 && string.Equals(parts[skip], rootParts[skip], StringComparison.OrdinalIgnoreCase))
        {
            skip++;
        }

        return skip == rootParts.Length ? parts.Skip(skip).Take(parts.Count - skip - 1).ToList() : (parts.Count >= 2 ? [parts[^2]] : []);
    }

    // ===== Patterns =====

    [GeneratedRegex(@"(?<![a-z0-9])s(?<s>\d{1,2})[ ._-]?e(?<e>\d{1,3})(?:[-_ ]?e\d{1,3})*(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodePattern();

    [GeneratedRegex(@"(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{2,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex CrossPattern();

    [GeneratedRegex(@"season\s*(?<s>\d{1,2})\s*[,.\-]?\s*episode\s*(?<e>\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex WordsPattern();

    [GeneratedRegex(@"^(?:season|series|s)\s*(?<s>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderPattern();

    [GeneratedRegex(@"^(?:e|ep|episode\s*)?(?<e>\d{1,3})(?![0-9])(?:\s*[-.]\s*|\s+|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingEpisodePattern();

    [GeneratedRegex(@"(?<![a-z])(?:episode|ep)\s*\.?\s*(?<e>\d{1,4})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWordPattern();

    [GeneratedRegex(@"\s-\s(?<e>\d{1,3})(?:v\d)?(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DashNumberPattern();

    [GeneratedRegex(@"(?:^|[\s(\[])\(?(?<y>(?:19|20)\d{2})\)?(?=[\s)\].]|$)")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"(?<y>(?:19|20)\d{2})[-_. ](?<m>\d{2})[-_. ](?<d>\d{2})(?![0-9])")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<![0-9])(?<y>(?:19|20)\d{2})(?<m>\d{2})(?<d>\d{2})(?:[_-]\d{4,9})?(?![0-9])")]
    private static partial Regex CompactDatePattern();

    [GeneratedRegex(@"^(?<p>[A-Za-z]{2,5}\d{0,2}|Screen Recording)[_ -]", RegexOptions.IgnoreCase)]
    private static partial Regex CameraPrefixPattern();

    [GeneratedRegex(@"(?<![a-z0-9])(?:2160p|1080[pi]|720p|576p|480p|4k|uhd|hdr10?\+?|dv|dolby\s?vision|blu-?ray|bdrip|brrip|web-?dl|web-?rip|webrip|hdtv|dvd-?rip|dvd|remux|proper|repack|extended|unrated|x26[45]|h\.?26[45]|hevc|avc|xvid|divx|aac(?:2\.0)?|ac3|eac3|ddp?5\.1|dts(?:-hd)?|truehd|atmos|10bit|8bit|imax|multi|subbed|dubbed)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)+")]
    private static partial Regex LeadingGroupPattern();

    [GeneratedRegex(@"\[(?<y>(?:19|20)\d{2})\]")]
    private static partial Regex BracketYearPattern();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketTagPattern();

    [GeneratedRegex(@"(?<=[^\s])[._](?=[^\s])")]
    private static partial Regex DotOrUnderscorePattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesPattern();
}
