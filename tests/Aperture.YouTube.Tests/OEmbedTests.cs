using System.Net;
using Aperture.Core.YouTube;

namespace Aperture.YouTube.Tests;

public class OEmbedTests
{
    [Fact]
    public async Task Reads_title_and_channel_and_uses_the_16_by_9_thumbnail()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""
            { "title": "Me at the zoo", "author_name": "jawed", "type": "video", "provider_name": "YouTube",
              "thumbnail_url": "https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg" }
            """));
        var result = await new YouTubeOEmbed(http.Client()).LookupAsync("jNQXAC9IVRw", default);
        Assert.NotNull(result);
        Assert.Equal(YouTubePlayability.Playable, result.Playability);
        Assert.Equal("Me at the zoo", result.Video!.Title);
        Assert.Equal("jawed", result.Video.Channel);
        Assert.Equal("https://i.ytimg.com/vi/jNQXAC9IVRw/mqdefault.jpg", result.Video.ThumbnailUrl);
        Assert.Equal("https://www.youtube.com/oembed?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DjNQXAC9IVRw", http.Requests[0].Uri.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, YouTubePlayability.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, YouTubePlayability.NotEmbeddable)]
    public async Task Unavailable_videos_are_recognised(HttpStatusCode status, YouTubePlayability expected)
    {
        var http = new FakeHttp(_ => new HttpResponseMessage(status));
        var result = await new YouTubeOEmbed(http.Client()).LookupAsync("jNQXAC9IVRw", default);
        Assert.Equal(expected, result!.Playability);
    }

    [Fact]
    public async Task Network_problems_leave_the_decision_to_the_player()
    {
        var offline = new FakeHttp(_ => throw new HttpRequestException("offline"));
        Assert.Null(await new YouTubeOEmbed(offline.Client()).LookupAsync("jNQXAC9IVRw", default));
        var broken = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Null(await new YouTubeOEmbed(broken.Client()).LookupAsync("jNQXAC9IVRw", default));
    }
}
