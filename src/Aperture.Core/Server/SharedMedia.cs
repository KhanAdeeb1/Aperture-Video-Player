using Aperture.Core.Library;

namespace Aperture.Core.Server;

/// <summary>
/// Rules that keep the media server inside the folders the user shared. Devices only ever
/// name library items by id; this decides whether an item may be listed or streamed.
/// </summary>
public static class SharedMedia
{
    /// <summary>
    /// True when <paramref name="item"/> belongs to a shared folder, its path lies inside that
    /// folder (with no "." or ".." segments that could lead out), and the last scan found it.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="sharedFolders">Shared folder ids and their paths.</param>
    public static bool IsShared(LibraryItem item, IReadOnlyDictionary<long, string> sharedFolders)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sharedFolders);
        return !item.IsMissing
            && sharedFolders.TryGetValue(item.FolderId, out var folder)
            && !HasRelativeSegments(item.Path)
            && LibraryFolderRules.IsInside(item.Path, folder);
    }

    /// <summary>True when any segment of the path is "." or "..".</summary>
    public static bool HasRelativeSegments(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Split('/', '\\').Any(segment => segment is "." or "..");
    }
}

/// <summary>Turns a position reported by a device into a history entry, refusing values that make no sense.</summary>
public static class RemoteProgress
{
    /// <summary>Longest length accepted from a device when the library doesn't know the file's length.</summary>
    public static readonly TimeSpan LongestReported = TimeSpan.FromHours(24);

    /// <summary>
    /// The entry to save, or null when the report is unusable (not a number, negative, or no
    /// known length). The library's length wins over the device's; the position is clamped to it.
    /// </summary>
    public static HistoryEntry? ToEntry(LibraryItem item, double positionSeconds, double? reportedDurationSeconds, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
        {
            return null;
        }

        var duration = item.Duration is { } known && known > TimeSpan.Zero
            ? known
            : reportedDurationSeconds is { } r && double.IsFinite(r) && r > 0 && r <= LongestReported.TotalSeconds
                ? TimeSpan.FromSeconds(r)
                : TimeSpan.Zero;
        if (duration <= TimeSpan.Zero)
        {
            return null;
        }

        var position = TimeSpan.FromSeconds(Math.Min(positionSeconds, duration.TotalSeconds));
        return new HistoryEntry(item.Path, Media.MediaFileTypes.TitleFromPath(item.Path), position, duration, nowUtc);
    }
}
