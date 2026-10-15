using Aperture.Core.Media;

namespace Aperture.Core.Library;

/// <summary>
/// Decides when the position of the playing file is written to history: at most every
/// <see cref="Interval"/> during playback, and immediately on pause, end, or leaving the
/// player. Nothing is written while the user has turned history off.
/// </summary>
public sealed class ProgressRecorder(IPlaybackHistory history, Func<bool> isEnabled, TimeProvider clock)
{
    /// <summary>Minimum time between periodic saves.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private DateTimeOffset _lastSaved = DateTimeOffset.MinValue;

    /// <summary>Called regularly while playing; saves only when <see cref="Interval"/> has passed.</summary>
    public Task TickAsync(string path, TimeSpan position, TimeSpan duration, CancellationToken cancellationToken = default) =>
        clock.GetUtcNow() - _lastSaved >= Interval
            ? SaveNowAsync(path, position, duration, cancellationToken)
            : Task.CompletedTask;

    /// <summary>Saves straight away (pause, end, closing the file or the window).</summary>
    public async Task SaveNowAsync(string path, TimeSpan position, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!isEnabled() || duration <= TimeSpan.Zero)
        {
            return;
        }

        var now = clock.GetUtcNow();
        _lastSaved = now;
        var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
        await history.SaveAsync(
            new HistoryEntry(path, MediaFileTypes.TitleFromPath(path), clamped, duration, now),
            cancellationToken).ConfigureAwait(false);
    }
}
