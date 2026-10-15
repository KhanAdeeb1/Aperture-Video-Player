using Aperture.Core.Library;
using Aperture.Core.Playback;
using Microsoft.UI.Xaml.Media;

namespace Aperture.App.Views;

/// <summary>
/// What a <see cref="MediaCard"/> shows: a played file from history, or a library item
/// (with its copies, watched state and where its details came from).
/// </summary>
internal sealed class MediaCardModel
{
    private MediaCardModel(string path, string title, string meta, double? progress, HistoryEntry? history, LibraryEntry? library)
    {
        Path = path;
        Title = title;
        Meta = meta;
        Progress = progress;
        History = history;
        Library = library;
    }

    /// <summary>The file played when the card is invoked (the best copy for a library item).</summary>
    public string Path { get; }

    public string Title { get; }

    /// <summary>One line of context: "12 min left", "S1 E3 · Pilot · 1080p", "42 min · Yesterday".</summary>
    public string Meta { get; }

    /// <summary>Progress from 0 to 1, or null when the card should not show progress.</summary>
    public double? Progress { get; }

    /// <summary>Playback history for the file, when it has been played.</summary>
    public HistoryEntry? History { get; }

    /// <summary>The library item and its copies, when the file is in the library.</summary>
    public LibraryEntry? Library { get; }

    /// <summary>Artwork downloaded for the item, if any (otherwise Windows' own thumbnail is used).</summary>
    public string? ArtworkPath => Library?.Item.ArtworkPath;

    /// <summary>True when the details came from a guess the user should confirm.</summary>
    public bool NeedsCheck => Library?.Item is { } item && item.Source != MetadataSource.Manual
        && (item.SuggestedMatch is not null || item.Confidence == MatchConfidence.Uncertain);

    public bool IsWatched => Library?.State == WatchState.Watched;

    /// <summary>The name Narrator reads: title, context, then state.</summary>
    public string AccessibleName =>
        $"{Title}, {Meta}{(IsWatched ? ", watched" : string.Empty)}{(NeedsCheck ? ", details need checking" : string.Empty)}";

    public ImageSource? Artwork { get; set; }

    /// <summary>A played file from history.</summary>
    public static MediaCardModel FromHistory(HistoryEntry entry, bool showProgress, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var meta = showProgress
            ? TimeFormat.Remaining(entry.Remaining)
            : $"{TimeFormat.Length(entry.Duration)} · {RelativeDate.Describe(entry.LastPlayedUtc, now)}";
        return new MediaCardModel(entry.Path, entry.Title, meta, showProgress ? entry.Progress : null, entry, null);
    }

    /// <summary>A library item. Progress shows only while it is part-watched.</summary>
    public static MediaCardModel FromLibrary(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var item = entry.Item;
        var parts = new List<string>(3);
        if (item.Subtitle is { Length: > 0 } subtitle)
        {
            parts.Add(subtitle);
        }

        if (entry.State == WatchState.InProgress && entry.History is { } played)
        {
            parts.Add(TimeFormat.Remaining(played.Remaining));
        }
        else if (item.Duration is { } length && length > TimeSpan.Zero)
        {
            parts.Add(TimeFormat.Length(length));
        }

        if (item.Quality is { } quality)
        {
            parts.Add(quality);
        }

        if (entry.Group.HasCopies)
        {
            parts.Add(entry.Group.Others.Count == 1 ? "2 copies" : $"{entry.Group.Others.Count + 1} copies");
        }

        var meta = parts.Count > 0 ? string.Join(" · ", parts) : MediaCategories.SingularLabel(item.Category);
        double? progress = entry.State == WatchState.InProgress ? entry.History?.Progress : null;
        return new MediaCardModel(item.Path, item.Title, meta, progress, entry.History, entry);
    }
}
