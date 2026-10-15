using Aperture.Core.Playback;

namespace Aperture.Core.Series;

/// <summary>
/// Playback choices carried from one episode of a show to the next: the audio and subtitle
/// language, speed and sound options the user last picked while watching it. A file's own
/// remembered choices win over these, and these win over Settings. Null means "not chosen".
/// </summary>
/// <param name="AudioLanguage">Language tag of the audio track last chosen, when the episode had several.</param>
/// <param name="SubtitleLanguage">Language tag of the subtitles last shown.</param>
/// <param name="SubtitlesOff">True when the user turned subtitles off.</param>
/// <param name="Speed">Playback speed, when not normal.</param>
/// <param name="Sound">Sound options, when they differ from Settings.</param>
public sealed record ShowChoices(
    string? AudioLanguage = null,
    string? SubtitleLanguage = null,
    bool SubtitlesOff = false,
    double? Speed = null,
    SoundOptions? Sound = null)
{
    /// <summary>Nothing carried over.</summary>
    public static ShowChoices None { get; } = new();

    /// <summary>
    /// What to carry over from the episode being left. A language is only remembered when the
    /// user had a real choice (more than one audio track; any subtitles at all) and the track
    /// says its language. Speed and sound are kept only when they differ from the defaults.
    /// </summary>
    public static ShowChoices Capture(IReadOnlyList<MediaTrack> tracks, double speed, SoundOptions sound, SoundOptions settingsSound)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(settingsSound);
        var audio = tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        var subtitles = tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
        var shown = subtitles.FirstOrDefault(t => t.IsSelected);
        return new ShowChoices(
            AudioLanguage: audio.Count > 1 ? Known(audio.FirstOrDefault(t => t.IsSelected)?.Language) : null,
            SubtitleLanguage: shown is { IsForced: false } ? Known(shown.Language) : null,
            SubtitlesOff: subtitles.Count > 0 && shown is null,
            Speed: Math.Abs(speed - PlaybackSpeed.Normal) < 0.001 ? null : speed,
            Sound: sound == settingsSound ? null : sound);
    }

    /// <summary>
    /// The tracks to pick in a new episode: the first audio track in the remembered language
    /// (the file's default among them when there are several), and the first full (not forced)
    /// subtitle track in the remembered language, or subtitles off. Null leaves the engine's choice.
    /// </summary>
    public (long? AudioTrackId, long? SubtitleTrackId, bool SubtitlesOff) PickTracks(IReadOnlyList<MediaTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        long? audio = null;
        if (AudioLanguage is { } audioLanguage)
        {
            var matches = tracks.Where(t => t.Kind == TrackKind.Audio && SameLanguage(t.Language, audioLanguage)).ToList();
            audio = (matches.FirstOrDefault(t => t.IsDefault) ?? matches.FirstOrDefault())?.Id;
        }

        long? subtitle = null;
        if (!SubtitlesOff && SubtitleLanguage is { } subtitleLanguage)
        {
            var matches = tracks.Where(t => t.Kind == TrackKind.Subtitle && SameLanguage(t.Language, subtitleLanguage)).ToList();
            subtitle = (matches.FirstOrDefault(t => !t.IsForced) ?? matches.FirstOrDefault())?.Id;
        }

        return (audio, subtitle, SubtitlesOff && tracks.Any(t => t.Kind == TrackKind.Subtitle));
    }

    /// <summary>True for two tags naming the same language ("en", "eng", "English", "fre" and "fra").</summary>
    public static bool SameLanguage(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var nameA = TrackLabels.LanguageName(a);
        return nameA is not null && nameA.Equals(TrackLabels.LanguageName(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Known(string? tag) => TrackLabels.LanguageName(tag) is null ? null : tag;
}
