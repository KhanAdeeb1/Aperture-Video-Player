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

    private static readonly HashSet<string> AudioExtensionSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".opus", ".ogg", ".oga", ".flac", ".wav", ".wma",
    };

    private static readonly HashSet<string> SubtitleExtensionSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi",
    };

    /// <summary>Video extensions, lower case with a leading dot, in a stable order.</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } = [.. VideoExtensionSet.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Audio extensions, lower case with a leading dot. Aperture plays these for podcasts; the
    /// open dialog still offers video files only.
    /// </summary>
    public static IReadOnlyList<string> AudioExtensions { get; } = [.. AudioExtensionSet.Order(StringComparer.Ordinal)];

    /// <summary>Subtitle file extensions Aperture can load next to a video, in a stable order.</summary>
    public static IReadOnlyList<string> SubtitleExtensions { get; } = [.. SubtitleExtensionSet.Order(StringComparer.Ordinal)];

    /// <summary>True when the path has a subtitle extension Aperture recognises.</summary>
    public static bool IsSubtitleFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return SubtitleExtensionSet.Contains(Path.GetExtension(path));
    }

    /// <summary>True when the path has a video extension Aperture recognises.</summary>
    public static bool IsVideoFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return VideoExtensionSet.Contains(Path.GetExtension(path));
    }

    /// <summary>True when the path has an audio extension Aperture recognises.</summary>
    public static bool IsAudioFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return AudioExtensionSet.Contains(Path.GetExtension(path));
    }

    /// <summary>True for anything the player opens: video, or audio (podcasts).</summary>
    public static bool IsPlayableFile(string path) => IsVideoFile(path) || IsAudioFile(path);

    /// <summary>The title shown for a file: its name without the extension.</summary>
    public static string TitleFromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var title = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title;
    }
}
