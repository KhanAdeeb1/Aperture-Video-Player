using System.Security.Cryptography;
using Aperture.Core.Library;

namespace Aperture.Media.Tests;

public class LibraryScannerTests
{
    [Fact]
    public async Task A_scan_finds_videos_reads_their_names_and_leaves_files_untouched()
    {
        await using var library = await TestLibrary.CreateAsync();
        var movie = library.AddFile("Paper Moons (2003)/Paper.Moons.2003.1080p.mkv");
        library.AddFile("Harbour Lights/Season 1/Harbour.Lights.S01E02.mkv", seed: 2);
        library.AddFile("notes.txt", seed: 3);
        var before = SHA256.HashData(File.ReadAllBytes(movie));
        var modified = File.GetLastWriteTimeUtc(movie);
        var folder = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Mixed);

        var summary = await library.Scanner().ScanAsync(folder, null, CancellationToken.None);

        Assert.True(summary.Completed);
        Assert.Equal(2, summary.Added);
        var items = await library.Store.GetItemsAsync();
        Assert.Contains(items, i => i is { Category: MediaCategory.Movie, Title: "Paper Moons", Year: 2003 });
        Assert.Contains(items, i => i is { Category: MediaCategory.Episode, Title: "Harbour Lights", Season: 1, Episode: 2 });
        Assert.All(items, i => Assert.NotNull(i.Fingerprint));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(movie)));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(movie));
        Assert.False((await library.Store.GetFoldersAsync()).Single().ScanIncomplete);
    }

    [Fact]
    public async Task Scanning_again_adds_nothing_and_makes_no_duplicates()
    {
        await using var library = await TestLibrary.CreateAsync();
        for (var i = 1; i <= 12; i++)
        {
            library.AddFile($"Clip {i:00}.mp4", seed: (byte)i);
        }

        var folder = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Personal);
        await library.Scanner().ScanAsync(folder, null, CancellationToken.None);

        var again = await library.Scanner().ScanAsync(folder, null, CancellationToken.None);

        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Missing));
        Assert.Equal(12, (await library.Store.GetItemsAsync()).Count);
    }

    [Fact]
    public async Task A_cancelled_scan_keeps_what_it_found_and_the_next_scan_finishes_the_job()
    {
        await using var library = await TestLibrary.CreateAsync();
        for (var i = 1; i <= 120; i++)
        {
            library.AddFile($"Clip {i:000}.mp4", size: 512, seed: (byte)i);
        }

        var folder = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Personal);
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(p =>
        {
            if (p is { Phase: ScanPhase.Finding, Done: 100 })
            {
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.Scanner().ScanAsync(folder, progress, cancel.Token));

        var partial = (await library.Store.GetFoldersAsync()).Single();
        Assert.True(partial.ScanIncomplete);
        Assert.Equal(0, (await library.Store.GetItemsAsync()).Count(i => i.IsMissing));

        var resumed = await library.Scanner().ScanAsync(partial, null, CancellationToken.None);

        Assert.True(resumed.Completed);
        Assert.Equal(0, resumed.Missing);
        var items = await library.Store.GetItemsAsync();
        Assert.Equal(120, items.Count);
        Assert.Equal(120, items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.False((await library.Store.GetFoldersAsync()).Single().ScanIncomplete);
    }

    [Fact]
    public async Task Cancelling_while_inspecting_leaves_the_rest_for_the_next_scan()
    {
        await using var library = await TestLibrary.CreateAsync();
        for (var i = 1; i <= 5; i++)
        {
            library.AddFile($"Clip {i}.mp4", seed: (byte)i);
        }

        var folder = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Personal);
        using var cancel = new CancellationTokenSource();
        var probe = new FakeProbe(onProbe: count =>
        {
            if (count == 2)
            {
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.Scanner(probe).ScanAsync(folder, null, cancel.Token));
        Assert.Equal(4, (await library.Store.GetUnprobedAsync(folder.Id)).Count);

        await library.Scanner(probe).ScanAsync(folder, null, CancellationToken.None);

        Assert.Empty(await library.Store.GetUnprobedAsync(folder.Id));
        Assert.All(await library.Store.GetItemsAsync(), i => Assert.Equal(TimeSpan.FromMinutes(42), i.Duration));
    }

    [Fact]
    public async Task Hidden_files_samples_and_linked_folders_are_skipped()
    {
        await using var library = await TestLibrary.CreateAsync();
        library.AddFile("Film (2010)/Film.2010.mkv");
        library.AddFile("Film (2010)/Film.2010.sample.mkv", seed: 2);
        library.AddFile("Film (2010)/Sample/clip.mkv", seed: 3);
        var hidden = library.AddFile(".hidden/secret.mkv", seed: 4);
        File.SetAttributes(Path.GetDirectoryName(hidden)!, FileAttributes.Hidden | FileAttributes.Directory);
        var outside = Path.Combine(library.Data, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "elsewhere.mkv"), [1, 2, 3]);
        Directory.CreateSymbolicLink(Path.Combine(library.Media, "link"), outside);
        var folder = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Movies);

        await library.Scanner().ScanAsync(folder, null, CancellationToken.None);

        var item = Assert.Single(await library.Store.GetItemsAsync());
        Assert.EndsWith("Film.2010.mkv", item.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audio_is_only_picked_up_where_podcasts_are_expected()
    {
        await using var library = await TestLibrary.CreateAsync();
        library.AddFile("Music/song.mp3");
        library.AddFile("Podcasts/Night Signals/Episode 4.mp3", seed: 2);
        var mixed = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Mixed);

        await library.Scanner().ScanAsync(mixed, null, CancellationToken.None);

        var item = Assert.Single(await library.Store.GetItemsAsync());
        Assert.Equal(MediaCategory.Podcast, item.Category);
    }

    [Fact]
    public async Task A_deleted_file_is_marked_missing_only_after_a_complete_scan()
    {
        await using var library = await TestLibrary.CreateAsync();
        library.AddFile("a.mp4");
        var gone = library.AddFile("b.mp4", seed: 2);
        var folder = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Personal);
        await library.Scanner().ScanAsync(folder, null, CancellationToken.None);
        File.Delete(gone);

        var summary = await library.Scanner().ScanAsync(folder, null, CancellationToken.None);

        Assert.Equal(1, summary.Missing);
        Assert.Single(await library.Store.GetItemsAsync());
        File.WriteAllBytes(gone, [9, 9, 9]);
        await library.Scanner().ScanAsync(folder, null, CancellationToken.None);
        Assert.Equal(2, (await library.Store.GetItemsAsync()).Count);
    }

    [Fact]
    public async Task An_unavailable_folder_keeps_every_item()
    {
        await using var library = await TestLibrary.CreateAsync();
        var drive = Path.Combine(library.Media, "drive");
        library.AddFile("drive/a.mp4");
        var folder = await library.Store.AddFolderAsync(drive, LibraryFolderKind.Personal);
        await library.Scanner().ScanAsync(folder, null, CancellationToken.None);
        Directory.Move(drive, drive + "-unplugged");

        var summary = await library.Scanner().ScanAsync(folder, null, CancellationToken.None);

        Assert.True(summary.FolderUnavailable);
        Assert.False(summary.Completed);
        Assert.Single(await library.Store.GetItemsAsync());
    }

    [Fact]
    public async Task A_changed_file_is_inspected_again()
    {
        await using var library = await TestLibrary.CreateAsync();
        var path = library.AddFile("a.mp4");
        var folder = await library.Store.AddFolderAsync(library.Media, LibraryFolderKind.Personal);
        await library.Scanner().ScanAsync(folder, null, CancellationToken.None);
        var first = Assert.Single(await library.Store.GetItemsAsync()).Fingerprint;
        library.AddFile("a.mp4", size: 8192, seed: 7);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var summary = await library.Scanner().ScanAsync(folder, null, CancellationToken.None);

        Assert.Equal(1, summary.Updated);
        Assert.NotEqual(first, Assert.Single(await library.Store.GetItemsAsync()).Fingerprint);
    }

    private sealed class FakeProbe(Action<int> onProbe) : IMediaProbe
    {
        private int _count;

        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            onProbe(++_count);
            return Task.FromResult<MediaProbeResult?>(new MediaProbeResult(TimeSpan.FromMinutes(42), 1920, 1080, "h264"));
        }
    }
}
