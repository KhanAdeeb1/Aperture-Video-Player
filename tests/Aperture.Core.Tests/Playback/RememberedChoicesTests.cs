using Aperture.Core.Playback;

namespace Aperture.Core.Tests.Playback;

public class RememberedChoicesTests
{
    private static readonly MediaTrack[] Tracks =
    [
        new(1, TrackKind.Video),
        new(1, TrackKind.Audio, Language: "eng"),
        new(2, TrackKind.Audio, Language: "fra", IsSelected: true),
        new(1, TrackKind.Subtitle, Language: "eng"),
        new(2, TrackKind.Subtitle, Language: "fra", IsSelected: true),
    ];

    [Fact]
    public void Captures_the_chosen_tracks_offsets_shape_and_speed()
    {
        var choices = RememberedChoices.Capture(Tracks, TimeSpan.FromMilliseconds(320), TimeSpan.FromMilliseconds(-100), AspectMode.Fill, 1.5);

        Assert.Equal(new PlaybackPreferences(2, 2, false, TimeSpan.FromMilliseconds(320), TimeSpan.FromMilliseconds(-100), AspectMode.Fill, 1.5), choices);
    }

    [Fact]
    public void Defaults_are_stored_as_nothing_chosen()
    {
        MediaTrack[] plain = [new(1, TrackKind.Video), new(1, TrackKind.Audio, IsSelected: true)];

        Assert.True(RememberedChoices.Capture(plain, TimeSpan.Zero, TimeSpan.Zero, AspectMode.Original, 1.0).IsEmpty);
    }

    [Fact]
    public void Subtitles_turned_off_are_remembered_but_external_files_are_not_remembered_by_number()
    {
        MediaTrack[] off = [new(1, TrackKind.Audio, IsSelected: true), new(1, TrackKind.Subtitle)];
        MediaTrack[] external = [new(1, TrackKind.Audio, IsSelected: true), new(3, TrackKind.Subtitle, IsSelected: true, IsExternal: true)];

        Assert.True(RememberedChoices.Capture(off, TimeSpan.Zero, TimeSpan.Zero, AspectMode.Original, 1).SubtitlesOff);
        var choices = RememberedChoices.Capture(external, TimeSpan.Zero, TimeSpan.Zero, AspectMode.Original, 1);
        Assert.Null(choices.SubtitleTrackId);
        Assert.False(choices.SubtitlesOff);
    }

    [Fact]
    public void Restoring_ignores_tracks_that_no_longer_exist()
    {
        var saved = new PlaybackPreferences(AudioTrackId: 2, SubtitleTrackId: 9, SubtitlesOff: false);

        Assert.Equal((2L, (long?)null, false), RememberedChoices.Tracks(saved, Tracks));
        Assert.Equal(((long?)null, (long?)null, true), RememberedChoices.Tracks(new PlaybackPreferences(SubtitlesOff: true), Tracks));
    }
}
