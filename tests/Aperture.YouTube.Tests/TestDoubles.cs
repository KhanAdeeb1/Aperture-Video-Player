using System.Net;
using System.Text;
using Aperture.Core.Settings;

namespace Aperture.YouTube.Tests;

/// <summary>Answers HTTP requests from a function and records them; no network is used.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string? Authorization, string? Body)> Requests { get; } = [];

    public HttpClient Client() => new(this, disposeHandler: false);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        return respond(request);
    }
}

internal sealed class MemorySecrets : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public string? Read(string name) => Values.GetValueOrDefault(name);

    public void Write(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Values.Remove(name);
        }
        else
        {
            Values[name] = value;
        }
    }
}

internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Response bodies in the shapes documented at developers.google.com/youtube/v3/docs.</summary>
internal static class Responses
{
    public static string Video(string id, string title = "Me at the zoo", string duration = "PT19S", bool embeddable = true, string privacy = "public", string live = "none", string extraDetails = "") => $$"""
        {
          "kind": "youtube#video", "id": "{{id}}",
          "snippet": {
            "title": "{{title}}", "channelTitle": "jawed", "liveBroadcastContent": "{{live}}",
            "thumbnails": {
              "default": { "url": "https://i.ytimg.com/vi/{{id}}/default.jpg", "width": 120, "height": 90 },
              "medium": { "url": "https://i.ytimg.com/vi/{{id}}/mqdefault.jpg", "width": 320, "height": 180 },
              "high": { "url": "https://i.ytimg.com/vi/{{id}}/hqdefault.jpg", "width": 480, "height": 360 }
            }
          },
          "contentDetails": { "duration": "{{duration}}", "contentRating": {} {{extraDetails}} },
          "status": { "uploadStatus": "processed", "privacyStatus": "{{privacy}}", "embeddable": {{(embeddable ? "true" : "false")}} }
        }
        """;

    public static string Videos(params string[] items) => $$"""{ "kind": "youtube#videoListResponse", "items": [{{string.Join(",", items)}}] }""";

    public static string Search(string? next, params string[] ids) => $$"""
        {
          "kind": "youtube#searchListResponse", {{(next is null ? string.Empty : $"\"nextPageToken\": \"{next}\",")}}
          "items": [{{string.Join(",", ids.Select(id => $$"""{ "kind": "youtube#searchResult", "id": { "kind": "youtube#video", "videoId": "{{id}}" }, "snippet": { "title": "Tom &amp; Jerry&#39;s", "channelTitle": "x" } }"""))}}]
        }
        """;

    public static string Error(int code, string reason, string message = "The request cannot be completed.") => $$"""
        { "error": { "code": {{code}}, "message": "{{message}}", "errors": [ { "message": "{{message}}", "domain": "youtube.quota", "reason": "{{reason}}" } ] } }
        """;
}
