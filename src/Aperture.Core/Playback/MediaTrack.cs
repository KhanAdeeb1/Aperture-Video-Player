namespace Aperture.Core.Playback;

/// <summary>The kind of stream a <see cref="MediaTrack"/> carries.</summary>
public enum TrackKind
{
    /// <summary>Picture.</summary>
    Video,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Subtitles or captions.</summary>
    Subtitle,
}

/// <summary>
/// One stream in the open file, as the playback engine reports it. Values the file does not
/// state are null; nothing here is guessed.
/// </summary>
/// <param name="Id">Engine track id, unique within its <see cref="Kind"/>.</param>
/// <param name="Kind">Video, audio or subtitle.</param>
/// <param name="Title">Track title from the file, if any.</param>
/// <param name="Language">Language tag from the file (usually ISO 639-2, for example "eng"), if any.</param>
/// <param name="Codec">Short codec name, for example "h264", "aac" or "subrip".</param>
/// <param name="IsSelected">True for the track that is playing.</param>
/// <param name="IsDefault">The file marks this track as the default.</param>
/// <param name="IsForced">The file marks this subtitle track as forced.</param>
/// <param name="IsExternal">The track comes from a separate file, not the video itself.</param>
/// <param name="IsImage">A still image (cover art) rather than moving video.</param>
/// <param name="Width">Picture width in pixels (video).</param>
/// <param name="Height">Picture height in pixels (video).</param>
/// <param name="FramesPerSecond">Frame rate (video).</param>
/// <param name="Channels">Channel count (audio).</param>
/// <param name="SampleRate">Sample rate in Hz (audio).</param>
/// <param name="ExternalFileName">For external tracks, the subtitle file's path.</param>
public sealed record MediaTrack(
    long Id,
    TrackKind Kind,
    string? Title = null,
    string? Language = null,
    string? Codec = null,
    bool IsSelected = false,
    bool IsDefault = false,
    bool IsForced = false,
    bool IsExternal = false,
    bool IsImage = false,
    int? Width = null,
    int? Height = null,
    double? FramesPerSecond = null,
    int? Channels = null,
    int? SampleRate = null,
    string? ExternalFileName = null);
