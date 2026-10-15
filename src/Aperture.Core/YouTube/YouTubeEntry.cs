namespace Aperture.Core.YouTube;

/// <summary>
/// Aperture's own record of a YouTube video the user watched in Aperture or saved for later.
/// The position, times and saved flag are Aperture's; <see cref="Video"/> is YouTube's data,
/// cached under the 30-day rule (null once it has been removed and not yet refreshed).
/// </summary>
/// <param name="VideoId">The video id.</param>
/// <param name="Video">YouTube's details, when held.</param>
/// <param name="Position">Where the user stopped watching in Aperture.</param>
/// <param name="Duration">Length reported by the player, or zero when unknown.</param>
/// <param name="WatchedUtc">When it was last played in Aperture, or null if only saved.</param>
/// <param name="SavedUtc">When it was saved for later, or null.</param>
/// <param name="DetailsUtc">When YouTube's details were fetched, or null.</param>
public sealed record YouTubeEntry(
    string VideoId,
    YouTubeVideo? Video,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset? WatchedUtc,
    DateTimeOffset? SavedUtc,
    DateTimeOffset? DetailsUtc)
{
    /// <summary>Fraction watched, 0 to 1.</summary>
    public double Progress => Duration > TimeSpan.Zero ? Math.Clamp(Position / Duration, 0, 1) : 0;

    /// <summary>Time left to watch.</summary>
    public TimeSpan Remaining => Duration > Position ? Duration - Position : TimeSpan.Zero;

    /// <summary>The title, or a neutral label while YouTube's details aren't held.</summary>
    public string Title => Video?.Title is { Length: > 0 } title ? title : "YouTube video";

    /// <summary>The thumbnail to show (YouTube's own, derived from the id when no details are held).</summary>
    public string ThumbnailUrl => Video?.ThumbnailUrl ?? YouTubeVideo.DefaultThumbnail(VideoId);
}
