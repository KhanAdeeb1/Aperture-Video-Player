using Aperture.Core.Playback;
using Aperture.Core.Series;

namespace Aperture.Core.Tests.Series;

public class ShowChoicesTests
{
    private static readonly MediaTrack[] Tracks =
    [
        new(1, TrackKind.Video),
        new(1, TrackKind.Audio, Language: "eng", IsDefault: true),
        new(2, TrackKind.Audio, Language: "jpn", IsSelected: true),
        new(1, TrackKind.Subtitle, Language: "eng", IsForced: true),
        new(2, TrackKind.Subtitle, Language: "eng", IsSelected: true),
        new(3, TrackKind.Subtitle, Language: "fre"),
    ];

    [Fact]
    public void Capture_keeps_real_choices_only()
    {
        var choices = ShowChoices.Capture(Tracks, 1.25, new SoundOptions(ClearDialogue: true), SoundOptions.Off);
        Assert.Equal(new ShowChoices("jpn", "eng", false, 1.25, new SoundOptions(ClearDialogue: true)), choices);

        // One audio track is no choice; normal speed and the Settings sound are not remembered.
        MediaTrack[] single = [new(1, TrackKind.Audio, Language: "eng", IsSelected: true), new(1, TrackKind.Subtitle, Language: "eng")];
        Assert.Equal(new ShowChoices(SubtitlesOff: true), ShowChoices.Capture(single, 1.0, SoundOptions.Off, SoundOptions.Off));
    }

    [Fact]
    public void Picks_tracks_by_language_preferring_full_subtitles()
    {
        MediaTrack[] next =
        [
            new(1, TrackKind.Audio, Language: "en", IsDefault: true),
            new(2, TrackKind.Audio, Language: "ja"),
            new(1, TrackKind.Subtitle, Language: "en", IsForced: true),
            new(2, TrackKind.Subtitle, Language: "English"),
        ];
        Assert.Equal((2L, 2L, false), new ShowChoices("jpn", "eng").PickTracks(next));
        Assert.Equal(((long?)null, (long?)null, true), new ShowChoices(SubtitlesOff: true).PickTracks(next));
        Assert.Equal(((long?)null, (long?)null, false), new ShowChoices("kor", "kor").PickTracks(next));
    }

    [Theory]
    [InlineData("eng", "en", true)]
    [InlineData("fre", "fra", true)]
    [InlineData("eng", "jpn", false)]
    [InlineData(null, "eng", false)]
    public void Language_tags_compare_by_language(string? a, string b, bool same) =>
        Assert.Equal(same, ShowChoices.SameLanguage(a, b));
}
