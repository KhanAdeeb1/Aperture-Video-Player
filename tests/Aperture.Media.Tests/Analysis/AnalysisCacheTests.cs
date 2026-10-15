using Aperture.Core.Series;
using Aperture.Media.Analysis;
using static Aperture.Media.Tests.Analysis.SyntheticAudio;

namespace Aperture.Media.Tests.Analysis;

public sealed class AnalysisCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aperture-cache-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static EpisodeSignals Sample(string id) => new(
        id,
        TimeSpan.FromSeconds(95.5),
        AudioPrint.Compute(Concat(Silence(2), Music(5, 20), Speech(3, 10)), TimeSpan.FromSeconds(1)),
        TimeSpan.FromSeconds(52),
        [new TimeRange(TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(95.5))],
        [new ChapterMark("Opening", TimeSpan.FromSeconds(3)), new ChapterMark(null, TimeSpan.FromSeconds(23))]);

    [Fact]
    public void Measurements_round_trip_exactly()
    {
        var cache = new AnalysisCache(_folder);
        var original = Sample("0a1b2c");

        cache.Save(original);
        var loaded = cache.Load("0a1b2c");

        Assert.NotNull(loaded);
        Assert.Equal(original.Duration, loaded.Duration);
        Assert.Equal(original.Audio!.Hashes, loaded.Audio!.Hashes);
        Assert.Equal(original.Audio.Quiet, loaded.Audio.Quiet);
        Assert.Contains(true, loaded.Audio.Quiet);
        Assert.Equal(original.Audio.Start, loaded.Audio.Start);
        Assert.Equal(original.DarkFrom, loaded.DarkFrom);
        Assert.Equal(original.Dark, loaded.Dark);
        Assert.Equal(original.Chapters, loaded.Chapters);
    }

    [Fact]
    public void Missing_sound_and_picture_are_remembered_as_missing()
    {
        var cache = new AnalysisCache(_folder);
        cache.Save(Sample("ff") with { Audio = null, Dark = null, Chapters = [] });

        var loaded = cache.Load("ff")!;

        Assert.Null(loaded.Audio);
        Assert.Null(loaded.Dark);
        Assert.Empty(loaded.Chapters);
    }

    [Fact]
    public void A_damaged_or_unknown_file_is_ignored_so_the_episode_is_measured_again()
    {
        var cache = new AnalysisCache(_folder);
        Assert.Null(cache.Load("abc"));

        cache.Save(Sample("abc"));
        var path = Path.Combine(_folder, "abc.signals");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        Assert.Null(cache.Load("abc"));

        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        Assert.Null(cache.Load("abc"));
    }

    [Fact]
    public void Only_hexadecimal_fingerprints_become_file_names()
    {
        var cache = new AnalysisCache(_folder);
        Assert.Throws<ArgumentException>(() => cache.Save(Sample("../escape")));
        Assert.Throws<ArgumentException>(() => cache.Load("..\\escape"));
    }
}
