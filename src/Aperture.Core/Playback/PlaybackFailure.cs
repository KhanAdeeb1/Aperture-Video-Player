namespace Aperture.Core.Playback;

/// <summary>Why a file could not be played. Each kind has its own calm, specific message.</summary>
public enum PlaybackFailureKind
{
    /// <summary>The file does not exist at its path (moved, renamed, deleted, or on a disconnected drive).</summary>
    NotFound,

    /// <summary>The file is not a video, or its format or codec cannot be decoded.</summary>
    Unsupported,

    /// <summary>The file was found and recognised but playback failed (corrupt data, access denied, decoder error).</summary>
    Failed,
}

/// <summary>A playback failure with an optional technical detail for the error screen.</summary>
public sealed record PlaybackFailure(PlaybackFailureKind Kind, string? Detail = null);
