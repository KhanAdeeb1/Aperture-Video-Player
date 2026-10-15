using Aperture.Core.Library;

namespace Aperture.Core.Tests.Library;

public class ProgressRecorderTests
{
    private static readonly string FilePath = Path.Combine("Videos", "Night Drive.mp4");

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryHistory : IPlaybackHistory
    {
        public List<HistoryEntry> Saved { get; } = [];

        public Task<IReadOnlyList<HistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoryEntry>>(Saved);

        public Task<HistoryEntry?> FindAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Saved.LastOrDefault(e => e.Path == path));

        public Task SaveAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
        {
            Saved.Add(entry);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero));
    private readonly MemoryHistory _history = new();

    [Fact]
    public async Task Ticks_are_throttled_to_the_interval()
    {
        var recorder = new ProgressRecorder(_history, () => true, _clock);
        var duration = TimeSpan.FromMinutes(10);

        await recorder.TickAsync(FilePath, TimeSpan.FromSeconds(1), duration);
        _clock.Now += TimeSpan.FromSeconds(2);
        await recorder.TickAsync(FilePath, TimeSpan.FromSeconds(3), duration);
        _clock.Now += ProgressRecorder.Interval;
        await recorder.TickAsync(FilePath, TimeSpan.FromSeconds(8), duration);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8)], _history.Saved.Select(e => e.Position));
        Assert.Equal("Night Drive", _history.Saved[0].Title);
    }

    [Fact]
    public async Task Nothing_is_saved_when_history_is_off_or_duration_unknown()
    {
        var off = new ProgressRecorder(_history, () => false, _clock);
        await off.SaveNowAsync(FilePath, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));

        var on = new ProgressRecorder(_history, () => true, _clock);
        await on.SaveNowAsync(FilePath, TimeSpan.FromSeconds(30), TimeSpan.Zero);

        Assert.Empty(_history.Saved);
    }

    [Fact]
    public async Task Position_is_clamped_to_the_duration()
    {
        var recorder = new ProgressRecorder(_history, () => true, _clock);
        await recorder.SaveNowAsync(FilePath, TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), _history.Saved.Single().Position);
    }
}
