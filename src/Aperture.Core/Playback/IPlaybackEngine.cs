namespace Aperture.Core.Playback;

/// <summary>Where a playback engine is in the life of the current file.</summary>
public enum PlaybackStatus
{
    /// <summary>Nothing is open.</summary>
    Idle,

    /// <summary>A file is being opened; nothing has been shown yet.</summary>
    Opening,

    /// <summary>Playing.</summary>
    Playing,

    /// <summary>Open and paused.</summary>
    Paused,

    /// <summary>Played to the end. The last frame stays on screen.</summary>
    Ended,

    /// <summary>The file could not be opened or playback broke; see <see cref="IPlaybackEngine.Failure"/>.</summary>
    Failed,
}

/// <summary>
/// The contract the interface uses to drive playback. Implementations raise every event
/// on the thread that created them (the UI thread), so handlers can touch the UI directly.
/// The implementation is libmpv (<c>Aperture.Playback.Mpv</c>); see docs/architecture.md.
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    /// <summary>Current status.</summary>
    PlaybackStatus Status { get; }

    /// <summary>Why the last open or playback failed, when <see cref="Status"/> is <see cref="PlaybackStatus.Failed"/>.</summary>
    PlaybackFailure? Failure { get; }

    /// <summary>Current position.</summary>
    TimeSpan Position { get; }

    /// <summary>Duration of the open file, or zero when unknown.</summary>
    TimeSpan Duration { get; }

    /// <summary>Volume from 0 to 1.</summary>
    double Volume { get; set; }

    /// <summary>Whether audio is muted.</summary>
    bool IsMuted { get; set; }

    /// <summary>Playback speed, 1 being normal. Pitch is preserved.</summary>
    double Rate { get; set; }

    /// <summary>How the picture is fitted to the window. Kept across files.</summary>
    AspectMode Aspect { get; set; }

    /// <summary>The open file's video, audio and subtitle tracks, in file order.</summary>
    IReadOnlyList<MediaTrack> Tracks { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Raised when <see cref="Tracks"/> or the selected tracks change.</summary>
    event EventHandler? TracksChanged;

    /// <summary>
    /// Opens a file and starts playing it from <paramref name="startAt"/>. Completes once the
    /// file is playing or has failed. Failures are reported through <see cref="Status"/> and
    /// <see cref="Failure"/>, not exceptions.
    /// </summary>
    Task OpenAsync(string path, TimeSpan startAt, CancellationToken cancellationToken = default);

    /// <summary>Plays or resumes. After the end, starts again from the beginning.</summary>
    void Play();

    /// <summary>Pauses.</summary>
    void Pause();

    /// <summary>Moves to a position, clamped to the file's length.</summary>
    void Seek(TimeSpan position);

    /// <summary>Stops playback and releases the open file.</summary>
    void Close();

    /// <summary>Plays the audio track with this id.</summary>
    void SelectAudioTrack(long id);

    /// <summary>Shows the subtitle track with this id, or hides subtitles when null.</summary>
    void SelectSubtitleTrack(long? id);

    /// <summary>
    /// Loads a subtitle file for the open video and shows it. Returns null on success, or a
    /// short reason when the file could not be loaded. The subtitle file is only read.
    /// </summary>
    Task<string?> AddSubtitleFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>A snapshot of stream and playback details for the information panel.</summary>
    PlaybackDiagnostics GetDiagnostics();
}
