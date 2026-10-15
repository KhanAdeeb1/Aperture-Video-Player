using Aperture.Core.Bookmarks;
using Aperture.Core.Playback;

namespace Aperture.Core.Tests.Playback;

public class AdvancedPlaybackTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Loop_key_marks_start_then_end_then_clears()
    {
        var loop = LoopPoints.None.Next(S(10), out _);
        Assert.True(loop.IsMarking);
        Assert.Equal("Loop starts at 0:10", loop.Describe());
        loop = loop.Next(S(25), out var refused);
        Assert.False(refused);
        Assert.True(loop.IsLooping);
        Assert.Equal("Loop 0:10 – 0:25", loop.Describe());
        Assert.Equal(LoopPoints.None, loop.Next(S(30), out _));
    }

    [Fact]
    public void Loop_end_before_start_swaps_and_too_short_is_refused()
    {
        Assert.Equal(new LoopPoints(S(5), S(10)), new LoopPoints(S(10)).Next(S(5), out _));
        var same = new LoopPoints(S(10)).Next(S(10.2), out var refused);
        Assert.True(refused);
        Assert.Equal(new LoopPoints(S(10)), same);
    }

    [Fact]
    public void Loop_menu_setters_keep_what_still_makes_sense()
    {
        var loop = new LoopPoints(S(10), S(20));
        Assert.Equal(new LoopPoints(S(12), S(20)), loop.WithStart(S(12)));
        Assert.Equal(new LoopPoints(S(25)), loop.WithStart(S(25)));
        Assert.False(LoopPoints.None.TryWithEnd(S(5), out _));
        Assert.True(new LoopPoints(S(5)).TryWithEnd(S(9), out var ended));
        Assert.Equal(new LoopPoints(S(5), S(9)), ended);
        Assert.False(new LoopPoints(S(5)).TryWithEnd(S(4), out _));
    }

    [Fact]
    public void Sound_options_build_one_filter_graph()
    {
        Assert.Equal(string.Empty, SoundOptions.Off.FilterGraph());
        Assert.Equal("lavfi=[dynaudnorm=f=150:g=15:p=0.9:m=8]", new SoundOptions(EvenVolume: true).FilterGraph());
        var both = new SoundOptions(true, true).FilterGraph();
        Assert.StartsWith("lavfi=[aformat=channel_layouts=stereo,dialoguenhance", both, StringComparison.Ordinal);
        Assert.EndsWith("dynaudnorm=f=150:g=15:p=0.9:m=8]", both, StringComparison.Ordinal);
        Assert.Equal("Clearer dialogue and even volume", new SoundOptions(true, true).Describe());
    }

    [Theory]
    [InlineData(60, 2)]
    [InlineData(600, 2)]
    [InlineData(7200, 18)]
    public void Preview_step_grows_with_length(double seconds, double step) =>
        Assert.Equal(S(step), SeekPreview.Step(S(seconds)));

    [Fact]
    public void Preview_slots_round_to_the_grid_and_stay_inside_the_file()
    {
        Assert.Equal(S(10), SeekPreview.Slot(S(10.9), S(60)));
        Assert.Equal(S(12), SeekPreview.Slot(S(11.1), S(60)));
        Assert.Equal(S(59), SeekPreview.Slot(S(60), S(60)));
        Assert.Equal(TimeSpan.Zero, SeekPreview.Slot(S(-3), S(60)));
        Assert.Equal(TimeSpan.Zero, SeekPreview.Slot(S(3), TimeSpan.Zero));
    }

    [Fact]
    public void Preview_size_fits_the_box_and_keeps_the_shape()
    {
        Assert.Equal((240, 135), SeekPreview.Size(16.0 / 9, 240, 160));
        Assert.Equal((90, 160), SeekPreview.Size(9.0 / 16, 240, 160));
        Assert.Equal((240, 100), SeekPreview.Size(2.4, 240, 160));
        Assert.Equal((240, 135), SeekPreview.Size(double.NaN, 240, 160));
    }

    [Fact]
    public void Bookmark_notes_are_cleaned()
    {
        Assert.Equal(string.Empty, Bookmark.CleanNote("   "));
        Assert.Equal("a b", Bookmark.CleanNote(" a\r\n\tb "));
        Assert.Equal(Bookmark.MaximumNoteLength, Bookmark.CleanNote(new string('n', 900)).Length);
    }

    [Fact]
    public void Bookmarks_near_and_next()
    {
        var marks = new[] { Mark(1, 30), Mark(2, 90), Mark(3, 300) };
        Assert.Equal(2, Bookmark.Near(marks, S(90.6))?.Id);
        Assert.Null(Bookmark.Near(marks, S(92)));
        Assert.Equal(2, Bookmark.Adjacent(marks, S(30), forward: true)?.Id);
        Assert.Equal(1, Bookmark.Adjacent(marks, S(91), forward: false)?.Id);
        Assert.Equal(2, Bookmark.Adjacent(marks, S(95), forward: false)?.Id);
        Assert.Null(Bookmark.Adjacent(marks, S(300), forward: true));

        static Bookmark Mark(long id, double seconds) => new(id, "a.mkv", TimeSpan.FromSeconds(seconds), string.Empty, DateTimeOffset.UnixEpoch);
    }
}
