using Aperture.Core.Series;

namespace Aperture.Core.Tests.Series;

public class EpisodeCueTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(42);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static EpisodeRules Rules(bool autoplay = true, SkipMode intro = SkipMode.Button, SkipMode recap = SkipMode.Button, bool credits = true) =>
        new(autoplay, intro, recap, credits, S(10));

    private static MediaSegment Intro(double confidence = 0.9) => new(SegmentKind.Intro, S(62), S(112), confidence, SegmentSource.Detected);

    private static MediaSegment Credits(double confidence = 0.95, double endBeforeFileEnd = 0) =>
        new(SegmentKind.Credits, Duration - S(60 + endBeforeFileEnd), Duration - S(endBeforeFileEnd), confidence, SegmentSource.Detected);

    private static EpisodeSegments With(params MediaSegment[] detected) => new(detected, []);

    [Fact]
    public void Skip_is_offered_only_inside_the_intro()
    {
        var tracker = new EpisodeCueTracker(With(Intro()), Rules(), hasNext: true);

        Assert.Null(tracker.Evaluate(S(61), Duration, false).Offer);
        Assert.Equal(SegmentKind.Intro, tracker.Evaluate(S(62), Duration, false).Offer!.Kind);
        Assert.Equal(SegmentKind.Intro, tracker.Evaluate(S(100), Duration, false).Offer!.Kind);
        Assert.Null(tracker.Evaluate(S(111.5), Duration, false).Offer);
    }

    [Fact]
    public void An_uncertain_intro_is_never_skipped_automatically_and_a_doubtful_one_not_even_offered()
    {
        var unsure = new EpisodeCueTracker(With(Intro(0.7)), Rules(intro: SkipMode.Automatic), hasNext: true);
        var state = unsure.Evaluate(S(62.2), Duration, false);
        Assert.Null(state.SkipNow);
        Assert.NotNull(state.Offer);

        var doubtful = new EpisodeCueTracker(With(Intro(0.5)), Rules(intro: SkipMode.Automatic), hasNext: true);
        Assert.Equal(CueState.Nothing, doubtful.Evaluate(S(62.2), Duration, false));
    }

    [Fact]
    public void A_sure_intro_is_skipped_once_on_entry_and_watching_it_again_is_respected()
    {
        var tracker = new EpisodeCueTracker(With(Intro()), Rules(intro: SkipMode.Automatic), hasNext: true);

        Assert.Equal(SegmentKind.Intro, tracker.Evaluate(S(62.3), Duration, false).SkipNow!.Kind);

        // The user went back to watch it: offered, not skipped again.
        var again = tracker.Evaluate(S(63), Duration, false);
        Assert.Null(again.SkipNow);
        Assert.NotNull(again.Offer);
    }

    [Fact]
    public void Seeking_into_the_middle_of_an_intro_does_not_skip_it()
    {
        var tracker = new EpisodeCueTracker(With(Intro()), Rules(intro: SkipMode.Automatic), hasNext: true);
        var state = tracker.Evaluate(S(90), Duration, false);
        Assert.Null(state.SkipNow);
        Assert.NotNull(state.Offer);
    }

    [Fact]
    public void Nothing_is_offered_when_the_user_turned_it_off()
    {
        var tracker = new EpisodeCueTracker(With(Intro()), Rules(intro: SkipMode.Never), hasNext: true);
        Assert.Equal(CueState.Nothing, tracker.Evaluate(S(70), Duration, false));
    }

    [Fact]
    public void A_correction_replaces_detection_and_none_means_none()
    {
        var corrected = new EpisodeSegments([Intro()], [new SegmentCorrection(SegmentKind.Intro, S(30), S(80))]);
        var tracker = new EpisodeCueTracker(corrected, Rules(), hasNext: true);
        Assert.NotNull(tracker.Evaluate(S(40), Duration, false).Offer);
        Assert.Null(tracker.Evaluate(S(100), Duration, false).Offer);

        var none = new EpisodeSegments([Intro()], [new SegmentCorrection(SegmentKind.Intro, null, null)]);
        Assert.Null(new EpisodeCueTracker(none, Rules(intro: SkipMode.Automatic), hasNext: true).Evaluate(S(62.2), Duration, false).Offer);
    }

    [Fact]
    public void Credits_to_the_end_bring_up_the_next_episode_instead_of_a_skip_button()
    {
        var tracker = new EpisodeCueTracker(With(Credits()), Rules(), hasNext: true);

        var before = tracker.Evaluate(Duration - S(61), Duration, false);
        Assert.Equal(NextUpMoment.None, before.NextUp);

        var during = tracker.Evaluate(Duration - S(50), Duration, false);
        Assert.Equal(NextUpMoment.Credits, during.NextUp);
        Assert.Null(during.Offer);
    }

    [Fact]
    public void A_scene_after_the_credits_is_kept_the_credits_are_only_skippable_and_the_card_waits_for_the_end()
    {
        var tracker = new EpisodeCueTracker(With(Credits(endBeforeFileEnd: 90)), Rules(), hasNext: true);

        var during = tracker.Evaluate(Duration - S(120), Duration, false);
        Assert.Equal(SegmentKind.Credits, during.Offer!.Kind);
        Assert.Equal(NextUpMoment.None, during.NextUp);
        Assert.Null(during.SkipNow);

        var scene = tracker.Evaluate(Duration - S(60), Duration, false);
        Assert.Equal(NextUpMoment.None, scene.NextUp);
        Assert.Equal(NextUpMoment.Ended, tracker.Evaluate(Duration, Duration, true).NextUp);
    }

    [Fact]
    public void The_last_episode_has_no_next_up_and_offers_skip_credits_if_wanted()
    {
        var tracker = new EpisodeCueTracker(With(Credits()), Rules(), hasNext: false);
        var state = tracker.Evaluate(Duration - S(50), Duration, false);
        Assert.Equal(NextUpMoment.None, state.NextUp);
        Assert.Equal(SegmentKind.Credits, state.Offer!.Kind);
        Assert.Equal(NextUpMoment.None, tracker.Evaluate(Duration, Duration, true).NextUp);
    }

    [Fact]
    public void Autoplay_never_counts_down_when_it_is_off()
    {
        var tracker = new EpisodeCueTracker(With(Credits()), Rules(autoplay: false), hasNext: true);
        Assert.False(tracker.ShouldCountDown(NextUpMoment.Credits, cancelled: false, consecutiveAutoplays: 0));
        Assert.False(tracker.ShouldCountDown(NextUpMoment.Ended, cancelled: false, consecutiveAutoplays: 0));

        // Turned on while watching: from then on it does.
        tracker.SetRules(Rules(autoplay: true));
        Assert.True(tracker.ShouldCountDown(NextUpMoment.Ended, cancelled: false, consecutiveAutoplays: 0));
    }

    [Fact]
    public void Cancelling_or_a_long_unattended_run_stops_the_countdown()
    {
        var tracker = new EpisodeCueTracker(With(Credits()), Rules(), hasNext: true);
        Assert.True(tracker.ShouldCountDown(NextUpMoment.Credits, cancelled: false, consecutiveAutoplays: 0));
        Assert.False(tracker.ShouldCountDown(NextUpMoment.Credits, cancelled: true, consecutiveAutoplays: 0));
        Assert.False(tracker.ShouldCountDown(NextUpMoment.Ended, cancelled: false, consecutiveAutoplays: Autoplay.ConsecutiveLimit));
        Assert.False(tracker.ShouldCountDown(NextUpMoment.None, cancelled: false, consecutiveAutoplays: 0));
    }

    [Fact]
    public void Credits_that_are_only_fairly_sure_wait_for_the_real_end_before_counting_down()
    {
        var tracker = new EpisodeCueTracker(With(Credits(confidence: 0.7)), Rules(), hasNext: true);
        Assert.Equal(NextUpMoment.Credits, tracker.Evaluate(Duration - S(50), Duration, false).NextUp);
        Assert.False(tracker.ShouldCountDown(NextUpMoment.Credits, cancelled: false, consecutiveAutoplays: 0));
        Assert.True(tracker.ShouldCountDown(NextUpMoment.Ended, cancelled: false, consecutiveAutoplays: 0));
    }

    [Fact]
    public void Per_show_preferences_override_settings_and_credits_are_never_skipped_unasked()
    {
        var settings = Aperture.Core.Settings.AppSettings.Default with { AutoplayNextEpisode = true, IntroSkip = SkipMode.Button };
        var rules = new SeriesPreferences(Autoplay: false, Intro: SkipMode.Automatic).Resolve(settings);
        Assert.False(rules.Autoplay);
        Assert.Equal(SkipMode.Automatic, rules.ModeFor(SegmentKind.Intro));
        Assert.Equal(SkipMode.Button, rules.ModeFor(SegmentKind.Recap));
        Assert.Equal(SkipMode.Button, rules.ModeFor(SegmentKind.Credits));
        Assert.Equal(SkipMode.Never, (rules with { OfferSkipCredits = false }).ModeFor(SegmentKind.Credits));
        Assert.True(SeriesPreferences.Default.IsDefault);
        Assert.Equal(TimeSpan.FromSeconds(10), SeriesPreferences.Default.Resolve(settings).Countdown);
    }

    [Fact]
    public void Corrections_must_be_inside_the_file_and_long_enough()
    {
        Assert.Null(SegmentCorrection.Validate(S(10), S(40), Duration));
        Assert.NotNull(SegmentCorrection.Validate(S(40), S(10), Duration));
        Assert.NotNull(SegmentCorrection.Validate(S(10), S(10.5), Duration));
        Assert.NotNull(SegmentCorrection.Validate(S(10), Duration + S(5), Duration));
    }
}

public class NextEpisodeCountdownTests
{
    [Fact]
    public void Counts_down_against_the_clock_and_can_be_cancelled()
    {
        var clock = new ManualClock();
        var countdown = new NextEpisodeCountdown(clock);
        Assert.False(countdown.IsRunning);

        countdown.Start(TimeSpan.FromSeconds(10));
        Assert.Equal(10, countdown.SecondsLeft);
        clock.Advance(TimeSpan.FromSeconds(2.5));
        Assert.Equal(8, countdown.SecondsLeft);
        Assert.Equal(0.25, countdown.Fraction, 3);
        Assert.False(countdown.IsDue);

        countdown.Cancel();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.False(countdown.IsDue);
        Assert.Equal(0, countdown.SecondsLeft);
    }

    [Fact]
    public void Becomes_due_when_time_runs_out()
    {
        var clock = new ManualClock();
        var countdown = new NextEpisodeCountdown(clock);
        countdown.Start(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(countdown.IsDue);
        Assert.Equal(1.0, countdown.Fraction, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => countdown.Start(TimeSpan.Zero));
    }
}
