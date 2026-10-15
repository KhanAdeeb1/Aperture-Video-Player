namespace Aperture.Core.YouTube;

/// <summary>Whether a video is live, waiting to go live, or an ordinary upload.</summary>
public enum YouTubeLiveState
{
    None,
    Live,
    Upcoming,
}

/// <summary>
/// A video as YouTube describes it. Every field except <see cref="Id"/> is YouTube's data and
/// is kept only as long as its developer policies allow (see <see cref="YouTubeDataRules"/>).
/// </summary>
/// <param name="Id">The video id.</param>
/// <param name="Title">The title, exactly as YouTube gives it.</param>
/// <param name="Channel">The uploading channel's name.</param>
/// <param name="ThumbnailUrl">A 16:9 thumbnail on YouTube's image host.</param>
/// <param name="Duration">The length, when known (not for live streams).</param>
/// <param name="Status">What YouTube says about playing the video here, when it was checked.</param>
public sealed record YouTubeVideo(
    string Id,
    string Title,
    string Channel,
    string? ThumbnailUrl,
    TimeSpan? Duration,
    YouTubeVideoStatus? Status = null)
{
    /// <summary>YouTube's own 16:9 thumbnail for a video id (320 × 180), used when no better one is known.</summary>
    public static string DefaultThumbnail(string videoId) => $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
}

/// <summary>The parts of YouTube's video record that decide whether the embedded player can play it.</summary>
/// <param name="Embeddable">False when the owner turned off playing on other sites and apps.</param>
/// <param name="PrivacyStatus">"public", "unlisted" or "private".</param>
/// <param name="AgeRestricted">True when YouTube marked it age-restricted (embeds can't play these).</param>
/// <param name="AllowedRegions">When set, the only regions where it plays.</param>
/// <param name="BlockedRegions">Regions where it doesn't play.</param>
/// <param name="Live">Live or upcoming broadcasts.</param>
/// <param name="UploadStatus">"processed" for an ordinary finished upload.</param>
public sealed record YouTubeVideoStatus(
    bool Embeddable,
    string PrivacyStatus,
    bool AgeRestricted,
    IReadOnlyList<string>? AllowedRegions,
    IReadOnlyList<string>? BlockedRegions,
    YouTubeLiveState Live,
    string? UploadStatus);
