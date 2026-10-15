using System.Net;
using Aperture.Core.YouTube;
using Aperture.Data;
using Microsoft.Data.Sqlite;

namespace Aperture.YouTube.Tests;

public sealed class YouTubeServiceTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aperture-yt-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly MemorySecrets _secrets = new();
    private readonly List<string> _log = [];
    private SqliteYouTubeStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        var database = new ApertureDatabase(Path.Combine(_folder, "aperture.db"));
        await database.MigrateAsync();
        _store = new SqliteYouTubeStore(database);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    private YouTubeService Service(FakeHttp http, YouTubeAccount? account = null) => new(
        _store,
        new YouTubeDataApi(http.Client()),
        new YouTubeOEmbed(http.Client()),
        account ?? new YouTubeAccount(_secrets, new GoogleOAuth(http.Client(), _clock), _clock),
        _secrets,
        _clock,
        "GB",
        (level, message) => _log.Add($"{level}: {message}"));

    private static readonly YouTubeVideo Zoo = new("jNQXAC9IVRw", "Me at the zoo", "jawed", YouTubeVideo.DefaultThumbnail("jNQXAC9IVRw"), TimeSpan.FromSeconds(19));

    [Fact]
    public async Task Without_a_key_links_are_looked_up_through_oembed_and_nothing_else_is_called()
    {
        var http = new FakeHttp(request => request.RequestUri!.Host == "www.youtube.com"
            ? FakeHttp.Json("""{ "title": "Me at the zoo", "author_name": "jawed" }""")
            : throw new InvalidOperationException("The Data API must not be called without a key."));
        var service = Service(http);

        Assert.False(service.CanBrowse);
        var lookup = await service.LookupAsync("jNQXAC9IVRw", default);
        Assert.Equal(YouTubePlayability.Playable, lookup.Playability);
        Assert.Equal("Me at the zoo", lookup.Video!.Title);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task With_a_key_lookups_report_restrictions_from_the_data_api()
    {
        _secrets.Write(YouTubeService.ApiKeyName, "k");
        var http = new FakeHttp(_ => FakeHttp.Json(Responses.Videos(Responses.Video("jNQXAC9IVRw", extraDetails: """, "regionRestriction": { "allowed": ["US"] }"""))));
        var lookup = await Service(http).LookupAsync("jNQXAC9IVRw", default);
        Assert.Equal(YouTubePlayability.RegionBlocked, lookup.Playability);

        var gone = new FakeHttp(_ => FakeHttp.Json(Responses.Videos()));
        Assert.Equal(YouTubePlayability.NotFound, (await Service(gone).LookupAsync("jNQXAC9IVRw", default)).Playability);
    }

    [Fact]
    public async Task A_failing_data_api_falls_back_to_oembed()
    {
        _secrets.Write(YouTubeService.ApiKeyName, "k");
        var http = new FakeHttp(request => request.RequestUri!.Host == "www.googleapis.com"
            ? FakeHttp.Json(Responses.Error(403, "quotaExceeded"), HttpStatusCode.Forbidden)
            : FakeHttp.Json("""{ "title": "Me at the zoo", "author_name": "jawed" }"""));
        var lookup = await Service(http).LookupAsync("jNQXAC9IVRw", default);
        Assert.Equal("Me at the zoo", lookup.Video!.Title);
        Assert.Contains(_log, line => line.Contains("quotaExceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Progress_stores_details_once_and_continue_watching_follows()
    {
        var service = Service(new FakeHttp(_ => throw new InvalidOperationException()));
        await service.RecordProgressAsync(Zoo.Id, Zoo, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(600), default);
        await service.RecordProgressAsync(Zoo.Id, Zoo with { Title = "changed" }, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(600), default);
        var entries = await service.GetEntriesAsync(default);
        var entry = Assert.Single(YouTubeDataRules.ContinueWatching(entries));
        Assert.Equal("Me at the zoo", entry.Title); // details aren't rewritten on every progress tick
        Assert.Equal(TimeSpan.FromSeconds(60), entry.Position);
    }

    [Fact]
    public async Task Details_are_refreshed_before_thirty_days_when_a_key_is_available()
    {
        var service = Service(new FakeHttp(_ => throw new InvalidOperationException()));
        await service.SaveAsync(Zoo, default);

        _secrets.Write(YouTubeService.ApiKeyName, "k");
        _clock.Now = _clock.Now.AddDays(26);
        var http = new FakeHttp(_ => FakeHttp.Json(Responses.Videos(Responses.Video("jNQXAC9IVRw", "Me at the zoo (remastered)"))));
        var (refreshed, removed) = await Service(http).MaintainDetailsAsync(default);

        Assert.Equal((1, 0), (refreshed, removed));
        var entry = (await _store.GetAsync(Zoo.Id))!;
        Assert.Equal("Me at the zoo (remastered)", entry.Title);
        Assert.Equal(_clock.Now, entry.DetailsUtc);
    }

    [Fact]
    public async Task Details_that_cannot_be_refreshed_are_removed_at_thirty_days()
    {
        var service = Service(new FakeHttp(_ => throw new InvalidOperationException()));
        await service.SaveAsync(Zoo, default);

        // No key, and oEmbed can't be reached.
        _clock.Now = _clock.Now.AddDays(26);
        var offline = new FakeHttp(_ => throw new HttpRequestException("offline"));
        Assert.Equal((0, 0), await Service(offline).MaintainDetailsAsync(default));
        Assert.NotNull((await _store.GetAsync(Zoo.Id))!.Video);

        _clock.Now = _clock.Now.AddDays(4);
        Assert.Equal((0, 1), await Service(offline).MaintainDetailsAsync(default));
        var entry = (await _store.GetAsync(Zoo.Id))!;
        Assert.Null(entry.Video);
        Assert.NotNull(entry.SavedUtc); // Aperture's own record stays
    }

    [Fact]
    public async Task Details_of_deleted_videos_are_removed()
    {
        var service = Service(new FakeHttp(_ => throw new InvalidOperationException()));
        await service.SaveAsync(Zoo, default);
        _clock.Now = _clock.Now.AddDays(26);
        var gone = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal((0, 1), await Service(gone).MaintainDetailsAsync(default));
        Assert.Null((await _store.GetAsync(Zoo.Id))!.Video);
    }

    [Fact]
    public async Task Clearing_youtube_data_deletes_records_and_revokes_the_sign_in()
    {
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        _secrets.Write(YouTubeAccount.ClientIdName, "id");
        _secrets.Write(YouTubeAccount.RefreshTokenName, "refresh");
        var service = Service(http);
        await service.SaveAsync(Zoo, default);

        await service.ClearAllAsync(default);

        Assert.Empty(await service.GetEntriesAsync(default));
        Assert.False(service.Account.IsSignedIn);
        Assert.Contains(http.Requests, r => r.Uri == GoogleOAuth.RevocationEndpoint);
    }

    [Fact]
    public async Task Account_lists_need_a_sign_in()
    {
        _secrets.Write(YouTubeService.ApiKeyName, "k");
        var service = Service(new FakeHttp(_ => throw new InvalidOperationException()));
        var error = await Assert.ThrowsAsync<YouTubeApiException>(() => service.PlaylistsAsync(default));
        Assert.Equal(YouTubeProblem.SignInExpired, error.Problem);
    }
}
