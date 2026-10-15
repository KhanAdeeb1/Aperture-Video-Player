using Aperture.Core.Series;
using Aperture.Media.Analysis;

namespace Aperture.Playback.Mpv.Tests;

/// <summary>
/// What intro and credits detection reads from real files, and detection end to end on three
/// generated episodes of one show (tools/test-media/make-engine-test-media.sh): each has its
/// own cold open (10, 3 and 15 s), the same 20 s opening theme, its own story, and the same
/// 20 s end theme over black from 70 s to the end at 90 s. Episode 2 has named chapters.
/// </summary>
[Collection("libmpv")]
public sealed class SignalReaderTests : IDisposable
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1.2);
    private readonly string _work = Path.Combine(Path.GetTempPath(), "aperture-signals-" + Guid.NewGuid().ToString("N"));
    private readonly MpvSignalReader _reader;

    public SignalReaderTests() => _reader = new MpvSignalReader(_work, static (_, _) => { });

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [MediaFact]
    public async Task Sound_is_decoded_whole_at_the_requested_rate_and_the_temporary_file_is_removed()
    {
        var audio = await _reader.ReadAudioAsync(TestMedia.PathOf("Harbour.S01E01.mkv"), TimeSpan.Zero, null, AudioPrint.SampleRate);

        Assert.NotNull(audio);
        Assert.Equal(AudioPrint.SampleRate, audio.SampleRate);
        Assert.InRange(audio.Length.TotalSeconds, 89.5, 90.5);
        Assert.Contains(audio.Samples, s => Math.Abs((int)s) > 1000);
        Assert.Empty(Directory.Exists(_work) ? Directory.GetFiles(_work) : []);
    }

    [MediaFact]
    public async Task Part_of_the_sound_can_be_read()
    {
        var audio = await _reader.ReadAudioAsync(TestMedia.PathOf("Harbour.S01E01.mkv"), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), AudioPrint.SampleRate);

        Assert.NotNull(audio);
        Assert.Equal(TimeSpan.FromSeconds(30), audio.Start);
        Assert.InRange(audio.Length.TotalSeconds, 9.7, 10.3);
    }

    [MediaFact]
    public async Task A_black_ending_is_found_where_it_is()
    {
        var dark = await _reader.FindDarkStretchesAsync(TestMedia.PathOf("Harbour.S01E01.mkv"), TimeSpan.FromSeconds(50));

        Assert.NotNull(dark);
        var stretch = Assert.Single(dark);
        Assert.InRange(stretch.Start.TotalSeconds, 69.5, 70.5);
        Assert.InRange(stretch.End.TotalSeconds, 89.5, 90.5);
    }

    [MediaFact]
    public async Task A_file_without_a_picture_has_no_dark_stretches_to_report()
    {
        Assert.Null(await _reader.FindDarkStretchesAsync(TestMedia.PathOf("audio-only.mp4"), TimeSpan.Zero));
    }

    [MediaFact]
    public async Task Chapters_are_read_with_their_names()
    {
        var chapters = await _reader.ReadChaptersAsync(TestMedia.PathOf("Harbour.S01E02.mkv"));

        Assert.Equal(["Cold open", "Opening", "Part A", "Ending"], chapters.Select(c => c.Title));
        Assert.InRange(chapters[1].Start.TotalSeconds, 2.9, 3.1);
        Assert.Empty(await _reader.ReadChaptersAsync(TestMedia.PathOf("Harbour.S01E01.mkv")));
    }

    [MediaFact]
    public async Task An_unreadable_file_is_reported_not_treated_as_silent()
    {
        await Assert.ThrowsAsync<IOException>(() => _reader.ReadAudioAsync(TestMedia.PathOf("damaged.mp4"), TimeSpan.Zero, null, AudioPrint.SampleRate));
    }

    [MediaFact]
    public async Task Intros_and_credits_are_found_in_real_episodes()
    {
        string[] names = ["Harbour.S01E01.mkv", "Harbour.S01E02.mkv", "Harbour.S01E03.mkv"];
        double[] coldOpens = [10, 3, 15];
        var signals = new List<EpisodeSignals>();
        foreach (var name in names)
        {
            signals.Add(await MeasureAsync(name));
        }

        for (var i = 0; i < names.Length; i++)
        {
            var others = signals.Where((_, j) => j != i).ToList();
            var segments = SegmentDetector.Detect(signals[i], others, i > 0 ? [signals[i - 1]] : [], default);

            var intro = segments.SingleOrDefault(s => s.Kind == SegmentKind.Intro);
            Assert.NotNull(intro);
            Near(TimeSpan.FromSeconds(coldOpens[i]), intro.Start, $"{names[i]} intro start");
            Near(TimeSpan.FromSeconds(coldOpens[i] + 20), intro.End, $"{names[i]} intro end");
            if (i == 1)
            {
                // Episode 2's named chapters are used, and agree with what was measured.
                Assert.Equal(SegmentSource.Chapter, intro.Source);
                Assert.True(intro.Confidence >= 0.95, $"{names[i]} intro confidence {intro.Confidence}");
            }
            else
            {
                // Two other episodes agree: offered, but automatic skipping waits for three.
                Assert.InRange(intro.Confidence, SegmentPolicy.OfferThreshold, SegmentPolicy.AutoSkipThreshold - 0.01);
            }


            var credits = segments.SingleOrDefault(s => s.Kind == SegmentKind.Credits);
            Assert.NotNull(credits);
            Near(TimeSpan.FromSeconds(70), credits.Start, $"{names[i]} credits start");
            Assert.True(credits.Confidence >= SegmentPolicy.AutoSkipThreshold, $"{names[i]} credits confidence {credits.Confidence}");
            Assert.True(EpisodeCueTracker.RunsToEnd(credits, signals[i].Duration));

            // No recap: nothing in these episodes repeats the one before.
            Assert.DoesNotContain(segments, s => s.Kind == SegmentKind.Recap);
            TestMedia.Record($"{names[i]} (intro and credits)", "Detected", $"intro {intro.Start.TotalSeconds:0.0}–{intro.End.TotalSeconds:0.0} s ({intro.Confidence:0.00}, {intro.Source}), credits from {credits.Start.TotalSeconds:0.0} s ({credits.Confidence:0.00})");
        }
    }

    private async Task<EpisodeSignals> MeasureAsync(string name)
    {
        var path = TestMedia.PathOf(name);
        var audio = await _reader.ReadAudioAsync(path, TimeSpan.Zero, null, AudioPrint.SampleRate);
        Assert.NotNull(audio);
        var duration = audio.Length;
        var darkFrom = SegmentDetector.TailStart(duration);
        var dark = await _reader.FindDarkStretchesAsync(path, darkFrom);
        var chapters = await _reader.ReadChaptersAsync(path);
        return new EpisodeSignals(name, duration, AudioPrint.Compute(audio.Samples, audio.Start), darkFrom, dark, chapters);
    }

    private static void Near(TimeSpan expected, TimeSpan actual, string what) =>
        Assert.True((expected - actual).Duration() <= Tolerance, $"{what}: expected {expected.TotalSeconds:0.0} s, found {actual.TotalSeconds:0.0} s");
}
