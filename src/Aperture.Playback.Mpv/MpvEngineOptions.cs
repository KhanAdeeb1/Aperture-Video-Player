namespace Aperture.Playback.Mpv;

/// <summary>How an <see cref="MpvPlaybackEngine"/> is set up.</summary>
public sealed record MpvEngineOptions
{
    /// <summary>
    /// libmpv video output. "libmpv" renders through the render API into Aperture's own
    /// surface (the app); "null" decodes without showing anything (tests).
    /// </summary>
    public string VideoOutput { get; init; } = "libmpv";

    /// <summary>libmpv audio output, or null for the platform default. "null" discards sound (tests).</summary>
    public string? AudioOutput { get; init; }

    /// <summary>Hardware decoding preference passed to libmpv's <c>hwdec</c> option.</summary>
    public string HardwareDecoding { get; init; } = "no";

    /// <summary>Lowest libmpv log level forwarded to <see cref="Log"/> ("warn" by default).</summary>
    public string LogLevel { get; init; } = "warn";

    /// <summary>Receives (level, message) for engine and libmpv messages. File paths are replaced with "&lt;file&gt;".</summary>
    public Action<string, string> Log { get; init; } = static (_, _) => { };

    /// <summary>Extra libmpv options applied last, for tests and diagnostics.</summary>
    public IReadOnlyDictionary<string, string> ExtraOptions { get; init; } = new Dictionary<string, string>();
}
