using Aperture.Core.Settings;
using Aperture.Core.YouTube;

namespace Aperture.YouTube;

/// <summary>A video's details and whether it can play here, from the best source available.</summary>
public sealed record YouTubeLookup(YouTubeVideo? Video, YouTubePlayability Playability);

/// <summary>
/// Everything the YouTube section does, in one place: browsing through the Data API when the
/// user has set up a key or signed in, link lookups through oEmbed when not, Aperture's own
/// Continue Watching and Saved lists, and the 30-day rule for YouTube's data. Nothing here
/// touches local playback, the library or their records.
/// </summary>
public sealed class YouTubeService(
    IYouTubeStore store,
    YouTubeDataApi api,
    YouTubeOEmbed oembed,
    YouTubeAccount account,
    ISecretStore secrets,
    TimeProvider clock,
    string? region,
    Action<string, string> log)
{
    /// <summary>The Credential Manager entry holding the user's YouTube Data API key.</summary>
    public const string ApiKeyName = "YouTube";

    public YouTubeAccount Account => account;

    /// <summary>The viewer's region (ISO 3166-1 alpha-2), used to rank results and check regional limits.</summary>
    public string? Region => region;

    public bool HasApiKey => secrets.Read(ApiKeyName) is { Length: > 0 };

    /// <summary>True when search and discovery can work (a key, or a signed-in account).</summary>
    public bool CanBrowse => HasApiKey || account.IsSignedIn;

    public void SetApiKey(string? key) => secrets.Write(ApiKeyName, key?.Trim());

    // ===== Browsing (Data API) =====

    public async Task<YouTubePage> SearchAsync(string query, string? pageToken, CancellationToken cancellationToken) =>
        await api.SearchAsync(query, pageToken, region, await CredentialsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<YouTubeVideo>> PopularAsync(CancellationToken cancellationToken) =>
        await api.PopularAsync(region, await CredentialsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<YouTubeVideo>> LikedAsync(CancellationToken cancellationToken) =>
        await api.LikedAsync(await AccountCredentialsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<YouTubePlaylist>> PlaylistsAsync(CancellationToken cancellationToken) =>
        await api.MyPlaylistsAsync(await AccountCredentialsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<YouTubeVideo>> PlaylistVideosAsync(string playlistId, CancellationToken cancellationToken) =>
        await api.PlaylistVideosAsync(playlistId, await AccountCredentialsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Details and playability for one video: the Data API when a key or sign-in is available
    /// (it reports embedding, age and regional limits), otherwise oEmbed (title and channel, and
    /// whether it exists and can be embedded). When YouTube can't be reached the player is
    /// allowed to try and report its own error.
    /// </summary>
    public async Task<YouTubeLookup> LookupAsync(string videoId, CancellationToken cancellationToken)
    {
        if (!YouTubeLink.IsVideoId(videoId))
        {
            return new YouTubeLookup(null, YouTubePlayability.NotFound);
        }

        if (CanBrowse)
        {
            try
            {
                var found = await api.GetVideosAsync([videoId], await CredentialsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                return found.Count == 0
                    ? new YouTubeLookup(null, YouTubePlayability.NotFound)
                    : new YouTubeLookup(found[0], YouTubePlayabilityRules.Evaluate(found[0].Status, region));
            }
            catch (YouTubeApiException error)
            {
                log("warn", $"YouTube lookup through the Data API failed; trying oEmbed. {error.Message}");
            }
        }

        var embed = await oembed.LookupAsync(videoId, cancellationToken).ConfigureAwait(false);
        return embed is null ? new YouTubeLookup(null, YouTubePlayability.Playable) : new YouTubeLookup(embed.Video, embed.Playability);
    }

    // ===== Aperture's own records =====

    public Task<IReadOnlyList<YouTubeEntry>> GetEntriesAsync(CancellationToken cancellationToken) => store.GetAllAsync(cancellationToken);

    public Task<YouTubeEntry?> GetEntryAsync(string videoId, CancellationToken cancellationToken) => store.GetAsync(videoId, cancellationToken);

    /// <summary>Records the position reached in Aperture, with the video's details the first time.</summary>
    public async Task RecordProgressAsync(string videoId, YouTubeVideo? video, TimeSpan position, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var existing = await store.GetAsync(videoId, cancellationToken).ConfigureAwait(false);
        await store.RecordProgressAsync(videoId, position, duration, now, cancellationToken).ConfigureAwait(false);
        if (video is not null && (existing?.Video is null || YouTubeDataRules.DetailsDueForRefresh(existing, now)))
        {
            await store.SetDetailsAsync(video, now, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(YouTubeVideo video, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(video);
        var now = clock.GetUtcNow();
        await store.SetSavedAsync(video.Id, now, cancellationToken).ConfigureAwait(false);
        await store.SetDetailsAsync(video, now, cancellationToken).ConfigureAwait(false);
    }

    public Task UnsaveAsync(string videoId, CancellationToken cancellationToken) => store.SetSavedAsync(videoId, null, cancellationToken);

    public Task ForgetProgressAsync(string videoId, CancellationToken cancellationToken) => store.ForgetProgressAsync(videoId, cancellationToken);

    /// <summary>
    /// Applies the 30-day rule: details older than <see cref="YouTubeDataRules.RefreshAfter"/> are
    /// refreshed from YouTube when possible; details that reach <see cref="YouTubeDataRules.DetailsLifetime"/>
    /// without a refresh, or whose video is gone, are removed. Returns how many were refreshed and removed.
    /// </summary>
    public async Task<(int Refreshed, int Removed)> MaintainDetailsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var entries = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var due = entries.Where(e => YouTubeDataRules.DetailsDueForRefresh(e, now)).ToList();
        var refreshed = new HashSet<string>(StringComparer.Ordinal);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (due.Count > 0)
        {
            try
            {
                if (CanBrowse)
                {
                    var ids = due.Select(e => e.VideoId).ToList();
                    var videos = await api.GetVideosAsync(ids, await CredentialsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    foreach (var video in videos)
                    {
                        await store.SetDetailsAsync(video, now, cancellationToken).ConfigureAwait(false);
                        refreshed.Add(video.Id);
                    }

                    gone.UnionWith(ids.Where(id => !refreshed.Contains(id)));
                }
                else
                {
                    foreach (var entry in due.Take(YouTubeDataRules.ContinueWatchingLimit * 2))
                    {
                        var embed = await oembed.LookupAsync(entry.VideoId, cancellationToken).ConfigureAwait(false);
                        if (embed?.Video is { } video)
                        {
                            await store.SetDetailsAsync(video, now, cancellationToken).ConfigureAwait(false);
                            refreshed.Add(video.Id);
                        }
                        else if (embed is not null)
                        {
                            gone.Add(entry.VideoId);
                        }
                    }
                }
            }
            catch (YouTubeApiException error)
            {
                log("warn", $"YouTube details couldn't be refreshed; expired ones are removed instead. {error.Message}");
            }
        }

        var remove = entries
            .Where(e => e.DetailsUtc is not null && !refreshed.Contains(e.VideoId)
                && (YouTubeDataRules.DetailsExpired(e, now) || gone.Contains(e.VideoId)))
            .Select(e => e.VideoId)
            .ToList();
        if (remove.Count > 0)
        {
            await store.ClearDetailsAsync(remove, cancellationToken).ConfigureAwait(false);
        }

        return (refreshed.Count, remove.Count);
    }

    /// <summary>Deletes everything Aperture holds about YouTube and signs out (revoking the grant with Google).</summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        await store.ClearAllAsync(cancellationToken).ConfigureAwait(false);
        if (account.IsSignedIn)
        {
            await account.SignOutAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<YouTubeCredentials> CredentialsAsync(CancellationToken cancellationToken)
    {
        var key = secrets.Read(ApiKeyName);
        // With a key, public requests don't need the account; without one, the sign-in identifies them.
        var token = key is { Length: > 0 } ? null : await account.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return new YouTubeCredentials(key, token);
    }

    private async Task<YouTubeCredentials> AccountCredentialsAsync(CancellationToken cancellationToken)
    {
        var token = await account.AccessTokenAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new YouTubeApiException(YouTubeProblem.SignInExpired, "Not signed in to YouTube.");
        return new YouTubeCredentials(null, token);
    }
}
