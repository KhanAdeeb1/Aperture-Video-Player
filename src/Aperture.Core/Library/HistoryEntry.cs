namespace Aperture.Core.Library;

/// <summary>
/// What Aperture remembers about a file it has played. The media file itself is never
/// modified; this is Aperture's own record, keyed by the file's full path.
/// </summary>
/// <param name="Path">Full path of the media file.</param>
/// <param name="Title">Display title.</param>
/// <param name="Position">Last playback position.</param>
/// <param name="Duration">Media duration, or <see cref="TimeSpan.Zero"/> when unknown.</param>
/// <param name="LastPlayedUtc">When the file was last played.</param>
public sealed record HistoryEntry(
    string Path,
    string Title,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset LastPlayedUtc)
{
    /// <summary>Fraction watched, from 0 to 1. Zero when the duration is unknown.</summary>
    public double Progress =>
        Duration > TimeSpan.Zero ? Math.Clamp(Position / Duration, 0, 1) : 0;

    /// <summary>Time left to watch.</summary>
    public TimeSpan Remaining =>
        Duration > Position ? Duration - Position : TimeSpan.Zero;
}
