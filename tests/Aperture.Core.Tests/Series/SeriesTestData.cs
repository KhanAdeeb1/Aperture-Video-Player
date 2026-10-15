using Aperture.Core.Library;

namespace Aperture.Core.Tests.Series;

internal static class SeriesTestData
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    public static LibraryItem Episode(string title, int? season, int? episode, string? path = null, int? end = null, int? year = null, long id = 0) => new()
    {
        Id = id,
        Path = path ?? $@"D:\TV\{title}\{title}.S{season ?? 1:00}E{episode ?? 0:00}.mkv",
        Title = title,
        Year = year,
        Category = MediaCategory.Episode,
        Season = season,
        Episode = episode,
        EpisodeEnd = end,
        Duration = TimeSpan.FromMinutes(42),
        SizeBytes = 1_000_000_000,
        Width = 1920,
        Height = 1080,
    };

    public static HistoryEntry Played(LibraryItem item, double minutes, DateTimeOffset when) =>
        new(item.Path, item.Title, TimeSpan.FromMinutes(minutes), item.Duration!.Value, when);
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = SeriesTestData.T0;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
