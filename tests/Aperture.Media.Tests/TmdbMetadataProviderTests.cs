using System.Net;
using Aperture.Core.Library;

namespace Aperture.Media.Tests;

public class TmdbMetadataProviderTests
{
    private const string MovieResults = """
        {"results":[
          {"id":11,"title":"Paper Moons","release_date":"2003-05-01","overview":"A quiet film.","backdrop_path":"/pm.jpg","popularity":12.5},
          {"id":12,"title":"Paper Moons Rising","release_date":"2019-02-02","popularity":40}
        ]}
        """;

    private static LibraryItem Movie(string title, int? year) =>
        new() { Path = "/m/x.mkv", Category = MediaCategory.Movie, Title = title, Year = year };

    [Fact]
    public async Task Same_title_and_year_is_a_certain_match_with_a_backdrop()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(MovieResults));
        var provider = new TmdbMetadataProvider(http.Client(), "abc123");

        var match = await provider.FindAsync(Movie("Paper Moons", 2003), CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(MatchConfidence.Certain, match.Confidence);
        Assert.Equal("movie/11", match.ServiceId);
        Assert.Equal(TmdbMetadataProvider.ImageRoot + "/pm.jpg", match.ArtworkUrl);
        var query = Assert.Single(http.Requests).RequestUri!.Query;
        Assert.Contains("query=Paper%20Moons", query, StringComparison.Ordinal);
        Assert.Contains("year=2003", query, StringComparison.Ordinal);
        Assert.Contains("api_key=abc123", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_read_access_token_is_sent_as_a_bearer_header_not_in_the_address()
    {
        var token = "eyJhbGciOiJIUzI1NiJ9." + new string('a', 60) + ".signature";
        var http = new FakeHttp(_ => FakeHttp.Json(MovieResults));

        await new TmdbMetadataProvider(http.Client(), token).FindAsync(Movie("Paper Moons", 2003), CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.DoesNotContain("api_key", request.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_loose_title_match_comes_back_uncertain()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(MovieResults));

        var match = await new TmdbMetadataProvider(http.Client(), "k").FindAsync(Movie("Paper Moon", 1973), CancellationToken.None);

        Assert.Equal(MatchConfidence.Uncertain, match?.Confidence);
    }

    [Fact]
    public async Task No_results_with_a_year_tries_again_without_it()
    {
        var http = new FakeHttp(r => r.RequestUri!.Query.Contains("year=", StringComparison.Ordinal)
            ? FakeHttp.Json("""{"results":[]}""")
            : FakeHttp.Json(MovieResults));

        var match = await new TmdbMetadataProvider(http.Client(), "k").FindAsync(Movie("Paper Moons", 2004), CancellationToken.None);

        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(MatchConfidence.Likely, match?.Confidence);
    }

    [Fact]
    public async Task Episodes_get_the_episode_title_and_still()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath.Contains("/episode/", StringComparison.Ordinal)
            ? FakeHttp.Json("""{"name":"The Pilot Light","overview":"It begins.","still_path":"/still.jpg"}""")
            : FakeHttp.Json("""{"results":[{"id":77,"name":"Harbour Lights","first_air_date":"2018-01-01","backdrop_path":"/hl.jpg"}]}"""));
        var item = new LibraryItem { Path = "/t/x.mkv", Category = MediaCategory.Episode, Title = "Harbour Lights", Season = 1, Episode = 2 };

        var match = await new TmdbMetadataProvider(http.Client(), "k").FindAsync(item, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("The Pilot Light", match.EpisodeTitle);
        Assert.Equal(TmdbMetadataProvider.ImageRoot + "/still.jpg", match.ArtworkUrl);
        Assert.EndsWith("tv/77/season/1/episode/2", http.Requests[1].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Personal_videos_are_never_sent()
    {
        var http = new FakeHttp(_ => throw new InvalidOperationException("No request expected."));
        var item = new LibraryItem { Path = "/p/x.mp4", Category = MediaCategory.Personal, Title = "Beach day" };

        Assert.Null(await new TmdbMetadataProvider(http.Client(), "k").FindAsync(item, CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_refused_key_is_reported()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"status_code":7}""", HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<MetadataKeyRejectedException>(() =>
            new TmdbMetadataProvider(http.Client(), "bad").FindAsync(Movie("Paper Moons", 2003), CancellationToken.None));
    }

    [Theory]
    [InlineData("paper moons", "paper moons", 1.0)]
    [InlineData("night", "nacht", 0.25)]
    [InlineData("ab", "cd", 0.0)]
    public void Similarity_is_dice_over_letter_pairs(string a, string b, double expected) =>
        Assert.Equal(expected, TmdbMetadataProvider.Similarity(a, b), 3);
}
