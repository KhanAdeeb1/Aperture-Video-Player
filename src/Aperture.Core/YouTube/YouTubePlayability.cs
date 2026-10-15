namespace Aperture.Core.YouTube;

/// <summary>Why a YouTube video can or cannot play inside Aperture.</summary>
public enum YouTubePlayability
{
    Playable,
    NotFound,
    Private,
    NotEmbeddable,
    AgeRestricted,
    RegionBlocked,
    Upcoming,
    Processing,
    PlayerProblem,
    ClientIdentityRejected,
}

/// <summary>
/// Decides, from YouTube's own data or the player's error codes, whether a video can play in
/// Aperture's embedded player, and words the reason for people. Aperture never tries to work
/// around any of these; it offers to open the video on YouTube instead.
/// </summary>
public static class YouTubePlayabilityRules
{
    /// <summary>What YouTube's video record says about playing it here, for a viewer in <paramref name="region"/> (ISO 3166-1 alpha-2, or null when unknown).</summary>
    public static YouTubePlayability Evaluate(YouTubeVideoStatus? status, string? region)
    {
        if (status is null)
        {
            return YouTubePlayability.Playable;
        }

        if (string.Equals(status.PrivacyStatus, "private", StringComparison.OrdinalIgnoreCase))
        {
            return YouTubePlayability.Private;
        }

        if (!status.Embeddable)
        {
            return YouTubePlayability.NotEmbeddable;
        }

        if (status.AgeRestricted)
        {
            return YouTubePlayability.AgeRestricted;
        }

        if (region is { Length: > 0 }
            && ((status.AllowedRegions is { } allowed && !allowed.Contains(region, StringComparer.OrdinalIgnoreCase))
                || (status.BlockedRegions is { } blocked && blocked.Contains(region, StringComparer.OrdinalIgnoreCase))))
        {
            return YouTubePlayability.RegionBlocked;
        }

        if (status.Live == YouTubeLiveState.Upcoming)
        {
            return YouTubePlayability.Upcoming;
        }

        if (status.UploadStatus is { Length: > 0 } upload && !upload.Equals("processed", StringComparison.OrdinalIgnoreCase))
        {
            return YouTubePlayability.Processing;
        }

        return YouTubePlayability.Playable;
    }

    /// <summary>
    /// Maps the IFrame Player API's <c>onError</c> codes
    /// (https://developers.google.com/youtube/iframe_api_reference#onError).
    /// </summary>
    public static YouTubePlayability FromPlayerError(int code) => code switch
    {
        100 => YouTubePlayability.NotFound,
        101 or 150 => YouTubePlayability.NotEmbeddable,
        153 => YouTubePlayability.ClientIdentityRejected,
        _ => YouTubePlayability.PlayerProblem,
    };

    /// <summary>A heading and one sentence for the player screen.</summary>
    public static (string Title, string Detail) Describe(YouTubePlayability reason) => reason switch
    {
        YouTubePlayability.NotFound => ("This video isn’t available", "It may have been removed, made private, or the link may be incomplete."),
        YouTubePlayability.Private => ("This video is private", "Only people its owner chose can watch it, and only on YouTube."),
        YouTubePlayability.NotEmbeddable => ("This video only plays on YouTube", "YouTube didn’t let it play in other apps, usually because its owner turned that off. You can watch it on YouTube instead."),
        YouTubePlayability.AgeRestricted => ("This video is age-restricted", "YouTube only plays age-restricted videos on its own site, after checking your age."),
        YouTubePlayability.RegionBlocked => ("Not available in your region", "Its owner or YouTube has limited where it can be watched."),
        YouTubePlayability.Upcoming => ("This stream hasn’t started", "It’s a scheduled live stream or premiere. Try again when it begins, or open it on YouTube for a reminder."),
        YouTubePlayability.Processing => ("This video is still processing", "YouTube hasn’t finished preparing it. Try again later."),
        YouTubePlayability.ClientIdentityRejected => ("YouTube didn’t accept the player", "YouTube requires players in apps to identify themselves and refused this one. Details are in Aperture’s log."),
        YouTubePlayability.PlayerProblem => ("The video couldn’t be played", "YouTube’s player reported a problem. Check your connection, or open the video on YouTube."),
        _ => (string.Empty, string.Empty),
    };
}
