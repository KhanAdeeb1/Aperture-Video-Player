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
