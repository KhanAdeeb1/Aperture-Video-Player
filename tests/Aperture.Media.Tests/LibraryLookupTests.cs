using System.Net;
using Aperture.Core.Library;

namespace Aperture.Media.Tests;

public class LibraryLookupTests
{
    private static async Task<(TestLibrary Library, LibraryItem Movie, LibraryItem Other)> SetUpAsync()
    {
        var library = await TestLibrary.CreateAsync();
        library.AddFile("Paper Moons (2003)/Paper.Moons.2003.mkv");
        library.AddFile("Grey Harbour (2011)/Grey.Harbour.2011.mkv", seed: 2);
        var folder = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Movies);
        await library.Scanner().ScanAsync(folder, null, CancellationToken.None);
        var items = await library.Store.GetItemsAsync();
        return (library, items.Single(i => i.Title == "Paper Moons"), items.Single(i => i.Title == "Grey Harbour"));
    }

    private static LibraryLookup Lookup(TestLibrary library, IMetadataProvider provider, ArtworkCache? artwork = null) =>
        new(library.Store, provider, artwork, (_, m) => library.Log.Add(m)) { Spacing = TimeSpan.Zero };

    [Fact]
    public async Task Confident_matches_are_applied_with_artwork_and_uncertain_ones_kept_as_suggestions()
    {
        var (library, movie, other) = await SetUpAsync();
        await using var _ = library;
        var provider = new FakeProvider(item => item.Title == "Paper Moons"
            ? new OnlineMatch("TMDB", "movie/11", "Paper Moons", 2003, null, "A quiet film.", "https://image.example/pm.jpg", MatchConfidence.Certain)
            : new OnlineMatch("TMDB", "movie/99", "Grey Harbor", 2012, null, null, null, MatchConfidence.Uncertain));
        var http = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xFF, 0xD8, 0xFF]) { Headers = { ContentType = new("image/jpeg") } },
        });
        var artwork = new ArtworkCache(http.Client(), Path.Combine(library.Data, "Artwork"));

        var summary = await Lookup(library, provider, artwork).RunAsync(null, CancellationToken.None);

        Assert.Equal((1, 1, 0), (summary.Applied, summary.Suggested, summary.NotFound));
        var applied = await library.Store.FindByPathAsync(movie.Path);
        Assert.Equal(MetadataSource.Online, applied!.Source);
        Assert.Equal("A quiet film.", applied.Overview);
        Assert.True(File.Exists(applied.ArtworkPath));
        Assert.StartsWith(library.Data, applied.ArtworkPath, StringComparison.Ordinal);
        var suggested = await library.Store.FindByPathAsync(other.Path);
        Assert.Equal("Grey Harbour", suggested!.Title);
        Assert.Equal("Grey Harbor", suggested.SuggestedMatch?.Title);
        Assert.Empty(await library.Store.GetUnmatchedAsync(10));
    }

    [Fact]
    public async Task Accepting_a_suggestion_applies_it()
    {
        var (library, _, other) = await SetUpAsync();
        await using var _l = library;
        var provider = new FakeProvider(item => new OnlineMatch("TMDB", "movie/99", item.Title + " Bay", 2011, null, null, null, MatchConfidence.Uncertain));
        var lookup = Lookup(library, provider);
        await lookup.RunAsync(null, CancellationToken.None);

        await lookup.AcceptSuggestionAsync((await library.Store.FindByPathAsync(other.Path))!, CancellationToken.None);

        var item = await library.Store.FindByPathAsync(other.Path);
        Assert.Equal("Grey Harbour Bay", item!.Title);
        Assert.Null(item.SuggestedMatch);
    }

    [Fact]
    public async Task Manual_corrections_are_never_overwritten_or_looked_up()
    {
        var (library, movie, _) = await SetUpAsync();
        await using var _l = library;
        await library.Store.SaveManualDetailsAsync(movie.Id, new ManualDetails(MediaCategory.Movie, "My Title", 2000, null, null, null));
        var provider = new FakeProvider(item => new OnlineMatch("TMDB", "movie/1", "Other", 2003, null, null, null, MatchConfidence.Certain));

        await Lookup(library, provider).RunAsync(null, CancellationToken.None);

        Assert.DoesNotContain(provider.Seen, t => t == "My Title");
        Assert.Equal("My Title", (await library.Store.FindByPathAsync(movie.Path))!.Title);
    }

    [Fact]
    public async Task Without_a_network_the_pass_stops_quietly_and_tries_again_later()
    {
        var (library, _, _) = await SetUpAsync();
        await using var _l = library;
        var provider = new FakeProvider(_ => throw new HttpRequestException("No route to host"));

        var summary = await Lookup(library, provider).RunAsync(null, CancellationToken.None);

        Assert.True(summary.Offline);
        Assert.Equal(2, (await library.Store.GetUnmatchedAsync(10)).Count);
    }

    [Fact]
    public async Task A_refused_key_stops_the_pass_and_is_reported()
    {
        var (library, _, _) = await SetUpAsync();
        await using var _l = library;
        var provider = new FakeProvider(_ => throw new MetadataKeyRejectedException("no"));

        var summary = await Lookup(library, provider).RunAsync(null, CancellationToken.None);

        Assert.True(summary.KeyRejected);
        Assert.Single(provider.Seen);
    }

    private sealed class FakeProvider(Func<LibraryItem, OnlineMatch?> find) : IMetadataProvider
    {
        public List<string> Seen { get; } = [];

        public string Name => "Fake";

        public Task<OnlineMatch?> FindAsync(LibraryItem item, CancellationToken cancellationToken)
        {
            Seen.Add(item.Title);
            return Task.FromResult(find(item));
        }
    }
}
