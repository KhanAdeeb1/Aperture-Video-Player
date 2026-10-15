using Aperture.Core.Library;
using Aperture.Core.Server;

namespace Aperture.Core.Tests.Server;

public class SharedMediaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<long, string> Shared = new() { [1] = @"D:\Films" };

    private static LibraryItem Item(string path, long folder = 1, bool missing = false, TimeSpan? duration = null) => new()
    {
        Id = 7,
        Path = path,
        FolderId = folder,
        Title = "Night Market",
        IsMissing = missing,
        Duration = duration,
    };

    [Fact]
    public void Items_in_shared_folders_are_shared() =>
        Assert.True(SharedMedia.IsShared(Item(@"D:\Films\Night Market (2021)\Night Market.mkv"), Shared));

    [Fact]
    public void Items_in_other_folders_are_not() =>
        Assert.False(SharedMedia.IsShared(Item(@"E:\Private\clip.mp4", folder: 2), Shared));

    [Fact]
    public void An_item_claiming_a_shared_folder_but_lying_outside_it_is_not_shared()
    {
        Assert.False(SharedMedia.IsShared(Item(@"C:\Users\me\secret.mp4"), Shared));
        Assert.False(SharedMedia.IsShared(Item(@"D:\Films2\other.mp4"), Shared));
        Assert.False(SharedMedia.IsShared(Item(@"D:\Films"), Shared));
    }

    [Theory]
    [InlineData(@"D:\Films\..\Private\secret.mp4")]
    [InlineData(@"D:\Films\.\clip.mp4")]
    [InlineData("D:/Films/../Private/secret.mp4")]
    public void Relative_segments_are_refused(string path) =>
        Assert.False(SharedMedia.IsShared(Item(path), Shared));

    [Fact]
    public void Missing_items_are_not_shared() =>
        Assert.False(SharedMedia.IsShared(Item(@"D:\Films\gone.mkv", missing: true), Shared));

    [Fact]
    public void Progress_uses_the_library_length_and_clamps_the_position()
    {
        var entry = RemoteProgress.ToEntry(Item(@"D:\Films\a.mkv", duration: TimeSpan.FromMinutes(90)), positionSeconds: 99_999, reportedDurationSeconds: 5, Now);

        Assert.NotNull(entry);
        Assert.Equal(TimeSpan.FromMinutes(90), entry.Duration);
        Assert.Equal(TimeSpan.FromMinutes(90), entry.Position);
        Assert.Equal(Now, entry.LastPlayedUtc);
        Assert.Equal(@"D:\Films\a.mkv", entry.Path);
    }

    [Fact]
    public void Progress_falls_back_to_the_reported_length_within_reason()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), RemoteProgress.ToEntry(Item(@"D:\Films\a.mkv"), 30, 600, Now)!.Duration);
        Assert.Null(RemoteProgress.ToEntry(Item(@"D:\Films\a.mkv"), 30, null, Now));
        Assert.Null(RemoteProgress.ToEntry(Item(@"D:\Films\a.mkv"), 30, TimeSpan.FromDays(3).TotalSeconds, Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Nonsense_positions_are_ignored(double position) =>
        Assert.Null(RemoteProgress.ToEntry(Item(@"D:\Films\a.mkv", duration: TimeSpan.FromMinutes(90)), position, null, Now));
}
