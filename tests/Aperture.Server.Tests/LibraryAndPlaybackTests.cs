using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aperture.Core.Settings;
using Aperture.Data;

namespace Aperture.Server.Tests;

public class LibraryAndPlaybackTests
{
    [Fact]
    public async Task Devices_see_only_shared_folders()
    {
        long privateId = 0;
        await using var test = await TestServer.StartAsync(async t =>
        {
            var films = await t.AddFolderAsync("Films", shared: true);
            var home = await t.AddFolderAsync("Home videos", shared: false);
            await t.AddItemAsync(films, "Night Market.mp4", "Night Market");
            privateId = (await t.AddItemAsync(home, "Birthday.mp4", "Birthday")).Id;
        });
        using var client = await test.SignedInClientAsync();

        var library = await (await client.GetAsync("api/library")).Content.ReadAsStringAsync();
        Assert.Contains("Night Market", library, StringComparison.Ordinal);
        Assert.DoesNotContain("Birthday", library, StringComparison.Ordinal);
        Assert.DoesNotContain(test.MediaFolder, library, StringComparison.Ordinal); // no paths leave the server

        using var item = await client.GetAsync($"api/items/{privateId}");
        Assert.Equal(HttpStatusCode.NotFound, item.StatusCode);
        using var play = await client.PostAsJsonAsync($"api/items/{privateId}/play", TestServer.PlayRequest());
        Assert.Equal(HttpStatusCode.NotFound, play.StatusCode);
        using var progress = await client.PostAsJsonAsync($"api/items/{privateId}/progress", new { position = 10 });
        Assert.Equal(HttpStatusCode.NotFound, progress.StatusCode);
    }

    [Fact]
    public async Task Direct_play_serves_the_original_file_with_ranges_and_leaves_it_untouched()
    {
        var content = TestServer.RandomBytes(256 * 1024);
        long id = 0;
        string path = string.Empty;
        await using var test = await TestServer.StartAsync(async t =>
        {
            var films = await t.AddFolderAsync("Films", shared: true);
            var item = await t.AddItemAsync(films, "Night Market.mp4", "Night Market", content);
            (id, path) = (item.Id, item.Path);
        });
        using var client = await test.SignedInClientAsync();

        using var play = await client.PostAsJsonAsync($"api/items/{id}/play", TestServer.PlayRequest());
        var answer = await TestServer.ReadJsonAsync(play);
        Assert.Equal("direct", answer.GetProperty("mode").GetString());
        var url = answer.GetProperty("url").GetString()!;
        Assert.Matches("^/media/[0-9a-f]{32}/file\\.mp4$", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimStart('/'));
        request.Headers.Range = new RangeHeaderValue(1000, 1999);
        using var partial = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("video/mp4", partial.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content[1000..2000], await partial.Content.ReadAsByteArrayAsync());

        using var whole = await client.GetAsync(url.TrimStart('/'));
        Assert.Equal(content, await whole.Content.ReadAsByteArrayAsync());
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Media_addresses_stop_working_when_access_is_withdrawn()
    {
        long id = 0, folderId = 0;
        await using var test = await TestServer.StartAsync(async t =>
        {
            var films = await t.AddFolderAsync("Films", shared: true);
            folderId = films.Id;
            id = (await t.AddItemAsync(films, "Night Market.mp4", "Night Market")).Id;
        });
        using var client = await test.SignedInClientAsync();

        async Task<string> PlayAsync()
        {
            using var play = await client.PostAsJsonAsync($"api/items/{id}/play", TestServer.PlayRequest());
            return (await TestServer.ReadJsonAsync(play)).GetProperty("url").GetString()!.TrimStart('/');
        }

        // A folder that is no longer shared.
        var url = await PlayAsync();
        await test.Store.SetFolderSharedAsync(folderId, false);
        using (var unshared = await client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.NotFound, unshared.StatusCode);
        }

        // A device signed out from the desktop app. Media requests carry no cookie, only the token.
        await test.Store.SetFolderSharedAsync(folderId, true);
        url = await PlayAsync();
        using var anonymous = test.Client();
        using (var works = await anonymous.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, works.StatusCode);
        }

        await test.Store.RemoveAllDevicesAsync();
        using var revoked = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Theory]
    [InlineData("media/0123456789abcdef0123456789abcdef/file.mp4")]
    [InlineData("media/../../etc/passwd")]
    [InlineData("media/%2e%2e/%2e%2e/etc/passwd")]
    [InlineData("media/x/..%2f..%2fetc%2fpasswd")]
    [InlineData("media/abc/index.m3u8")]
    [InlineData("media/abc/seg00000.ts")]
    [InlineData("api/items/..%2f..%2fetc%2fpasswd")]
    [InlineData("api/items/-1")]
    [InlineData("api/items/99999")]
    [InlineData("../../../../etc/passwd")]
    [InlineData("wwwroot/../Aperture.Server.dll")]
    public async Task Made_up_addresses_reach_nothing(string path)
    {
        await using var test = await TestServer.StartAsync(async t =>
        {
            var films = await t.AddFolderAsync("Films", shared: true);
            await t.AddItemAsync(films, "Night Market.mp4", "Night Market");
        });
        using var client = await test.SignedInClientAsync();

        using var response = await client.GetAsync(path);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{path} answered {response.StatusCode}");
    }

    [Fact]
    public async Task Positions_from_a_device_reach_the_desktops_history()
    {
        long id = 0;
        string path = string.Empty;
        await using var test = await TestServer.StartAsync(async t =>
        {
            var films = await t.AddFolderAsync("Films", shared: true);
            var item = await t.AddItemAsync(films, "Night Market.mp4", "Night Market", duration: TimeSpan.FromMinutes(90));
            (id, path) = (item.Id, item.Path);
        });
        using var client = await test.SignedInClientAsync();

        using var saved = await client.PostAsJsonAsync($"api/items/{id}/progress", new { position = 1234.5, duration = 60 });
        Assert.True((await TestServer.ReadJsonAsync(saved)).GetProperty("saved").GetBoolean());
        var entry = await test.History.FindAsync(path);
        Assert.NotNull(entry);
        Assert.Equal(1234.5, entry.Position.TotalSeconds, 1);
        Assert.Equal(TimeSpan.FromMinutes(90), entry.Duration); // the library's length, not the device's

        var detail = await TestServer.ReadJsonAsync(await client.GetAsync($"api/items/{id}"));
        Assert.Equal(1234.5, detail.GetProperty("card").GetProperty("resumeSeconds").GetDouble(), 0);

        using var nonsense = await client.PostAsJsonAsync($"api/items/{id}/progress", new { position = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Equal(1234.5, (await test.History.FindAsync(path))!.Position.TotalSeconds, 1);
    }

    [Fact]
    public async Task Positions_are_not_saved_when_history_is_off_on_the_desktop()
    {
        long id = 0;
        string path = string.Empty;
        await using var test = await TestServer.StartAsync(async t =>
        {
            var films = await t.AddFolderAsync("Films", shared: true);
            var item = await t.AddItemAsync(films, "Night Market.mp4", "Night Market");
            (id, path) = (item.Id, item.Path);
            await new SqliteSettingsStore(t.Database).SaveAsync(AppSettings.Default with { RememberHistory = false });
        });
        using var client = await test.SignedInClientAsync();

        using var response = await client.PostAsJsonAsync($"api/items/{id}/progress", new { position = 30 });

        Assert.False((await TestServer.ReadJsonAsync(response)).GetProperty("saved").GetBoolean());
        Assert.Null(await test.History.FindAsync(path));
    }

    [Fact]
    public async Task Without_ffmpeg_files_a_device_cannot_play_say_why()
    {
        long id = 0;
        await using var test = await TestServer.StartAsync(async t =>
        {
            var films = await t.AddFolderAsync("Films", shared: true);
            id = (await t.AddItemAsync(films, "Archive.mkv", "Archive", videoCodec: "hevc")).Id;
        });
        using var client = await test.SignedInClientAsync();

        using var play = await client.PostAsJsonAsync($"api/items/{id}/play", TestServer.PlayRequest());
        var answer = await TestServer.ReadJsonAsync(play);

        Assert.Equal("unavailable", answer.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("url").ValueKind);
        Assert.Contains("FFmpeg", answer.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_speed_test_sends_the_amount_asked_for_within_limits()
    {
        await using var test = await TestServer.StartAsync();
        using var client = await test.SignedInClientAsync();

        Assert.Equal(128 * 1024, (await client.GetByteArrayAsync("api/speedtest?kb=128")).Length);
        Assert.Equal(8192 * 1024, (await client.GetByteArrayAsync("api/speedtest?kb=999999")).Length);
    }
}
