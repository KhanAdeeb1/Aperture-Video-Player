using System.Net;
using System.Text.Json;
using Aperture.Core.YouTube;

namespace Aperture.YouTube;

/// <summary>What YouTube's oEmbed endpoint said about a video.</summary>
/// <param name="Video">Title, channel and thumbnail, when YouTube returned them.</param>
/// <param name="Playability">NotFound for 404, NotEmbeddable for 401 (embedding turned off or private), else Playable.</param>
public sealed record OEmbedResult(YouTubeVideo? Video, YouTubePlayability Playability);

/// <summary>
/// YouTube's public oEmbed endpoint (https://oembed.com, provider https://www.youtube.com/oembed).
/// It needs no key, so a pasted link gets its title and channel even when no API key is set up,
/// and it answers 401 for videos that can't be embedded and 404 for ones that don't exist.
/// </summary>
public sealed class YouTubeOEmbed(HttpClient http)
{
    public static readonly Uri Endpoint = new("https://www.youtube.com/oembed");

    /// <summary>Looks up one video. Network problems return null: the player can still try, and will report its own error.</summary>
    public async Task<OEmbedResult?> LookupAsync(string videoId, CancellationToken cancellationToken)
    {
        if (!YouTubeLink.IsVideoId(videoId))
        {
            return new OEmbedResult(null, YouTubePlayability.NotFound);
        }

        var watch = YouTubeLink.WatchUri(videoId);
        var uri = new Uri($"{Endpoint}?format=json&url={Uri.EscapeDataString(watch.ToString())}");
        try
        {
            using var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return new OEmbedResult(null, YouTubePlayability.NotFound);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new OEmbedResult(null, YouTubePlayability.NotEmbeddable);
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new OEmbedResult(Read(videoId, body), YouTubePlayability.Playable);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static YouTubeVideo? Read(string videoId, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return Text("title") is { } title
                // oEmbed's thumbnail is the 4:3 "hqdefault" with black bars; Aperture uses the 16:9 one.
                ? new YouTubeVideo(videoId, title, Text("author_name") ?? string.Empty, YouTubeVideo.DefaultThumbnail(videoId), null)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
