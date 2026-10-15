using Aperture.Core.Library;
using Aperture.Data;

namespace Aperture.Media.Tests;

public class LibraryCoordinatorTests
{
    private static LibraryCoordinator Coordinator(TestLibrary library, Func<LibraryLookup?>? lookup = null) =>
        new(
            library.Store,
            new SqliteItemStateStore(library.Database),
            new SqlitePlaybackHistory(library.Database),
            () => null,
            lookup ?? (() => null),
            TimeProvider.System,
            (_, m) => library.Log.Add(m));

    [Fact]
    public async Task Adding_a_folder_scans_it_in_the_background_and_reports_progress_then_idle()
    {
        await using var library = await TestLibrary.CreateAsync();
        library.AddFile("Paper Moons (2003)/Paper.Moons.2003.mkv");
        library.AddFile("Paper Moons (2003)/Paper.Moons.2003.720p.mkv", seed: 9);
        using var coordinator = Coordinator(library);
        var reports = new List<ScanProgress?>();
        var changes = 0;
        coordinator.ProgressChanged += (_, p) => { lock (reports) { reports.Add(p); } };
        coordinator.Changed += (_, _) => Interlocked.Increment(ref changes);

        await coordinator.AddFolderAsync(library.Media, LibraryFolderKind.Movies);
        await coordinator.WhenIdle();

        Assert.False(coordinator.IsBusy);
        Assert.Null(reports[^1]);
        Assert.Contains(reports, r => r?.Phase == ScanPhase.Finding);
        Assert.True(changes > 0);
        var snapshot = await coordinator.LoadAsync();
        var group = Assert.Single(snapshot.Groups);
        Assert.Single(group.Others);
        Assert.Equal(1, snapshot.Count(MediaCategory.Movie));
        Assert.Same(group, snapshot.GroupOf(group.Others[0].Path.ToUpperInvariant()));
        Assert.Null(snapshot.GroupOf(Path.Combine(library.Media, "elsewhere.mkv")));
    }

    [Fact]
    public async Task Asking_for_several_scans_at_once_runs_them_one_after_another()
    {
        await using var library = await TestLibrary.CreateAsync();
        library.AddFile("a/one.mp4");
        library.AddFile("b/two.mp4", seed: 2);
        await library.Store.AddFolderAsync(Path.Combine(library.Media, "a"), LibraryFolderKind.Personal);
        await library.Store.AddFolderAsync(Path.Combine(library.Media, "b"), LibraryFolderKind.Personal);
        using var coordinator = Coordinator(library);

        coordinator.RequestScan();
        coordinator.RequestScan();
        coordinator.RequestScan(1);
        await coordinator.WhenIdle();

        Assert.Equal(2, (await library.Store.GetItemsAsync()).Count);
        Assert.All(await library.Store.GetFoldersAsync(), f => Assert.False(f.ScanIncomplete));
    }

    [Fact]
    public async Task Watched_marks_and_corrections_show_up_in_the_next_snapshot_and_reset_restores_the_file_name()
    {
        await using var library = await TestLibrary.CreateAsync();
        library.AddFile("Grey.Harbour.2011.mkv");
        using var coordinator = Coordinator(library);
        await coordinator.AddFolderAsync(library.Media, LibraryFolderKind.Movies);
        await coordinator.WhenIdle();
        var item = (await coordinator.LoadAsync()).Groups.Single().Best;

        await coordinator.SetWatchedAsync([item.Path], true);
        await coordinator.SaveDetailsAsync(item, new ManualDetails(MediaCategory.Movie, "The Grey Harbour", 2011, null, null, null));
        var snapshot = await coordinator.LoadAsync();

        var entry = Assert.Single(snapshot.Query(null, WatchFilter.Watched, LibrarySort.Title, "grey"));
        Assert.Equal("The Grey Harbour", entry.Item.Title);
        Assert.Equal(MetadataSource.Manual, entry.Item.Source);

        await coordinator.ResetDetailsAsync(entry.Item);
        Assert.Equal("Grey Harbour", (await coordinator.LoadAsync()).Groups.Single().Best.Title);
    }

    [Fact]
    public async Task Removing_a_folder_forgets_its_items_but_not_the_files()
    {
        await using var library = await TestLibrary.CreateAsync();
        var file = library.AddFile("clip.mp4");
        using var coordinator = Coordinator(library);
        var folder = await coordinator.AddFolderAsync(library.Media, LibraryFolderKind.Personal);
        await coordinator.WhenIdle();

        await coordinator.RemoveFolderAsync(folder.Id);

        Assert.True((await coordinator.LoadAsync()).IsEmpty);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Cancelling_returns_to_idle_and_keeps_the_library_usable()
    {
        await using var library = await TestLibrary.CreateAsync();
        for (var i = 0; i < 300; i++)
        {
            library.AddFile($"clip {i:000}.mp4", size: 256, seed: (byte)i);
        }

        using var coordinator = Coordinator(library);
        await coordinator.AddFolderAsync(library.Media, LibraryFolderKind.Personal);
        coordinator.Cancel();
        await coordinator.WhenIdle();

        Assert.False(coordinator.IsBusy);
        Assert.Null(coordinator.Progress);
        coordinator.RequestScan();
        await coordinator.WhenIdle();
        Assert.Equal(300, (await library.Store.GetItemsAsync()).Count);
    }
}
