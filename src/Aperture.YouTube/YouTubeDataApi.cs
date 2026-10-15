using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using Aperture.Core.YouTube;

namespace Aperture.YouTube;

/// <summary>A page of videos and the token for the next page, if there is one.</summary>
public sealed record YouTubePage(IReadOnlyList<YouTubeVideo> Videos, string? NextPageToken);

/// <summary>One of the signed-in user's playlists.</summary>
public sealed record YouTubePlaylist(string Id, string Title, int ItemCount, string? ThumbnailUrl);

/// <summary>How a request identifies itself: the user's API key, a signed-in access token, or both.</summary>
public sealed record YouTubeCredentials(string? ApiKey, string? AccessToken)
{
    public bool IsEmpty => string.IsNullOrEmpty(ApiKey) && string.IsNullOrEmpty(AccessToken);
}

/// <summary>
/// The YouTube Data API v3 (https://developers.google.com/youtube/v3/docs), only the
/// documented, read-only calls Aperture uses: <c>search.list</c> (100 quota units),
/// <c>videos.list</c> (1 unit per 50 videos), <c>playlists.list</c> and <c>playlistItems.list</c>
/// (1 unit). Search asks only for videos that can be embedded. Nothing is written to the
/// user's account.
/// </summary>
public sealed class YouTubeDataApi(HttpClient http)
{
    /// <summary>The documented API root.</summary>
    public static readonly Uri ApiRoot = new("https://www.googleapis.com/youtube/v3/");

    private const string VideoParts = "snippet,contentDetails,status";

    /// <summary>Videos matching <paramref name="query"/>, with their length and playability filled in.</summary>
    public async Task<YouTubePage> SearchAsync(string query, string? pageToken, string? region, YouTubeCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var request = $"search?part=snippet&type=video&videoEmbeddable=true&maxResults=24&q={Uri.EscapeDataString(query.Trim())}"
            + (region is { Length: 2 } ? $"&regionCode={region}" : string.Empty)
            + (pageToken is { Length: > 0 } ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : string.Empty);
        using var document = await GetAsync(request, credentials, cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        foreach (var item in Items(document.RootElement))
        {
            if (item.TryGetProperty("id", out var id) && Text(id, "videoId") is { } videoId && YouTubeLink.IsVideoId(videoId))
            {
                ids.Add(videoId);
            }
        }

        // search.list gives neither length nor playability; one videos.list call (1 unit) adds both.
        var videos = await GetVideosAsync(ids, credentials, cancellationToken).ConfigureAwait(false);
        var byId = videos.ToDictionary(v => v.Id, StringComparer.Ordinal);
        return new YouTubePage(ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), Text(document.RootElement, "nextPageToken"));
    }

    /// <summary>Full details for up to any number of ids (requested 50 at a time). Missing ids are videos that no longer exist or are private.</summary>
    public async Task<IReadOnlyList<YouTubeVideo>> GetVideosAsync(IReadOnlyList<string> ids, YouTubeCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var videos = new List<YouTubeVideo>(ids.Count);
        foreach (var chunk in ids.Where(YouTubeLink.IsVideoId).Distinct(StringComparer.Ordinal).Chunk(50))
        {
            using var document = await GetAsync($"videos?part={VideoParts}&maxResults=50&id={string.Join(',', chunk)}", credentials, cancellationToken).ConfigureAwait(false);
            videos.AddRange(Items(document.RootElement).Select(ReadVideo).OfType<YouTubeVideo>());
        }

        return videos;
    }

    /// <summary>YouTube's chart of popular videos for a region (1 unit).</summary>
    public async Task<IReadOnlyList<YouTubeVideo>> PopularAsync(string? region, YouTubeCredentials credentials, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
            $"videos?part={VideoParts}&chart=mostPopular&maxResults=24" + (region is { Length: 2 } ? $"&regionCode={region}" : string.Empty),
            credentials,
            cancellationToken).ConfigureAwait(false);
        return Items(document.RootElement).Select(ReadVideo).OfType<YouTubeVideo>().ToList();
    }

    /// <summary>Videos the signed-in user liked (needs sign-in; 1 unit).</summary>
    public async Task<IReadOnlyList<YouTubeVideo>> LikedAsync(YouTubeCredentials credentials, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"videos?part={VideoParts}&myRating=like&maxResults=24", credentials, cancellationToken).ConfigureAwait(false);
        return Items(document.RootElement).Select(ReadVideo).OfType<YouTubeVideo>().ToList();
    }

    /// <summary>The signed-in user's own playlists (needs sign-in; 1 unit).</summary>
    public async Task<IReadOnlyList<YouTubePlaylist>> MyPlaylistsAsync(YouTubeCredentials credentials, CancellationToken cancellationToken)
    {
        using var document = await GetAsync("playlists?part=snippet,contentDetails&mine=true&maxResults=25", credentials, cancellationToken).ConfigureAwait(false);
        var playlists = new List<YouTubePlaylist>();
        foreach (var item in Items(document.RootElement))
        {
            if (Text(item, "id") is not { } id || !item.TryGetProperty("snippet", out var snippet))
            {
                continue;
            }

            var count = item.TryGetProperty("contentDetails", out var details) && details.TryGetProperty("itemCount", out var c) && c.TryGetInt32(out var n) ? n : 0;
            playlists.Add(new YouTubePlaylist(id, Decode(Text(snippet, "title")) ?? "Playlist", count, Thumbnail(snippet)));
        }

        return playlists;
    }

    /// <summary>The first videos of a playlist the user can see, with full details (2 units).</summary>
    public async Task<IReadOnlyList<YouTubeVideo>> PlaylistVideosAsync(string playlistId, YouTubeCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        using var document = await GetAsync($"playlistItems?part=contentDetails&maxResults=50&playlistId={Uri.EscapeDataString(playlistId)}", credentials, cancellationToken).ConfigureAwait(false);
        var ids = Items(document.RootElement)
            .Select(item => item.TryGetProperty("contentDetails", out var details) ? Text(details, "videoId") : null)
            .OfType<string>()
            .ToList();
        var videos = await GetVideosAsync(ids, credentials, cancellationToken).ConfigureAwait(false);
        var byId = videos.ToDictionary(v => v.Id, StringComparer.Ordinal);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    // ===== Reading responses =====

    internal static YouTubeVideo? ReadVideo(JsonElement item)
    {
        if (Text(item, "id") is not { } id || !YouTubeLink.IsVideoId(id) || !item.TryGetProperty("snippet", out var snippet))
        {
            return null;
        }

        TimeSpan? duration = null;
        var ageRestricted = false;
        IReadOnlyList<string>? allowed = null, blocked = null;
        if (item.TryGetProperty("contentDetails", out var details))
        {
            duration = ParseDuration(Text(details, "duration"));
            if (details.TryGetProperty("contentRating", out var rating))
            {
                ageRestricted = Text(rating, "ytRating") == "ytAgeRestricted";
            }

            if (details.TryGetProperty("regionRestriction", out var restriction))
            {
                allowed = Strings(restriction, "allowed");
                blocked = Strings(restriction, "blocked");
            }
        }

        var embeddable = true;
        var privacy = "public";
        string? upload = null;
        if (item.TryGetProperty("status", out var status))
        {
            embeddable = !status.TryGetProperty("embeddable", out var e) || e.ValueKind != JsonValueKind.False;
            privacy = Text(status, "privacyStatus") ?? privacy;
            upload = Text(status, "uploadStatus");
        }

        var live = Text(snippet, "liveBroadcastContent") switch
        {
            "live" => YouTubeLiveState.Live,
            "upcoming" => YouTubeLiveState.Upcoming,
            _ => YouTubeLiveState.None,
        };

        // Live streams report a zero length.
        if (duration == TimeSpan.Zero)
        {
            duration = null;
        }

        return new YouTubeVideo(
            id,
            Decode(Text(snippet, "title")) ?? string.Empty,
            Decode(Text(snippet, "channelTitle")) ?? string.Empty,
            Thumbnail(snippet) ?? YouTubeVideo.DefaultThumbnail(id),
            duration,
            new YouTubeVideoStatus(embeddable, privacy, ageRestricted, allowed, blocked, live, upload));
    }

    /// <summary>Reads an ISO 8601 duration ("PT1H2M3S", "P1DT2H").</summary>
    internal static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The 16:9 "medium" thumbnail (320 × 180); the larger sizes are 4:3 with black bars.</summary>
    private static string? Thumbnail(JsonElement snippet) =>
        snippet.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.TryGetProperty("medium", out var medium) ? Text(medium, "url") : null;

    // search.list returns titles with HTML entities (&amp;, &#39;); videos.list does not, and decoding plain text is harmless.
    private static string? Decode(string? value) => value is null ? null : WebUtility.HtmlDecode(value);

    private static List<JsonElement> Items(JsonElement root) =>
        root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToList() : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string>? Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : null;

    // ===== Requests =====

    private async Task<JsonDocument> GetAsync(string relative, YouTubeCredentials credentials, CancellationToken cancellationToken)
    {
        if (credentials.IsEmpty)
        {
            throw new YouTubeApiException(YouTubeProblem.KeyRejected, "No YouTube API key or sign-in is set up.");
        }

        // The key goes in the query string, as the API documents; the access token in the header.
        var uri = new Uri(ApiRoot, credentials.ApiKey is { Length: > 0 } key ? $"{relative}&key={Uri.EscapeDataString(key)}" : relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (credentials.AccessToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new YouTubeApiException(YouTubeProblem.Offline, "YouTube could not be reached.", error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new YouTubeApiException(YouTubeProblem.Offline, "YouTube did not answer in time.", error);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return JsonDocument.Parse(body);
                }
                catch (JsonException error)
                {
                    throw new YouTubeApiException(YouTubeProblem.Other, "YouTube sent a response Aperture couldn't read.", error);
                }
            }

            throw ReadError(response.StatusCode, body);
        }
    }

    /// <summary>Turns YouTube's error document (<c>error.errors[].reason</c>) into a problem; the message never echoes the request.</summary>
    internal static YouTubeApiException ReadError(HttpStatusCode status, string body)
    {
        string? reason = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                message = Text(error, "message");
                if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    reason = errors.EnumerateArray().Select(e => Text(e, "reason")).FirstOrDefault(r => r is not null);
                }

                if (reason is null && error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    reason = details.EnumerateArray().Select(d => Text(d, "reason")).FirstOrDefault(r => r is not null);
                }
            }
        }
        catch (JsonException)
        {
            // Not YouTube's JSON (a proxy page); the status code decides.
        }

        var code = ((int)status).ToString(CultureInfo.InvariantCulture);
        var problem = reason switch
        {
            "quotaExceeded" or "dailyLimitExceeded" or "rateLimitExceeded" or "RATE_LIMIT_EXCEEDED" => YouTubeProblem.QuotaExceeded,
            "keyInvalid" or "keyExpired" or "API_KEY_INVALID" or "ipRefererBlocked" or "API_KEY_SERVICE_BLOCKED" or "API_KEY_HTTP_REFERRER_BLOCKED" => YouTubeProblem.KeyRejected,
            "accessNotConfigured" or "SERVICE_DISABLED" => YouTubeProblem.ApiNotEnabled,
            "authError" or "ACCESS_TOKEN_EXPIRED" or "CREDENTIALS_MISSING" => YouTubeProblem.SignInExpired,
            _ when status == HttpStatusCode.Unauthorized => YouTubeProblem.SignInExpired,
            _ when (int)status >= 500 => YouTubeProblem.Offline,
            _ => YouTubeProblem.Other,
        };
        return new YouTubeApiException(problem, $"YouTube answered {code} ({reason ?? "no reason"}): {message ?? "no message"}");
    }
}
