using Aperture.Core.Library;

namespace Aperture.Core.Tests.Library;

public class WatchProgressTests
{
    private static HistoryEntry Entry(double positionSeconds, double durationSeconds) =>
        new("C:\\v\\a.mp4", "a", TimeSpan.FromSeconds(positionSeconds), TimeSpan.FromSeconds(durationSeconds), DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(5, 3600, false)]      // barely started
    [InlineData(600, 3600, true)]     // part way
    [InlineData(3500, 3600, false)]   // past 95 %
    [InlineData(100, 115, false)]     // within the last 20 s of a short clip
    [InlineData(60, 0, false)]        // unknown duration
    public void In_progress_means_started_and_not_finished(double position, double duration, bool expected) =>
        Assert.Equal(expected, WatchProgress.IsInProgress(Entry(position, duration)));

    [Fact]
    public void Resume_position_is_zero_for_finished_or_unknown_files()
    {
        Assert.Equal(TimeSpan.Zero, WatchProgress.ResumePosition(null));
        Assert.Equal(TimeSpan.Zero, WatchProgress.ResumePosition(Entry(3590, 3600)));
        Assert.Equal(TimeSpan.FromSeconds(600), WatchProgress.ResumePosition(Entry(600, 3600)));
    }

    [Fact]
    public void Progress_is_clamped() =>
        Assert.Equal(1, Entry(4000, 3600).Progress);
}
