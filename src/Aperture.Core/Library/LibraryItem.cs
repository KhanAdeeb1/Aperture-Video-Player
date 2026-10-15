namespace Aperture.Core.Library;

/// <summary>Where an item's title and details came from.</summary>
public enum MetadataSource
{
    /// <summary>Read from the file and folder names.</summary>
    FileName,

    /// <summary>Matched with an online service the user turned on.</summary>
    Online,

    /// <summary>Entered or corrected by the user. Never overwritten by scans or lookups.</summary>
    Manual,
}

/// <summary>How sure Aperture is about an item's identity.</summary>
public enum MatchConfidence
{
    /// <summary>A guess; the user should check it.</summary>
    Uncertain,

    /// <summary>Probably right.</summary>
    Likely,

    /// <summary>Unambiguous, or confirmed by the user.</summary>
    Certain,
}

/// <summary>A folder the user added to the library. Aperture only ever reads inside it.</summary>
/// <param name="Id">Database id.</param>
/// <param name="Path">Full path of the folder.</param>
/// <param name="Kind">What the user said it holds.</param>
/// <param name="AddedUtc">When it was added.</param>
/// <param name="LastScanStartedUtc">When the last scan started, or null if never scanned.</param>
/// <param name="LastScanCompletedUtc">When the last scan finished all the way, or null.</param>
/// <param name="ItemCount">How many items it currently has in the library.</param>
public sealed record LibraryFolder(
    long Id,
    string Path,
    LibraryFolderKind Kind,
    DateTimeOffset AddedUtc,
    DateTimeOffset? LastScanStartedUtc,
    DateTimeOffset? LastScanCompletedUtc,
    int ItemCount = 0)
{
    /// <summary>True when a scan was started but never finished (cancelled, or Aperture closed).</summary>
    public bool ScanIncomplete => LastScanStartedUtc is { } started && (LastScanCompletedUtc is not { } done || done < started);
}

/// <summary>
/// Online details found for an item. Stored separately from the item's own title so an
/// uncertain match can be offered to the user without replacing what they see.
/// </summary>
/// <param name="Service">The service it came from, for example "TMDB".</param>
/// <param name="ServiceId">The service's identifier, for example "movie/603".</param>
/// <param name="Title">The title the service gives.</param>
/// <param name="Year">The release or first-air year.</param>
/// <param name="EpisodeTitle">The episode's own title, for episodes.</param>
/// <param name="Overview">A short description.</param>
/// <param name="ArtworkUrl">A 16:9 image (backdrop or episode still), or null.</param>
/// <param name="Confidence">How well it matched the file name.</param>
public sealed record OnlineMatch(
    string Service,
    string ServiceId,
    string Title,
    int? Year,
    string? EpisodeTitle,
    string? Overview,
    string? ArtworkUrl,
    MatchConfidence Confidence);

/// <summary>One file in the library: what it is, what is known about the file, and where the details came from.</summary>
public sealed record LibraryItem
{
    public long Id { get; init; }

    /// <summary>Full path of the media file.</summary>
    public required string Path { get; init; }

    public long FolderId { get; init; }

    public MediaCategory Category { get; init; }

    /// <summary>Movie title, show name for episodes, or the video's name.</summary>
    public required string Title { get; init; }

    public int? Year { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    /// <summary>The last episode in a file that holds several ("S01E01E02"), or null for one episode.</summary>
    public int? EpisodeEnd { get; init; }

    /// <summary>The episode's own title, when known.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>The recording date of a personal video or podcast episode, when the name carries one.</summary>
    public DateOnly? Date { get; init; }

    public string? Overview { get; init; }

    public long SizeBytes { get; init; }

    public DateTimeOffset ModifiedUtc { get; init; }

    public DateTimeOffset AddedUtc { get; init; }

    /// <summary>Length, or null until the file has been inspected.</summary>
    public TimeSpan? Duration { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? VideoCodec { get; init; }

    /// <summary>A hash of the file's size, start and end, for spotting identical copies. Null until inspected.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>True once the file has been inspected (even if inspection found nothing).</summary>
    public bool Probed { get; init; }

    public MetadataSource Source { get; init; }

    public MatchConfidence Confidence { get; init; }

    /// <summary>An online match that was found but not applied, because it was uncertain.</summary>
    public OnlineMatch? SuggestedMatch { get; init; }

    /// <summary>Local path of cached artwork, or null.</summary>
    public string? ArtworkPath { get; init; }

    /// <summary>True when the last full scan of its folder did not find the file.</summary>
    public bool IsMissing { get; init; }

    /// <summary>A short line under the title: "S1 E3 · Pilot", "2019", "14 Jul 2024".</summary>
    public string? Subtitle => Category switch
    {
        MediaCategory.Episode when Season is { } s && Episode is { } e =>
            EpisodeTitle is { Length: > 0 } name ? $"{EpisodeLabel(s, e, EpisodeEnd)} · {name}" : EpisodeLabel(s, e, EpisodeEnd),
        MediaCategory.Episode when Episode is { } e => EpisodeTitle is { Length: > 0 } name ? $"E{e} · {name}" : $"Episode {e}",
        MediaCategory.Podcast when Episode is { } e => EpisodeTitle is { Length: > 0 } name ? $"Episode {e} · {name}" : $"Episode {e}",
        MediaCategory.Movie when Year is { } y => y.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ when Date is { } d => d.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
        _ => EpisodeTitle,
    };

    /// <summary>"4K", "1080p", "720p", "SD", or null when the picture size is unknown.</summary>
    public string? Quality => MediaQuality.Label(Width, Height);

    /// <summary>Average bitrate in bits per second, or zero when unknown.</summary>
    public double Bitrate => Duration is { TotalSeconds: > 1 } d ? SizeBytes * 8 / d.TotalSeconds : 0;

    /// <summary>Key shared by copies of the same film or episode, or null when the item has no identity beyond its file.</summary>
    public string? ContentKey => Category switch
    {
        MediaCategory.Movie when Confidence != MatchConfidence.Uncertain => $"movie|{Normalize(Title)}|{Year}",
        // The year keeps two shows of the same name apart ("Doctor Who (1963)" and "(2005)").
        MediaCategory.Episode when Season is { } s && Episode is { } e => EpisodeEnd is { } end ? $"episode|{Normalize(Title)}|{Year}|{s}|{e}-{end}" : $"episode|{Normalize(Title)}|{Year}|{s}|{e}",
        _ => null,
    };

    /// <summary>"S1 E3", or "S1 E3–4" for a file holding two episodes; "Special 2" for season 0.</summary>
    public static string EpisodeLabel(int season, int episode, int? episodeEnd = null)
    {
        var number = episodeEnd is { } end && end > episode ? $"{episode}–{end}" : episode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return season == 0 ? $"Special {number}" : $"S{season} E{number}";
    }

    /// <summary>Lower-case letters and digits only, for comparing titles.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.Normalize(System.Text.NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
