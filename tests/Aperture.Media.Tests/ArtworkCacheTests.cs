using System.Net;
using System.Text;

namespace Aperture.Media.Tests;

public class ArtworkCacheTests
{
    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "aperture-art-" + Guid.NewGuid().ToString("N"));

    private static HttpResponseMessage Image(byte[] bytes, string type = "image/jpeg") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(type) } } };

    [Fact]
    public async Task An_image_is_downloaded_once_and_reused()
    {
        var folder = NewFolder();
        var http = new FakeHttp(_ => Image([1, 2, 3]));
        var cache = new ArtworkCache(http.Client(), folder);
        try
        {
            var first = await cache.GetAsync("https://image.example/a.jpg", CancellationToken.None);
            var second = await cache.GetAsync("https://image.example/a.jpg", CancellationToken.None);

            Assert.Equal(first, second);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(first!));
            Assert.Single(http.Requests);
            Assert.Empty(Directory.GetFiles(folder, "*.part"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Anything_that_is_not_an_image_is_refused()
    {
        var folder = NewFolder();
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") });

        Assert.Null(await new ArtworkCache(http.Client(), folder).GetAsync("https://image.example/a.jpg", CancellationToken.None));
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any());
    }

    [Fact]
    public async Task An_oversized_image_is_refused_and_leaves_nothing_behind()
    {
        var folder = NewFolder();
        var http = new FakeHttp(_ => Image(new byte[ArtworkCache.MaxBytes + 1]));
        try
        {
            Assert.Null(await new ArtworkCache(http.Client(), folder).GetAsync("https://image.example/big.jpg", CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Only_https_addresses_are_fetched()
    {
        var http = new FakeHttp(_ => Image([1]));

        Assert.Null(await new ArtworkCache(http.Client(), NewFolder()).GetAsync("http://image.example/a.jpg", CancellationToken.None));
        Assert.Empty(http.Requests);
    }
}
