using Aperture.Core.YouTube;

namespace Aperture.Core.Tests.YouTube;

public class YouTubeLinkTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw", "jNQXAC9IVRw", 0)]
    [InlineData("youtube.com/watch?v=jNQXAC9IVRw&list=PL123&index=2", "jNQXAC9IVRw", 0)]
    [InlineData("https://m.youtube.com/watch?feature=share&v=jNQXAC9IVRw", "jNQXAC9IVRw", 0)]
    [InlineData("https://youtu.be/jNQXAC9IVRw?si=abc", "jNQXAC9IVRw", 0)]
    [InlineData("https://youtu.be/jNQXAC9IVRw?t=42", "jNQXAC9IVRw", 42)]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw&t=1m5s", "jNQXAC9IVRw", 65)]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw#t=1h0m2s", "jNQXAC9IVRw", 3602)]
    [InlineData("https://www.youtube.com/shorts/jNQXAC9IVRw", "jNQXAC9IVRw", 0)]
    [InlineData("https://www.youtube.com/embed/jNQXAC9IVRw?start=30", "jNQXAC9IVRw", 30)]
    [InlineData("https://www.youtube-nocookie.com/embed/jNQXAC9IVRw", "jNQXAC9IVRw", 0)]
    [InlineData("https://www.youtube.com/live/jNQXAC9IVRw", "jNQXAC9IVRw", 0)]
    [InlineData("https://music.youtube.com/watch?v=jNQXAC9IVRw", "jNQXAC9IVRw", 0)]
    [InlineData("  jNQXAC9IVRw  ", "jNQXAC9IVRw", 0)]
    public void Reads_video_links(string text, string id, int seconds)
    {
        var link = YouTubeLink.TryParse(text);
        Assert.NotNull(link);
        Assert.Equal(id, link.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(seconds), link.Start);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cats playing piano")]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("https://www.youtube.com/@somechannel")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://evil.example/watch?v=jNQXAC9IVRw")]
    [InlineData("https://youtube.com.evil.example/watch?v=jNQXAC9IVRw")]
    [InlineData("ftp://youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("javascript:alert(1)")]
    public void Refuses_anything_that_is_not_a_video_on_youtube(string? text) => Assert.Null(YouTubeLink.TryParse(text));

    [Fact]
    public void Watch_links_carry_the_start_time()
    {
        Assert.Equal("https://www.youtube.com/watch?v=jNQXAC9IVRw", YouTubeLink.WatchUri("jNQXAC9IVRw").ToString());
        Assert.Equal("https://www.youtube.com/watch?v=jNQXAC9IVRw&t=95s", YouTubeLink.WatchUri("jNQXAC9IVRw", TimeSpan.FromSeconds(95.7)).ToString());
    }

    [Theory]
    [InlineData("abc", 0)]
    [InlineData("10x", 0)]
    [InlineData("2h", 7200)]
    public void Reads_times(string value, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), YouTubeLink.ParseTime(value));
}
