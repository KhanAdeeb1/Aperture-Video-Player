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

    /// <summary>Played to the end.</summary>
    Ended,

    /// <summary>The file could not be opened or playback broke; see <see cref="IPlaybackEngine.Failure"/>.</summary>
    Failed,
}

/// <summary>
/// The contract the interface uses to drive playback. Implementations raise every event
/// on the thread that created them (the UI thread), so handlers can touch the UI directly.
/// The Milestone 1 implementation uses Windows Media Foundation; libmpv replaces it later
/// behind this same contract (see docs/architecture.md).
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

    /// <summary>Playback speed, 1 being normal.</summary>
    double Rate { get; set; }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>
    /// Opens a file and starts playing it from <paramref name="startAt"/>. Failures are
    /// reported through <see cref="Status"/> and <see cref="Failure"/>, not exceptions.
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
}
