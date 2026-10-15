namespace Aperture.Core.YouTube;

/// <summary>Aperture's records of YouTube videos watched in Aperture or saved for later.</summary>
public interface IYouTubeStore
{
    /// <summary>Every entry (the list is small: only videos played or saved in Aperture).</summary>
    Task<IReadOnlyList<YouTubeEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>One entry, or null.</summary>
    Task<YouTubeEntry?> GetAsync(string videoId, CancellationToken cancellationToken = default);

    /// <summary>Records where playback is now, creating the entry if needed.</summary>
    Task RecordProgressAsync(string videoId, TimeSpan position, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Stores YouTube's details for a video (only for videos that have an entry or are being saved).</summary>
    Task SetDetailsAsync(YouTubeVideo video, DateTimeOffset fetchedUtc, CancellationToken cancellationToken = default);

    /// <summary>Removes YouTube's details for these videos, keeping Aperture's own record.</summary>
    Task ClearDetailsAsync(IEnumerable<string> videoIds, CancellationToken cancellationToken = default);

    /// <summary>Saves a video for later (with its details) or removes it from Saved.</summary>
    Task SetSavedAsync(string videoId, DateTimeOffset? savedUtc, CancellationToken cancellationToken = default);

    /// <summary>Removes a video from Continue Watching (its position is forgotten; Saved is kept).</summary>
    Task ForgetProgressAsync(string videoId, CancellationToken cancellationToken = default);

    /// <summary>Deletes every YouTube record Aperture holds.</summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
