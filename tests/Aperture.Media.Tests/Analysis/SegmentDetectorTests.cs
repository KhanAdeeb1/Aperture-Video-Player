using Aperture.Core.Series;
using Aperture.Media.Analysis;
using static Aperture.Media.Tests.Analysis.SyntheticAudio;

namespace Aperture.Media.Tests.Analysis;

/// <summary>
/// Detection on representative episodes built from parts: each has its own cold open length,
/// the show's intro theme, its own story, and the show's credits music, and each is delivered
/// as a different encode. Expected times come from how the episodes were assembled.
/// </summary>
public class SegmentDetectorTests
{
    private const int IntroTheme = 100;
    private const int CreditsTheme = 200;
    private const double IntroSeconds = 40;
    private const double CreditsSeconds = 45;
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1.2);

    /// <summary>An assembled episode and where its parts really are.</summary>
    private sealed record Built(EpisodeSignals Signals, TimeSpan IntroStart, TimeSpan IntroEnd, TimeSpan CreditsStart, TimeSpan CreditsEnd);

    private static Built Episode(int number, double coldOpen, double story = 240, double postCredits = 0, bool intro = true, short[]? opening = null)
    {
        var parts = new List<short[]>();
        if (opening is not null)
        {
            parts.Add(opening);
        }

        parts.Add(Speech(number * 10 + 1, coldOpen));
        var introStart = TimeSpan.FromSeconds(parts.Sum(p => p.Length) / (double)Rate);
        parts.Add(intro ? Music(IntroTheme, IntroSeconds) : Speech(number * 10 + 5, IntroSeconds));
        parts.Add(Speech(number * 10 + 2, story));
        var creditsStart = TimeSpan.FromSeconds(parts.Sum(p => p.Length) / (double)Rate);
        parts.Add(Music(CreditsTheme, CreditsSeconds));
        var creditsEnd = TimeSpan.FromSeconds(parts.Sum(p => p.Length) / (double)Rate);
        if (postCredits > 0)
        {
            parts.Add(Speech(number * 10 + 3, postCredits));
        }

        var released = Release(Concat([.. parts]), seed: number);
        var shift = TimeSpan.FromSeconds((released.Length - parts.Sum(p => p.Length)) / (double)Rate);
        var print = AudioPrint.Compute(released, TimeSpan.Zero);
        var signals = new EpisodeSignals($"ep{number}", Length(released), print, TimeSpan.Zero, null, []);
        return new Built(signals, introStart + shift, introStart + shift + TimeSpan.FromSeconds(IntroSeconds), creditsStart + shift, creditsEnd + shift);
    }

    private static void Near(TimeSpan expected, TimeSpan actual, string what) =>
        Assert.True((expected - actual).Duration() <= Tolerance, $"{what}: expected {expected.TotalSeconds:0.0} s, found {actual.TotalSeconds:0.0} s");

    [Fact]
    public void Intros_are_found_wherever_each_episode_puts_them()
    {
        Built[] season = [Episode(1, 30), Episode(2, 95), Episode(3, 0.5), Episode(4, 140), Episode(5, 62)];
        foreach (var episode in season)
        {
            var peers = season.Where(e => e != episode).Select(e => e.Signals).ToList();
            var intro = SegmentDetector.FindIntro(episode.Signals, peers, default);

            Assert.NotNull(intro);
            Near(episode.IntroStart, intro.Start, $"{episode.Signals.Id} intro start");
            Near(episode.IntroEnd, intro.End, $"{episode.Signals.Id} intro end");
            Assert.True(intro.Confidence >= SegmentPolicy.AutoSkipThreshold, $"{episode.Signals.Id} confidence {intro.Confidence}");
            Assert.Equal(SegmentSource.Detected, intro.Source);
        }
    }

    [Fact]
    public void A_special_without_an_intro_gets_none_even_where_other_episodes_have_one()
    {
        // The special's story sits exactly where episode 1's intro is: same timestamps, different content.
        var special = Episode(9, 30, intro: false);
        Built[] others = [Episode(1, 30), Episode(2, 95), Episode(3, 0.5), Episode(4, 140)];
        Near(others[0].IntroStart, special.IntroStart, "same timestamp");

        var intro = SegmentDetector.FindIntro(special.Signals, others.Select(o => o.Signals).ToList(), default);

        Assert.Null(intro);
    }

    [Fact]
    public void With_only_one_other_episode_the_intro_is_offered_but_never_skipped_automatically()
    {
        var a = Episode(1, 30);
        var b = Episode(2, 95);

        var intro = SegmentDetector.FindIntro(a.Signals, [b.Signals], default);

        Assert.NotNull(intro);
        Assert.Equal(SkipAction.Offer, SegmentPolicy.Decide(intro, SkipMode.Automatic));
    }

    [Fact]
    public void Credits_are_found_and_a_scene_after_them_is_kept()
    {
        Built[] season = [Episode(1, 30), Episode(2, 95, postCredits: 50), Episode(3, 10), Episode(4, 60)];
        foreach (var episode in season)
        {
            var peers = season.Where(e => e != episode).Select(e => e.Signals).ToList();
            var credits = SegmentDetector.FindCredits(episode.Signals, peers, default);

            Assert.NotNull(credits);
            Near(episode.CreditsStart, credits.Start, $"{episode.Signals.Id} credits start");
            Near(episode.CreditsEnd, credits.End, $"{episode.Signals.Id} credits end");
        }

        var withScene = SegmentDetector.FindCredits(season[1].Signals, season.Where(e => e != season[1]).Select(e => e.Signals).ToList(), default)!;
        Assert.False(EpisodeCueTracker.RunsToEnd(withScene, season[1].Signals.Duration));
        var plain = SegmentDetector.FindCredits(season[0].Signals, season.Skip(1).Select(e => e.Signals).ToList(), default)!;
        Assert.True(EpisodeCueTracker.RunsToEnd(plain, season[0].Signals.Duration));
    }

    [Fact]
    public void Dark_picture_and_ending_music_together_give_the_highest_confidence()
    {
        var a = Episode(1, 30);
        var withDark = a.Signals with
        {
            DarkFrom = SegmentDetector.TailStart(a.Signals.Duration),
            Dark = [new TimeRange(a.CreditsStart + TimeSpan.FromSeconds(0.4), a.Signals.Duration)],
        };

        var credits = SegmentDetector.FindCredits(withDark, [Episode(2, 95).Signals, Episode(3, 10).Signals], default);

        Assert.NotNull(credits);
        Assert.True(credits.Confidence >= 0.95);
        Near(a.CreditsStart, credits.Start, "credits start");
    }

    [Fact]
    public void A_long_dark_ending_alone_is_offered_as_credits_without_autoplay_confidence()
    {
        var a = Episode(1, 30);
        var duration = a.Signals.Duration;
        var signals = a.Signals with
        {
            Audio = null,
            DarkFrom = SegmentDetector.TailStart(duration),
            Dark = [new TimeRange(duration - TimeSpan.FromSeconds(50), duration - TimeSpan.FromSeconds(31)), new TimeRange(duration - TimeSpan.FromSeconds(29.5), duration)],
        };

        var credits = SegmentDetector.FindCredits(signals, [], default);

        Assert.NotNull(credits);
        Near(duration - TimeSpan.FromSeconds(50), credits.Start, "dark start");
        Assert.InRange(credits.Confidence, SegmentPolicy.OfferThreshold, SegmentPolicy.AutoSkipThreshold - 0.01);
    }

    [Fact]
    public void A_dark_scene_with_more_story_after_it_is_not_credits()
    {
        var a = Episode(1, 30);
        var duration = a.Signals.Duration;
        var signals = a.Signals with
        {
            Audio = null,
            DarkFrom = SegmentDetector.TailStart(duration),
            Dark = [new TimeRange(duration - TimeSpan.FromSeconds(150), duration - TimeSpan.FromSeconds(120))],
        };

        Assert.Null(SegmentDetector.FindCredits(signals, [], default));
    }

    [Fact]
    public void A_recap_of_earlier_scenes_is_found_at_the_start()
    {
        var previous = Episode(2, 95);
        var previousSound = Concat(Speech(21, 95), Music(IntroTheme, IntroSeconds), Speech(22, 240));

        // "Previously on…" voice, then four excerpts from the previous episode's story, then the cold open.
        int Seconds(double s) => (int)(s * Rate);
        short[] Excerpt(double at, double length) => previousSound.Skip(Seconds(at)).Take(Seconds(length)).ToArray();
        var recap = Concat(Speech(77, 2.5), Excerpt(160, 6), Excerpt(230, 5), Excerpt(300, 7), Excerpt(180, 4));
        var episode = Episode(3, 40, opening: recap);
        var others = new[] { previous, Episode(1, 30), Episode(4, 10) };

        var segments = SegmentDetector.Detect(episode.Signals, others.Select(o => o.Signals).ToList(), [previous.Signals, others[1].Signals], default);

        var found = segments.SingleOrDefault(s => s.Kind == SegmentKind.Recap);
        Assert.NotNull(found);
        Assert.Equal(TimeSpan.Zero, found.Start);
        Near(Length(recap), found.End, "recap end");
        Assert.True(found.Confidence >= SegmentPolicy.OfferThreshold);

        // The intro after it is still found, and the recap does not run into it.
        var intro = segments.Single(s => s.Kind == SegmentKind.Intro);
        Assert.True(found.End <= intro.Start);
    }

    [Fact]
    public void An_episode_without_a_recap_gets_none()
    {
        var previous = Episode(2, 95);
        var episode = Episode(3, 40);

        Assert.Null(SegmentDetector.FindRecap(episode.Signals, null, [previous.Signals], default));
    }

    [Fact]
    public void Named_chapters_are_used_and_confirmed_by_measurement()
    {
        var a = Episode(1, 30);
        var chaptered = a.Signals with
        {
            Chapters =
            [
                new ChapterMark("Prologue", TimeSpan.Zero),
                new ChapterMark("Opening", a.IntroStart),
                new ChapterMark("Part A", a.IntroEnd),
                new ChapterMark("Ending", a.CreditsStart),
            ],
        };

        var segments = SegmentDetector.Detect(chaptered, [Episode(2, 95).Signals, Episode(3, 10).Signals], [], default);

        var intro = segments.Single(s => s.Kind == SegmentKind.Intro);
        Assert.Equal(SegmentSource.Chapter, intro.Source);
        Assert.Equal(a.IntroStart, intro.Start);
        Assert.Equal(a.IntroEnd, intro.End);
        Assert.True(intro.Confidence >= 0.95);
        var credits = segments.Single(s => s.Kind == SegmentKind.Credits);
        Assert.Equal(a.CreditsStart, credits.Start);
        Assert.Equal(chaptered.Duration, credits.End);
    }

    [Fact]
    public void A_duplicate_copy_is_not_evidence_for_an_intro()
    {
        var a = Episode(1, 30);
        var sameEpisodeOtherRelease = a.Signals with
        {
            Id = "ep1-copy",
            Audio = AudioPrint.Compute(Release(Concat(Speech(11, 30), Music(IntroTheme, IntroSeconds), Speech(12, 240), Music(CreditsTheme, CreditsSeconds)), seed: 99), TimeSpan.Zero),
        };

        Assert.Null(SegmentDetector.FindIntro(a.Signals, [sameEpisodeOtherRelease], default));
    }

    [Fact]
    public void Silence_never_matches_silence()
    {
        var a = AudioPrint.Compute(Concat(Silence(30), Speech(1, 30)), TimeSpan.Zero);
        var b = AudioPrint.Compute(Concat(Silence(30), Speech(2, 30)), TimeSpan.Zero);

        Assert.Empty(PrintMatcher.FindMatches(a, 0, a.Count, b, 0, b.Count, MatchOptions.Sequence(TimeSpan.FromSeconds(5))));
    }
}
