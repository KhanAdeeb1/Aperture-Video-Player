using System.Text.RegularExpressions;

namespace Aperture.Core.Media;

/// <summary>A subtitle file found for a video.</summary>
/// <param name="Path">The subtitle file.</param>
/// <param name="Language">ISO 639-2 code read from the file name ("eng"), or null when it doesn't say.</param>
/// <param name="IsForced">The name marks it as forced (only foreign-language lines).</param>
/// <param name="IsHearingImpaired">The name marks it as SDH or closed captions.</param>
public sealed record SubtitleMatch(string Path, string? Language, bool IsForced, bool IsHearingImpaired)
{
    /// <summary>A short title for the track menu: "SDH", or null.</summary>
    public string? Title => IsHearingImpaired ? "SDH" : null;
}

/// <summary>
/// Finds subtitle files that belong to a video, beyond the exact "Film.srt" next to "Film.mkv":
/// files named like the video with a language ("Film.English.forced.srt"), the episode's own
/// file in a season folder ("Show.S01E02.srt" for "Show.S01E02.1080p.mkv"), any subtitle beside
/// the only video in its folder, and the "Subs" folders that downloaded releases use, including
/// one subfolder per episode ("Subs/Show.S01E02/2_English.srt"). Folders are only listed, never
/// changed, and only the video's own folder and its subtitle folders are looked at.
/// </summary>
public static partial class SubtitleFinder
{
    private static readonly string[] SubtitleFolders = ["Subs", "Sub", "Subtitles", "Subtitle"];

    // Large folders are read only up to this many entries, so a video in a folder of thousands
    // of files doesn't delay playback.
    private const int MaximumEntries = 2000;

    // A folder of unrelated subtitles beside one video shouldn't fill the menu.
    private const int MaximumMatches = 24;

    private static readonly Dictionary<string, string> Languages = BuildLanguages();

    /// <summary>
    /// Subtitle files for <paramref name="videoPath"/>, those beside the video first, each once.
    /// Never throws for folders that can't be read; they are skipped.
    /// </summary>
    public static IReadOnlyList<SubtitleMatch> Find(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var folder = System.IO.Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(folder))
        {
            return [];
        }

        var baseName = System.IO.Path.GetFileNameWithoutExtension(videoPath);
        var entries = List(folder);
        var videos = entries.Where(MediaFileTypes.IsVideoFile).ToList();
        var onlyVideo = videos.Count == 1;
        var episode = EpisodeCode(baseName);

        var found = new List<SubtitleMatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string remainder)
        {
            if (found.Count < MaximumMatches && seen.Add(path) && !IsVobSubData(path))
            {
                found.Add(Describe(path, remainder));
            }
        }

        foreach (var file in entries.Where(MediaFileTypes.IsSubtitleFile).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Matches(baseName, episode, file, onlyVideo, out var remainder))
            {
                Add(file, remainder);
            }
        }

        foreach (var subFolder in Directories(folder).Where(d => SubtitleFolders.Contains(System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
        {
            foreach (var file in List(subFolder).Where(MediaFileTypes.IsSubtitleFile).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Matches(baseName, episode, file, onlyVideo, out var remainder))
                {
                    Add(file, remainder);
                }
            }

            // One folder per video, named exactly like it: everything inside belongs to it.
            foreach (var own in Directories(subFolder).Where(d => System.IO.Path.GetFileName(d).Equals(baseName, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var file in List(own).Where(MediaFileTypes.IsSubtitleFile).Order(StringComparer.OrdinalIgnoreCase))
                {
                    Add(file, System.IO.Path.GetFileNameWithoutExtension(file));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// True when <paramref name="subtitlePath"/> belongs to the video called <paramref name="videoBase"/>;
    /// <paramref name="remainder"/> is the part of its name that may hold a language.
    /// </summary>
    internal static bool Matches(string videoBase, string? episode, string subtitlePath, bool onlyVideo, out string remainder)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(subtitlePath);
        remainder = string.Empty;

        // "Film.srt", "Film.en.srt", "Film English.srt": the video's name, then a separator.
        if (name.StartsWith(videoBase, StringComparison.OrdinalIgnoreCase)
            && (name.Length == videoBase.Length || IsSeparator(name[videoBase.Length])))
        {
            remainder = name[videoBase.Length..];
            return true;
        }

        // A shorter name the video's starts with: "Show.S01E02.srt" for "Show.S01E02.1080p.WEB.mkv".
        // Language words at the end are set aside first ("Film.2019.eng.srt" for "Film.2019.1080p.mkv").
        var (core, languagePart) = SplitLanguageSuffix(name);
        // For an episode, the shorter name must still name the episode ("Show.srt" is not enough).
        if (core.Length >= 3
            && (episode is null || EpisodeCode(core) == episode)
            && videoBase.StartsWith(core, StringComparison.OrdinalIgnoreCase)
            && (videoBase.Length == core.Length || IsSeparator(videoBase[core.Length])))
        {
            remainder = languagePart;
            return true;
        }

        // In a season folder the episode code decides; beside the only video, anything does.
        var subtitleEpisode = EpisodeCode(name);
        if (episode is not null && subtitleEpisode is not null)
        {
            remainder = name;
            return episode == subtitleEpisode;
        }

        if (onlyVideo && subtitleEpisode is null)
        {
            remainder = name;
            return true;
        }

        return false;
    }

    /// <summary>Reads language, forced and SDH marks from the words of a name.</summary>
    internal static SubtitleMatch Describe(string path, string words)
    {
        string? language = null;
        var forced = false;
        var hearingImpaired = false;
        foreach (var word in Words(words))
        {
            if (word is "forced" or "foreign")
            {
                forced = true;
            }
            else if (word is "sdh" or "cc" or "hoh")
            {
                hearingImpaired = true;
            }
            else if (language is null && Languages.TryGetValue(word, out var code))
            {
                language = code;
            }
        }

        return new SubtitleMatch(path, language, forced, hearingImpaired);
    }

    /// <summary>"S01E02" (normalised) from "Show.s1e2" or "Show 1x02", or null.</summary>
    internal static string? EpisodeCode(string name)
    {
        var match = EpisodePattern().Match(name);
        if (!match.Success)
        {
            return null;
        }

        var season = int.Parse(match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["s2"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var number = int.Parse(match.Groups["e"].Success ? match.Groups["e"].Value : match.Groups["e2"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return FormattableString.Invariant($"S{season:00}E{number:00}");
    }

    private static (string Core, string Suffix) SplitLanguageSuffix(string name)
    {
        var end = name.Length;
        while (end > 0)
        {
            var start = end;
            while (start > 0 && !IsSeparator(name[start - 1]))
            {
                start--;
            }

            var word = name[start..end].ToLowerInvariant();
            if (start == 0 || !(Languages.ContainsKey(word) || word is "forced" or "sdh" or "cc" or "hoh"))
            {
                break;
            }

            end = start - 1;
        }

        return (name[..Math.Max(0, end)], name[Math.Min(name.Length, end)..]);
    }

    private static IEnumerable<string> Words(string text) =>
        text.Split(['.', '_', '-', ' ', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant());

    private static bool IsSeparator(char c) => c is '.' or '_' or '-' or ' ' or '[' or '(';

    // A VobSub pair is opened through its .idx; the .sub beside it is the picture data.
    private static bool IsVobSubData(string path) =>
        System.IO.Path.GetExtension(path).Equals(".sub", StringComparison.OrdinalIgnoreCase)
        && File.Exists(System.IO.Path.ChangeExtension(path, ".idx"));

    private static List<string> List(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder).Take(MaximumEntries)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> Directories(string folder)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(folder).Take(MaximumEntries)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Dictionary<string, string> BuildLanguages()
    {
        // Names and codes as release groups write them. "hi" is left out: it means both Hindi
        // and "hearing impaired" in subtitle names.
        var table = new (string Code, string[] Words)[]
        {
            ("eng", ["en", "eng", "english"]), ("fre", ["fr", "fre", "fra", "french", "francais", "français"]),
            ("ger", ["de", "ger", "deu", "german", "deutsch"]), ("spa", ["es", "spa", "spanish", "espanol", "español", "castellano"]),
            ("ita", ["it", "ita", "italian", "italiano"]), ("por", ["pt", "por", "portuguese", "portugues", "brazilian", "ptbr"]),
            ("dut", ["nl", "dut", "nld", "dutch", "nederlands"]), ("jpn", ["ja", "jp", "jpn", "japanese"]),
            ("kor", ["ko", "kor", "korean"]), ("chi", ["zh", "chi", "zho", "chs", "cht", "chinese", "mandarin"]),
            ("rus", ["ru", "rus", "russian"]), ("ara", ["ar", "ara", "arabic"]), ("hin", ["hin", "hindi"]),
            ("swe", ["sv", "swe", "swedish"]), ("dan", ["da", "dan", "danish"]), ("nor", ["no", "nor", "nob", "norwegian"]),
            ("fin", ["fi", "fin", "finnish"]), ("pol", ["pl", "pol", "polish"]), ("tur", ["tr", "tur", "turkish"]),
            ("heb", ["he", "heb", "hebrew"]), ("gre", ["el", "gre", "ell", "greek"]), ("cze", ["cs", "cze", "ces", "czech"]),
            ("hun", ["hu", "hun", "hungarian"]), ("rum", ["ro", "rum", "ron", "romanian"]), ("ukr", ["uk", "ukr", "ukrainian"]),
            ("vie", ["vi", "vie", "vietnamese"]), ("tha", ["th", "tha", "thai"]), ("ind", ["id", "ind", "indonesian"]),
            ("may", ["ms", "may", "msa", "malay"]), ("urd", ["ur", "urd", "urdu"]), ("ben", ["bn", "ben", "bengali"]),
            ("tam", ["ta", "tam", "tamil"]), ("per", ["fa", "per", "fas", "persian", "farsi"]),
        };
        var languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, words) in table)
        {
            foreach (var word in words)
            {
                languages[word] = code;
            }
        }

        return languages;
    }

    [GeneratedRegex(@"(?<![a-z0-9])(?:s(?<s>\d{1,2})[ ._-]?e(?<e>\d{1,3})|(?<s2>\d{1,2})x(?<e2>\d{2,3}))(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodePattern();
}
