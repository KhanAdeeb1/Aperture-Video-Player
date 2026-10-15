namespace Aperture.Core.Playback;

/// <summary>
/// Choices remembered for one file: tracks, timing offsets, picture shape and speed. Null
/// means "not chosen", so the engine's own default applies. Kept in Aperture's database;
/// the media file is never touched.
/// </summary>
/// <param name="AudioTrackId">The chosen audio track's id in the file.</param>
/// <param name="SubtitleTrackId">The chosen subtitle track's id, when <paramref name="SubtitlesOff"/> is false.</param>
/// <param name="SubtitlesOff">True when the user turned subtitles off for this file.</param>
/// <param name="SubtitleDelay">Subtitle timing offset.</param>
/// <param name="AudioDelay">Sound timing offset.</param>
/// <param name="Aspect">Picture shape.</param>
/// <param name="Speed">Playback speed.</param>
public sealed record PlaybackPreferences(
    long? AudioTrackId = null,
    long? SubtitleTrackId = null,
    bool SubtitlesOff = false,
    TimeSpan SubtitleDelay = default,
    TimeSpan AudioDelay = default,
    AspectMode? Aspect = null,
    double? Speed = null)
{
    /// <summary>Nothing chosen.</summary>
    public static PlaybackPreferences None { get; } = new();

    /// <summary>True when nothing differs from <see cref="None"/>.</summary>
    public bool IsEmpty => this == None;
}

/// <summary>What the player does with a file that was left part-way through.</summary>
public enum ResumeMode
{
    /// <summary>Start from the saved position, with a "Start over" button.</summary>
    Resume,

    /// <summary>Open paused at the saved position and offer "Resume" or "Start over".</summary>
    Ask,

    /// <summary>Always start from the beginning.</summary>
    StartOver,
}

/// <summary>Turns the player's state into remembered choices for a file, and back.</summary>
public static class RememberedChoices
{
    /// <summary>
    /// What to remember about a file when leaving it. Subtitles loaded from a separate file are
    /// not remembered by number (their numbers change each time), and normal speed and the
    /// original picture shape are stored as "nothing chosen".
    /// </summary>
    public static PlaybackPreferences Capture(IReadOnlyList<MediaTrack> tracks, TimeSpan subtitleDelay, TimeSpan audioDelay, AspectMode aspect, double speed)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var audio = tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        var subtitles = tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
        var shownSubtitle = subtitles.FirstOrDefault(t => t.IsSelected);
        return new PlaybackPreferences(
            AudioTrackId: audio.Count > 1 ? audio.FirstOrDefault(t => t.IsSelected)?.Id : null,
            SubtitleTrackId: shownSubtitle is { IsExternal: false } ? shownSubtitle.Id : null,
            SubtitlesOff: subtitles.Count > 0 && shownSubtitle is null,
            SubtitleDelay: SyncOffset.Normalize(subtitleDelay),
            AudioDelay: SyncOffset.Normalize(audioDelay),
            Aspect: aspect == AspectMode.Original ? null : aspect,
            Speed: Math.Abs(speed - PlaybackSpeed.Normal) < 0.001 ? null : speed);
    }

    /// <summary>
    /// Which tracks to select when the file opens again. Null means leave the engine's own
    /// choice; tracks that no longer exist are ignored.
    /// </summary>
    public static (long? AudioTrackId, long? SubtitleTrackId, bool SubtitlesOff) Tracks(PlaybackPreferences preferences, IReadOnlyList<MediaTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(tracks);
        long? audio = preferences.AudioTrackId is { } a && tracks.Any(t => t.Kind == TrackKind.Audio && t.Id == a) ? a : null;
        long? subtitle = preferences.SubtitleTrackId is { } s && tracks.Any(t => t.Kind == TrackKind.Subtitle && t.Id == s) ? s : null;
        return (audio, subtitle, subtitle is null && preferences.SubtitlesOff);
    }
}
