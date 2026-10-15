namespace Aperture.Core.Media;

/// <summary>
/// The file extensions Aperture offers in its open dialog and accepts by drag and drop.
/// Accepting an extension does not promise the codec inside is supported; the playback
/// engine makes that decision when it opens the file.
/// </summary>
public static class MediaFileTypes
{
    private static readonly HashSet<string> VideoExtensionSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".ogv",
    };

    /// <summary>Video extensions, lower case with a leading dot, in a stable order.</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } = [.. VideoExtensionSet.Order(StringComparer.Ordinal)];

    /// <summary>True when the path has a video extension Aperture recognises.</summary>
    public static bool IsVideoFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return VideoExtensionSet.Contains(Path.GetExtension(path));
    }

    /// <summary>The title shown for a file: its name without the extension.</summary>
    public static string TitleFromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var title = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title;
    }
}
