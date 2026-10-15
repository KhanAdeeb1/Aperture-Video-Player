using Aperture.Core.Playback;
using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv;

/// <summary>Turns libmpv's end-of-file error codes into Aperture's failure kinds.</summary>
internal static class MpvFailures
{
    /// <summary>The failure for a file that ended with <paramref name="error"/> before or during playback.</summary>
    public static PlaybackFailure FromEndFile(int error) => error switch
    {
        MpvError.UnknownFormat => new PlaybackFailure(PlaybackFailureKind.Damaged, "The file’s contents aren’t a format Aperture recognises."),
        MpvError.LoadingFailed => new PlaybackFailure(PlaybackFailureKind.Damaged, "The file could not be read as a video."),
        MpvError.NothingToPlay => new PlaybackFailure(PlaybackFailureKind.Unsupported, "It contains no video or audio stream, or none that the engine can decode."),
        MpvError.Unsupported => new PlaybackFailure(PlaybackFailureKind.Unsupported, "This kind of file isn’t supported."),
        MpvError.VideoOutputInitFailed => new PlaybackFailure(PlaybackFailureKind.Failed, "The video display could not start."),
        MpvError.AudioOutputInitFailed => new PlaybackFailure(PlaybackFailureKind.Failed, "No sound device could be opened."),
        _ => new PlaybackFailure(PlaybackFailureKind.Failed, $"Playback engine error: {SafeDescribe(error)}."),
    };

    private static string SafeDescribe(int error)
    {
        try
        {
            return MpvNative.Describe(error);
        }
        catch (DllNotFoundException)
        {
            return FormattableString.Invariant($"code {error}");
        }
    }
}
