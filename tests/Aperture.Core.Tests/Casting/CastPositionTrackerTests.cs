using Aperture.Core.Casting;

namespace Aperture.Core.Tests.Casting;

public class CastPositionTrackerTests
{
    [Fact]
    public void Before_any_report_the_desktop_position_stands()
    {
        var tracker = new CastPositionTracker(300, 3600);

        Assert.Equal(300, tracker.Trusted);
        Assert.False(tracker.Observe(CastState.Buffering, 0, 0));
        Assert.False(tracker.Observe(CastState.Playing, null, 1));
        Assert.Equal(300, tracker.Trusted);
    }

    [Fact]
    public void Zero_while_loading_is_ignored_and_arriving_is_trusted()
    {
        var tracker = new CastPositionTracker(300, 3600);

        Assert.False(tracker.Observe(CastState.Playing, 0, 0));
        Assert.False(tracker.Observe(CastState.Playing, 0.5, 1));
        Assert.Equal(300, tracker.Trusted);

        Assert.True(tracker.Observe(CastState.Playing, 301, 2));
        Assert.Equal(301, tracker.Trusted);
        Assert.True(tracker.Observe(CastState.Paused, 305, 6));
        Assert.Equal(305, tracker.Trusted);
    }

    [Fact]
    public void Starting_from_the_beginning_is_trusted_at_once()
    {
        var tracker = new CastPositionTracker(0, 600);

        Assert.True(tracker.Observe(CastState.Playing, 1, 0));
        Assert.Equal(1, tracker.Trusted);
    }

    [Fact]
    public void A_stale_report_from_before_a_seek_is_not_trusted()
    {
        var tracker = new CastPositionTracker(600, 3600);
        tracker.Observe(CastState.Playing, 600, 0);
        tracker.Observe(CastState.Playing, 601, 1);

        tracker.Restart(100, 0);
        Assert.False(tracker.Observe(CastState.Playing, 602, 2));
        Assert.False(tracker.Observe(CastState.Playing, 603, 3));
        Assert.Equal(601, tracker.Trusted);
        Assert.Equal(603, tracker.Latest);

        Assert.True(tracker.Observe(CastState.Playing, 100.5, 4));
        Assert.Equal(100.5, tracker.Trusted);
    }

    [Fact]
    public void A_TV_that_stays_somewhere_later_is_believed_after_steady_playback()
    {
        var tracker = new CastPositionTracker(0, 3600);
        tracker.Restart(100, 0);
        var t = 0.0;
        for (var position = 600.0; position < 606; position++, t++)
        {
            tracker.Observe(CastState.Playing, position, t);
        }

        Assert.Equal(605, tracker.Latest);
        Assert.Null(tracker.Trusted); // not yet: five seconds of steady playback from the first move
        tracker.Observe(CastState.Playing, 606, t);
        Assert.Equal(606, tracker.Trusted);
    }

    [Fact]
    public void Playing_from_earlier_than_asked_needs_a_minute_before_it_replaces_the_place()
    {
        var tracker = new CastPositionTracker(300, 3600);
        var t = 0.0;
        for (var position = 0.0; position < CastPositionTracker.EarlierNeedsSeconds; position++, t++)
        {
            tracker.Observe(CastState.Playing, position, t);
        }

        Assert.Equal(300, tracker.Trusted);
        tracker.Observe(CastState.Playing, 62, 62);
        Assert.Equal(62, tracker.Trusted);
    }

    [Fact]
    public void A_jump_back_to_zero_on_its_own_is_held_back()
    {
        var tracker = new CastPositionTracker(1000, 3600);
        tracker.Observe(CastState.Playing, 1000, 0);
        tracker.Observe(CastState.Playing, 1001, 1);
        Assert.Equal(1001, tracker.Trusted);

        Assert.False(tracker.Observe(CastState.Playing, 0, 2));
        Assert.False(tracker.Observe(CastState.Playing, 1, 3));
        Assert.Equal(1001, tracker.Trusted);
    }

    [Fact]
    public void A_converted_stream_counts_from_its_own_start()
    {
        var tracker = new CastPositionTracker(0, 3600);
        tracker.Restart(1200, 1200);

        Assert.True(tracker.Observe(CastState.Playing, 2, 0));
        Assert.Equal(1202, tracker.Trusted);
    }

    [Fact]
    public void Nonsense_is_refused_and_positions_stay_within_the_file()
    {
        var tracker = new CastPositionTracker(590, 600);

        Assert.False(tracker.Observe(CastState.Playing, -4, 0));
        Assert.False(tracker.Observe(CastState.Playing, double.NaN, 0));
        Assert.False(tracker.Observe(CastState.Playing, 9000, 0));
        Assert.True(tracker.Observe(CastState.Playing, 603, 1));
        Assert.Equal(600, tracker.Trusted);
        Assert.True(tracker.NearEnd);
    }

    [Fact]
    public void The_TVs_length_is_used_only_when_the_desktop_had_none()
    {
        var unknown = new CastPositionTracker(0, 0);
        unknown.OfferDuration(1800);
        Assert.Equal(1800, unknown.DurationSeconds);

        var known = new CastPositionTracker(0, 600);
        known.OfferDuration(1800);
        Assert.Equal(600, known.DurationSeconds);
    }
}
