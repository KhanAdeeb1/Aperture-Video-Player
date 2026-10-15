using System.Net;
using Aperture.Core.YouTube;

namespace Aperture.YouTube.Tests;

public class YouTubeDataApiTests
{
    private static readonly YouTubeCredentials Key = new("test-key", null);

    [Fact]
    public async Task Search_asks_only_for_embeddable_videos_and_fills_in_details()
    {
        var http = new FakeHttp(request => request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? FakeHttp.Json(Responses.Search("NEXT", "aaaaaaaaaaa", "bbbbbbbbbbb", "ccccccccccc"))
            // c has been deleted since the search index was built: videos.list leaves it out.
            : FakeHttp.Json(Responses.Videos(Responses.Video("bbbbbbbbbbb", "B", "PT1H2M3S"), Responses.Video("aaaaaaaaaaa", "Tom &amp; Jerry"))));
        var api = new YouTubeDataApi(http.Client());

        var page = await api.SearchAsync("zoo videos", null, "GB", Key, default);

        Assert.Equal(["aaaaaaaaaaa", "bbbbbbbbbbb"], page.Videos.Select(v => v.Id)); // search order kept
        Assert.Equal("NEXT", page.NextPageToken);
        Assert.Equal("Tom & Jerry", page.Videos[0].Title);
        Assert.Equal(new TimeSpan(1, 2, 3), page.Videos[1].Duration);
        Assert.Equal(2, http.Requests.Count);
        var search = http.Requests[0].Uri.Query;
        Assert.Contains("type=video", search, StringComparison.Ordinal);
        Assert.Contains("videoEmbeddable=true", search, StringComparison.Ordinal);
        Assert.Contains("q=zoo%20videos", search, StringComparison.Ordinal);
        Assert.Contains("regionCode=GB", search, StringComparison.Ordinal);
        Assert.Contains("key=test-key", search, StringComparison.Ordinal);
        Assert.Contains("id=aaaaaaaaaaa,bbbbbbbbbbb,ccccccccccc", Uri.UnescapeDataString(http.Requests[1].Uri.Query), StringComparison.Ordinal);
        Assert.Null(http.Requests[0].Authorization);
    }

    [Fact]
    public async Task A_signed_in_request_sends_the_token_in_the_header_not_the_url()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(Responses.Videos(Responses.Video("aaaaaaaaaaa"))));
        await new YouTubeDataApi(http.Client()).LikedAsync(new YouTubeCredentials(null, "access-token"), default);
        Assert.Equal("Bearer access-token", http.Requests[0].Authorization);
        Assert.DoesNotContain("access-token", http.Requests[0].Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("myRating=like", http.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Video_ids_are_requested_fifty_at_a_time_and_invalid_ids_are_dropped()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(Responses.Videos()));
        var ids = Enumerable.Range(0, 120).Select(i => $"video{i:000000}").Append("not an id").ToList();
        await new YouTubeDataApi(http.Client()).GetVideosAsync(ids, Key, default);
        Assert.Equal(3, http.Requests.Count);
        Assert.DoesNotContain("not", http.Requests[^1].Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_restrictions_from_a_video()
    {
        using var document = System.Text.Json.JsonDocument.Parse(Responses.Video(
            "aaaaaaaaaaa",
            embeddable: false,
            extraDetails: """, "regionRestriction": { "blocked": ["DE", "GB"] }"""));
        var video = YouTubeDataApi.ReadVideo(document.RootElement)!;
        Assert.False(video.Status!.Embeddable);
        Assert.Equal(["DE", "GB"], video.Status.BlockedRegions!);
        Assert.Equal("https://i.ytimg.com/vi/aaaaaaaaaaa/mqdefault.jpg", video.ThumbnailUrl);
        Assert.Equal(YouTubePlayability.NotEmbeddable, YouTubePlayabilityRules.Evaluate(video.Status, "GB"));
    }

    [Fact]
    public void Reads_age_restriction_and_live_state()
    {
        using var age = System.Text.Json.JsonDocument.Parse(Responses.Video("aaaaaaaaaaa").Replace("\"contentRating\": {}", "\"contentRating\": { \"ytRating\": \"ytAgeRestricted\" }", StringComparison.Ordinal));
        Assert.True(YouTubeDataApi.ReadVideo(age.RootElement)!.Status!.AgeRestricted);

        using var live = System.Text.Json.JsonDocument.Parse(Responses.Video("aaaaaaaaaaa", duration: "P0D", live: "upcoming"));
        var video = YouTubeDataApi.ReadVideo(live.RootElement)!;
        Assert.Null(video.Duration);
        Assert.Equal(YouTubeLiveState.Upcoming, video.Status!.Live);
    }

    [Theory]
    [InlineData(403, "quotaExceeded", YouTubeProblem.QuotaExceeded)]
    [InlineData(400, "keyInvalid", YouTubeProblem.KeyRejected)]
    [InlineData(403, "accessNotConfigured", YouTubeProblem.ApiNotEnabled)]
    [InlineData(401, "authError", YouTubeProblem.SignInExpired)]
    [InlineData(403, "forbidden", YouTubeProblem.Other)]
    [InlineData(503, "backendError", YouTubeProblem.Offline)]
    public async Task Errors_are_classified(int status, string reason, YouTubeProblem expected)
    {
        var http = new FakeHttp(_ => FakeHttp.Json(Responses.Error(status, reason), (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<YouTubeApiException>(() => new YouTubeDataApi(http.Client()).PopularAsync("GB", Key, default));
        Assert.Equal(expected, error.Problem);
        Assert.DoesNotContain("test-key", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.Explain()));
    }

    [Fact]
    public async Task No_connection_is_reported_as_offline()
    {
        var http = new FakeHttp(_ => throw new HttpRequestException("no route"));
        var error = await Assert.ThrowsAsync<YouTubeApiException>(() => new YouTubeDataApi(http.Client()).PopularAsync(null, Key, default));
        Assert.Equal(YouTubeProblem.Offline, error.Problem);
    }

    [Fact]
    public async Task Nothing_is_requested_without_a_key_or_sign_in()
    {
        var http = new FakeHttp(_ => throw new InvalidOperationException("should not be called"));
        await Assert.ThrowsAsync<YouTubeApiException>(() => new YouTubeDataApi(http.Client()).PopularAsync(null, new YouTubeCredentials(null, null), default));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Playlists_keep_their_order_and_skip_videos_that_are_gone()
    {
        var http = new FakeHttp(request => request.RequestUri!.AbsolutePath.EndsWith("/playlistItems", StringComparison.Ordinal)
            ? FakeHttp.Json("""{ "items": [ { "contentDetails": { "videoId": "bbbbbbbbbbb" } }, { "contentDetails": { "videoId": "aaaaaaaaaaa" } }, { "contentDetails": { "videoId": "deleted0000" } } ] }""")
            : FakeHttp.Json(Responses.Videos(Responses.Video("aaaaaaaaaaa"), Responses.Video("bbbbbbbbbbb"))));
        var videos = await new YouTubeDataApi(http.Client()).PlaylistVideosAsync("PL1", new YouTubeCredentials(null, "t"), default);
        Assert.Equal(["bbbbbbbbbbb", "aaaaaaaaaaa"], videos.Select(v => v.Id));
    }
}
