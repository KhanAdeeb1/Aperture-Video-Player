using Aperture.Core.Library;
using Aperture.Core.Playback;
using Microsoft.UI.Xaml.Media;

namespace Aperture.App.Views;

/// <summary>What a <see cref="MediaCard"/> shows for one history entry.</summary>
internal sealed class MediaCardModel(HistoryEntry entry, bool showProgress, DateTimeOffset now)
{
    public HistoryEntry Entry { get; } = entry;

    public string Title => Entry.Title;

    /// <summary>Progress from 0 to 1, or null when the card should not show progress.</summary>
    public double? Progress { get; } = showProgress ? Entry.Progress : null;

    /// <summary>"12 min left" on Continue Watching; "42 min · Yesterday" elsewhere.</summary>
    public string Meta { get; } = showProgress
        ? TimeFormat.Remaining(entry.Remaining)
        : $"{TimeFormat.Length(entry.Duration)} · {RelativeDate.Describe(entry.LastPlayedUtc, now)}";

    /// <summary>The name Narrator reads: title, then the context line.</summary>
    public string AccessibleName => $"{Title}, {Meta}";

    public ImageSource? Artwork { get; set; }
}
