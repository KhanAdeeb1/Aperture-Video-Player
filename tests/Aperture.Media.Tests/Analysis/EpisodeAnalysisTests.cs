using Aperture.Core.Library;
using Aperture.Core.Series;
using Aperture.Data;
using Aperture.Media.Analysis;
using static Aperture.Media.Tests.Analysis.SyntheticAudio;

namespace Aperture.Media.Tests.Analysis;

/// <summary>
/// Background analysis with a stand-in for libmpv that serves synthetic 90-second episodes:
/// a cold open of its own length, the show's 20-second theme, a story, and 20 seconds of end
/// music over a dark picture.
/// </summary>
public sealed class EpisodeAnalysisTests
{
    private static readonly double[] ColdOpens = [10, 3, 15, 6, 12];

    private sealed class FakeSignals : IMediaSignals
    {
        private readonly Dictionary<string, short[]> _sound = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Decoded { get; } = [];

        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, short[] sound) => _sound[path] = sound;

        public Task<AudioSamples?> ReadAudioAsync(string path, TimeSpan start, TimeSpan? length, int sampleRate, CancellationToken cancellationToken = default)
        {
            lock (Decoded)
            {
                Decoded.Add(path);
            }

            if (Unreadable.Contains(path))
            {
                throw new IOException("damaged");
            }

            return Task.FromResult<AudioSamples?>(new AudioSamples(sampleRate, TimeSpan.Zero, _sound[path]));
        }

        public Task<IReadOnlyList<TimeRange>?> FindDarkStretchesAsync(string path, TimeSpan start, CancellationToken cancellationToken = default)
        {
            var end = Length(_sound[path]);
            return Task.FromResult<IReadOnlyList<TimeRange>?>([new TimeRange(end - TimeSpan.FromSeconds(20), end)]);
        }

        public Task<IReadOnlyList<ChapterMark>> ReadChaptersAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChapterMark>>([]);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(TestLibrary library)
        {
            Library = library;
            Store = new SqliteSeriesStore(library.Database);
            Cache = new AnalysisCache(Path.Combine(library.Data, "Analysis"));
            Coordinator = new EpisodeAnalysisCoordinator(Store, () => Signals, Cache, TimeProvider.System, (_, _) => { });
        }

        public TestLibrary Library { get; }

        public SqliteSeriesStore Store { get; }

        public AnalysisCache Cache { get; }

        public FakeSignals Signals { get; } = new();

        public EpisodeAnalysisCoordinator Coordinator { get; }

        public List<LibraryItem> Items { get; } = [];

        public static async Task<Rig> CreateAsync() => new(await TestLibrary.CreateAsync());

        public LibraryItem AddEpisode(int number, string folder = "Night Shift")
        {
            var cold = ColdOpens[(number - 1) % ColdOpens.Length];
            var sound = Release(Concat(Speech(number * 10 + 1, cold), Music(100, 20), Speech(number * 10 + 2, 70 - 20 - cold), Music(200, 20)), seed: number);
            var path = Path.Combine(Library.Media, folder, $"Night.Shift.S01E{number:00}.mkv");
            Signals.Add(path, sound);
            var item = new LibraryItem
            {
                Path = path,
                Title = "Night Shift",
                Category = MediaCategory.Episode,
                Season = 1,
                Episode = number,
                Fingerprint = $"{folder.Length:x2}{number:x4}aa",
                Duration = Length(sound),
                Probed = true,
            };
            Items.Add(item);
            return item;
        }

        public SeriesCatalog Catalog() => SeriesCatalog.Build(CopyGroup.Build(Items));

        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            await Library.DisposeAsync();
        }
    }

    [Fact]
    public async Task Every_episode_is_analysed_and_each_file_is_decoded_only_once()
    {
        await using var rig = await Rig.CreateAsync();
        var episodes = Enumerable.Range(1, 4).Select(n => rig.AddEpisode(n)).ToList();

        rig.Coordinator.Update(rig.Catalog(), background: true);
        await rig.Coordinator.WhenIdle();

        Assert.Equal(4, rig.Signals.Decoded.Count);
        for (var i = 0; i < episodes.Count; i++)
        {
            var intro = (await rig.Store.GetSegmentsAsync(episodes[i].Fingerprint!)).Effective(SegmentKind.Intro);
            Assert.NotNull(intro);
            Assert.InRange(intro.Start.TotalSeconds, ColdOpens[i] - 1.2, ColdOpens[i] + 1.2);
            Assert.NotNull((await rig.Store.GetSegmentsAsync(episodes[i].Fingerprint!)).Effective(SegmentKind.Credits));
        }

        var records = await rig.Store.GetAnalysesAsync();
        Assert.Equal(4, records.Count);

        // The same library again: nothing to do.
        rig.Coordinator.Update(rig.Catalog(), background: true);
        await rig.Coordinator.WhenIdle();
        Assert.Equal(4, rig.Signals.Decoded.Count);
        Assert.Equal(records.Values.Select(r => r.AnalyzedUtc), (await rig.Store.GetAnalysesAsync()).Values.Select(r => r.AnalyzedUtc));
    }

    [Fact]
    public async Task A_new_episode_brings_the_others_up_to_date_without_decoding_them_again()
    {
        await using var rig = await Rig.CreateAsync();
        foreach (var n in new[] { 1, 2, 3 })
        {
            rig.AddEpisode(n);
        }

        rig.Coordinator.Update(rig.Catalog(), background: true);
        await rig.Coordinator.WhenIdle();
        var first = rig.Items[0];
        var before = (await rig.Store.GetSegmentsAsync(first.Fingerprint!)).Effective(SegmentKind.Intro)!;
        Assert.True(before.Confidence < SegmentPolicy.AutoSkipThreshold, "two other episodes only offer");

        rig.AddEpisode(4);
        rig.Coordinator.Update(rig.Catalog(), background: true);
        await rig.Coordinator.WhenIdle();

        // Only the new file was decoded; the others were compared again from what was kept.
        Assert.Equal(4, rig.Signals.Decoded.Distinct().Count());
        Assert.Equal(4, rig.Signals.Decoded.Count);
        var after = (await rig.Store.GetSegmentsAsync(first.Fingerprint!)).Effective(SegmentKind.Intro)!;
        Assert.True(after.Confidence >= SegmentPolicy.AutoSkipThreshold, $"three other episodes agree: {after.Confidence}");
    }

    [Fact]
    public async Task With_background_analysis_off_only_the_opened_episode_and_the_next_are_analysed()
    {
        await using var rig = await Rig.CreateAsync();
        var episodes = Enumerable.Range(1, 5).Select(n => rig.AddEpisode(n)).ToList();

        rig.Coordinator.Update(rig.Catalog(), background: false);
        await rig.Coordinator.WhenIdle();
        Assert.Empty(await rig.Store.GetAnalysesAsync());

        rig.Coordinator.Prioritize(episodes[1].Path);
        await rig.Coordinator.WhenIdle();

        var analysed = await rig.Store.GetAnalysesAsync();
        Assert.Equal([episodes[1].Fingerprint!, episodes[2].Fingerprint!], analysed.Keys.Order());
    }

    [Fact]
    public async Task An_unreadable_episode_is_recorded_and_the_rest_carry_on()
    {
        await using var rig = await Rig.CreateAsync();
        var episodes = Enumerable.Range(1, 4).Select(n => rig.AddEpisode(n)).ToList();
        rig.Signals.Unreadable.Add(episodes[2].Path);

        rig.Coordinator.Update(rig.Catalog(), background: true);
        await rig.Coordinator.WhenIdle();

        var records = await rig.Store.GetAnalysesAsync();
        Assert.Equal(AnalysisOutcome.Failed, records[episodes[2].Fingerprint!].Outcome);
        Assert.Equal(AnalysisOutcome.Done, records[episodes[0].Fingerprint!].Outcome);
        Assert.NotNull((await rig.Store.GetSegmentsAsync(episodes[0].Fingerprint!)).Effective(SegmentKind.Intro));

        // It is not retried endlessly.
        var attempts = rig.Signals.Decoded.Count(p => p == episodes[2].Path);
        rig.Coordinator.Update(rig.Catalog(), background: true);
        await rig.Coordinator.WhenIdle();
        Assert.Equal(attempts, rig.Signals.Decoded.Count(p => p == episodes[2].Path));
    }

    [Fact]
    public async Task A_correction_survives_analysis_running_again()
    {
        await using var rig = await Rig.CreateAsync();
        var episodes = Enumerable.Range(1, 3).Select(n => rig.AddEpisode(n)).ToList();
        rig.Coordinator.Update(rig.Catalog(), background: true);
        await rig.Coordinator.WhenIdle();
        await rig.Store.SaveCorrectionAsync(episodes[0].Fingerprint!, new SegmentCorrection(SegmentKind.Intro, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(31)));

        rig.AddEpisode(4);
        rig.Coordinator.Update(rig.Catalog(), background: true);
        await rig.Coordinator.WhenIdle();

        var intro = (await rig.Store.GetSegmentsAsync(episodes[0].Fingerprint!)).Effective(SegmentKind.Intro)!;
        Assert.Equal(SegmentSource.Manual, intro.Source);
        Assert.Equal(TimeSpan.FromSeconds(9), intro.Start);
    }

    [Fact]
    public void Each_release_is_compared_with_episodes_from_its_own_folder()
    {
        var items = new List<LibraryItem>();
        static LibraryItem Item(string folder, int episode, string fp) => new()
        {
            Path = $"/tv/{folder}/Show.S01E{episode:00}.mkv",
            Title = "Show",
            Category = MediaCategory.Episode,
            Season = 1,
            Episode = episode,
            Fingerprint = fp,
            Duration = TimeSpan.FromMinutes(42),
            SizeBytes = folder == "1080p" ? 2_000_000_000 : 700_000_000,
            Width = folder == "1080p" ? 1920 : 1280,
            Height = folder == "1080p" ? 1080 : 720,
            Probed = true,
        };
        for (var e = 1; e <= 3; e++)
        {
            items.Add(Item("1080p", e, $"a{e}"));
            items.Add(Item("720p", e, $"b{e}"));
        }

        var catalog = SeriesCatalog.Build(CopyGroup.Build(items));
        var jobs = EpisodeAnalysisPlan.Build(catalog, new Dictionary<string, AnalysisRecord>());

        Assert.Equal(6, jobs.Count);
        var small = jobs.Single(j => j.Fingerprint == "b2");
        Assert.Equal(["b1", "b3"], small.Peers.Select(p => p.Fingerprint).Order());
        Assert.Equal(["b1"], small.Previous.Select(p => p.Fingerprint));

        // Analysed already against the same episodes: not again.
        var done = jobs.ToDictionary(j => j.Fingerprint, j => new AnalysisRecord(j.Fingerprint, SegmentDetector.Version, j.Signature, DateTimeOffset.UnixEpoch, AnalysisOutcome.Done));
        Assert.Empty(EpisodeAnalysisPlan.Build(catalog, done));
        done["a1"] = done["a1"] with { DetectorVersion = SegmentDetector.Version - 1 };
        Assert.Equal(["a1"], EpisodeAnalysisPlan.Build(catalog, done).Select(j => j.Fingerprint));
    }

    [Fact]
    public void Short_clips_missing_files_and_unmeasured_files_are_not_analysed()
    {
        var episode = new LibraryItem { Path = "/tv/a.mkv", Title = "A", Category = MediaCategory.Episode, Fingerprint = "aa", Duration = TimeSpan.FromMinutes(30) };
        Assert.True(EpisodeAnalysisPlan.Analysable(episode));
        Assert.False(EpisodeAnalysisPlan.Analysable(episode with { Duration = TimeSpan.FromSeconds(40) }));
        Assert.False(EpisodeAnalysisPlan.Analysable(episode with { Duration = TimeSpan.FromHours(4) }));
        Assert.False(EpisodeAnalysisPlan.Analysable(episode with { Duration = null }));
        Assert.False(EpisodeAnalysisPlan.Analysable(episode with { Fingerprint = null }));
        Assert.False(EpisodeAnalysisPlan.Analysable(episode with { IsMissing = true }));
    }
}
