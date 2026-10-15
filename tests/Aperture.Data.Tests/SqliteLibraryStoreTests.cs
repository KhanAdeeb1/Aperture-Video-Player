using System.Diagnostics;
using Aperture.Core.Library;

namespace Aperture.Data.Tests;

public class SqliteLibraryStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static ScannedFile File(string path, long size = 100, int minute = 0, string? title = null) =>
        new(path, size, T0.AddMinutes(minute), new ParsedName(MediaCategory.Movie, title ?? Path.GetFileNameWithoutExtension(path), 2019, Confidence: MatchConfidence.Certain));

    [Fact]
    public async Task Repeated_scans_never_duplicate_items()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteLibraryStore(temp.Database);
        var folder = await store.AddFolderAsync(@"D:\Films", LibraryFolderKind.Movies);

        await store.UpsertAsync(folder.Id, [File(@"D:\Films\a.mkv"), File(@"D:\Films\b.mkv")], T0);
        await store.UpsertAsync(folder.Id, [File(@"D:\Films\A.MKV"), File(@"D:\Films\b.mkv")], T0.AddHours(1));

        Assert.Equal(2, (await store.GetItemsAsync()).Count);
        Assert.Equal(2, Assert.Single(await store.GetFoldersAsync()).ItemCount);
    }

    [Fact]
    public async Task Changed_files_are_inspected_again_and_unchanged_ones_keep_their_details()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteLibraryStore(temp.Database);
        var folder = await store.AddFolderAsync(@"D:\Films", LibraryFolderKind.Movies);
        await store.UpsertAsync(folder.Id, [File(@"D:\Films\a.mkv"), File(@"D:\Films\b.mkv")], T0);
        var index = await store.GetIndexAsync(folder.Id);
        foreach (var file in index)
        {
            await store.SaveProbeAsync(file.Id, new MediaProbeResult(TimeSpan.FromMinutes(90), 1920, 1080, "h264"), "fp");
        }

        Assert.Empty(await store.GetUnprobedAsync(folder.Id));
        await store.UpsertAsync(folder.Id, [File(@"D:\Films\a.mkv", size: 200), File(@"D:\Films\b.mkv")], T0.AddHours(1));

        var unprobed = Assert.Single(await store.GetUnprobedAsync(folder.Id));
        Assert.Equal(@"D:\Films\a.mkv", unprobed.Path);
        var b = await store.FindByPathAsync(@"D:\Films\b.mkv");
        Assert.Equal("fp", b!.Fingerprint);
        Assert.Equal(1080, b.Height);
        Assert.Null((await store.FindByPathAsync(@"D:\Films\a.mkv"))!.Fingerprint);
    }

    [Fact]
    public async Task Manual_details_survive_rescans_and_online_lookups()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteLibraryStore(temp.Database);
        var folder = await store.AddFolderAsync(@"D:\Films", LibraryFolderKind.Movies);
        await store.UpsertAsync(folder.Id, [File(@"D:\Films\a.mkv", title: "A Wrong Guess")], T0);
        var item = (await store.FindByPathAsync(@"D:\Films\a.mkv"))!;

        await store.SaveManualDetailsAsync(item.Id, new ManualDetails(MediaCategory.Episode, "Harbour Watch", null, 1, 3, "Low Water"));
        await store.UpsertAsync(folder.Id, [File(@"D:\Films\a.mkv", size: 999, title: "A Wrong Guess")], T0.AddHours(1));
        await store.SaveOnlineMatchAsync(item.Id, new OnlineMatch("TMDB", "tv/1", "Something Else", 2001, null, null, null, MatchConfidence.Certain), apply: true, artworkPath: null);

        var after = (await store.FindByPathAsync(@"D:\Films\a.mkv"))!;
        Assert.Equal(("Harbour Watch", MediaCategory.Episode, 1, 3, "Low Water"), (after.Title, after.Category, after.Season!.Value, after.Episode!.Value, after.EpisodeTitle));
        Assert.Equal(MetadataSource.Manual, after.Source);
        Assert.Equal(MatchConfidence.Certain, after.Confidence);
        Assert.DoesNotContain(after, await store.GetUnmatchedAsync(10));

        await store.ResetDetailsAsync(item.Id, new ParsedName(MediaCategory.Movie, "a", null));
        var reset = (await store.FindByPathAsync(@"D:\Films\a.mkv"))!;
        Assert.Equal((MetadataSource.FileName, "a", MediaCategory.Movie), (reset.Source, reset.Title, reset.Category));
    }

    [Fact]
    public async Task Uncertain_online_matches_are_kept_as_suggestions_only()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteLibraryStore(temp.Database);
        var folder = await store.AddFolderAsync(@"D:\Films", LibraryFolderKind.Movies);
        await store.UpsertAsync(folder.Id, [File(@"D:\Films\a.mkv", title: "Glass Garden"), File(@"D:\Films\b.mkv", title: "Paper Moons")], T0);
        var a = (await store.FindByPathAsync(@"D:\Films\a.mkv"))!;
        var b = (await store.FindByPathAsync(@"D:\Films\b.mkv"))!;

        await store.SaveOnlineMatchAsync(a.Id, new OnlineMatch("TMDB", "movie/9", "Glass Gardens", 1999, null, "About glass.", "https://image/x.jpg", MatchConfidence.Uncertain), apply: false, artworkPath: null);
        await store.SaveOnlineMatchAsync(b.Id, new OnlineMatch("TMDB", "movie/10", "Paper Moons", 2019, null, "About moons.", null, MatchConfidence.Certain), apply: true, artworkPath: @"C:\cache\b.jpg");

        var aAfter = (await store.FindByPathAsync(a.Path))!;
        Assert.Equal("Glass Garden", aAfter.Title);
        Assert.Equal(MetadataSource.FileName, aAfter.Source);
        Assert.Equal("Glass Gardens", aAfter.SuggestedMatch!.Title);
        Assert.Equal(MatchConfidence.Uncertain, aAfter.SuggestedMatch.Confidence);

        var bAfter = (await store.FindByPathAsync(b.Path))!;
        Assert.Equal((MetadataSource.Online, "About moons.", @"C:\cache\b.jpg"), (bAfter.Source, bAfter.Overview, bAfter.ArtworkPath));
        Assert.Null(bAfter.SuggestedMatch);
        Assert.Empty(await store.GetUnmatchedAsync(10));
    }

    [Fact]
    public async Task Only_a_complete_scan_marks_files_missing_and_they_return_when_seen()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteLibraryStore(temp.Database);
        var folder = await store.AddFolderAsync(@"D:\Films", LibraryFolderKind.Movies);
        await store.UpsertAsync(folder.Id, [File(@"D:\Films\a.mkv"), File(@"D:\Films\b.mkv")], T0);

        var second = T0.AddHours(1);
        var a = (await store.GetIndexAsync(folder.Id)).Single(f => f.Path.EndsWith("a.mkv", StringComparison.Ordinal));
        await store.TouchAsync([a.Id], second);
        Assert.Equal(1, await store.MarkMissingAsync(folder.Id, second));
        Assert.Equal([@"D:\Films\a.mkv"], (await store.GetItemsAsync()).Select(i => i.Path));

        await store.UpsertAsync(folder.Id, [File(@"D:\Films\b.mkv")], T0.AddHours(2));
        Assert.Equal(2, (await store.GetItemsAsync()).Count);
    }

    [Fact]
    public async Task Removing_a_folder_forgets_its_items_only()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteLibraryStore(temp.Database);
        var films = await store.AddFolderAsync(@"D:\Films", LibraryFolderKind.Movies);
        var home = await store.AddFolderAsync(@"D:\Home", LibraryFolderKind.Personal);
        await store.UpsertAsync(films.Id, [File(@"D:\Films\a.mkv")], T0);
        await store.UpsertAsync(home.Id, [File(@"D:\Home\b.mp4")], T0);

        await store.RemoveFolderAsync(films.Id);

        Assert.Equal([@"D:\Home\b.mp4"], (await store.GetItemsAsync()).Select(i => i.Path));
        Assert.Equal([@"D:\Home"], (await store.GetFoldersAsync()).Select(f => f.Path));
    }

    [Fact]
    public async Task Scan_state_records_incomplete_scans()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteLibraryStore(temp.Database);
        var folder = await store.AddFolderAsync(@"D:\Films", LibraryFolderKind.Movies);
        await store.MarkScanStartedAsync(folder.Id, T0);
        Assert.True(Assert.Single(await store.GetFoldersAsync()).ScanIncomplete);
        await store.MarkScanCompletedAsync(folder.Id, T0.AddMinutes(1));
        Assert.False(Assert.Single(await store.GetFoldersAsync()).ScanIncomplete);
    }

    [Fact]
    public async Task A_large_library_loads_quickly()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteLibraryStore(temp.Database);
        var folder = await store.AddFolderAsync(@"D:\Big", LibraryFolderKind.Mixed);
        var files = Enumerable.Range(0, 20_000).Select(i => File($@"D:\Big\Film {i:00000}.mkv", size: i, title: $"Film {i}")).ToList();

        var write = Stopwatch.StartNew();
        await store.UpsertAsync(folder.Id, files, T0);
        write.Stop();

        var read = Stopwatch.StartNew();
        var items = await store.GetItemsAsync();
        var groups = CopyGroup.Build(items);
        var results = LibraryQuery.Run(groups, new Dictionary<string, HistoryEntry>(), new Dictionary<string, bool>(), null, WatchFilter.All, LibrarySort.Title, "film 19");
        read.Stop();

        Assert.Equal(20_000, items.Count);
        Assert.NotEmpty(results);
        Console.WriteLine($"20,000 items: write {write.ElapsedMilliseconds} ms, load + group + search {read.ElapsedMilliseconds} ms");
        // Generous bounds for slow CI machines; the point is seconds, not minutes.
        Assert.True(write.Elapsed < TimeSpan.FromSeconds(30), $"write took {write.Elapsed}");
        Assert.True(read.Elapsed < TimeSpan.FromSeconds(5), $"load took {read.Elapsed}");
    }
}
