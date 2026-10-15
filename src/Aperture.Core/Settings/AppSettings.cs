using Aperture.Core.Playback;

namespace Aperture.Core.Settings;

/// <summary>The preferences shown on the settings screen. Every value here changes real behaviour.</summary>
/// <param name="Resume">What happens when a video left part-way is opened again.</param>
/// <param name="RememberHistory">Keep a record of played files for the home screen.</param>
/// <param name="ControlsHideDelaySeconds">Idle time before playback controls and the cursor hide.</param>
/// <param name="PreservePitch">Keep voices at their natural pitch when playing faster or slower.</param>
/// <param name="Subtitles">How text subtitles look.</param>
/// <param name="Volume">Last volume, 0 to 1, restored at the next launch.</param>
/// <param name="Muted">Whether sound was muted when Aperture last played.</param>
/// <param name="OnlineMetadata">Look up artwork and details for library items online (off unless chosen).</param>
public sealed record AppSettings(
    ResumeMode Resume,
    bool RememberHistory,
    int ControlsHideDelaySeconds,
    bool PreservePitch = true,
    SubtitleStyle? Subtitles = null,
    double Volume = 1.0,
    bool Muted = false,
    bool OnlineMetadata = false)
{
    /// <summary>The choices offered for <see cref="ControlsHideDelaySeconds"/>.</summary>
    public static IReadOnlyList<int> HideDelayChoices { get; } = [2, 3, 5, 10];

    /// <summary>Defaults for a first launch.</summary>
    public static AppSettings Default { get; } = new(Resume: ResumeMode.Resume, RememberHistory: true, ControlsHideDelaySeconds: 3)
    {
        Subtitles = SubtitleStyle.Default,
    };

    /// <summary>Subtitle style, never null.</summary>
    public SubtitleStyle SubtitleStyle => Subtitles ?? SubtitleStyle.Default;

    /// <summary>Returns a copy with out-of-range values replaced by defaults.</summary>
    public AppSettings Normalized() => this with
    {
        Resume = Enum.IsDefined(Resume) ? Resume : Default.Resume,
        ControlsHideDelaySeconds = HideDelayChoices.Contains(ControlsHideDelaySeconds) ? ControlsHideDelaySeconds : Default.ControlsHideDelaySeconds,
        Subtitles = Subtitles is { } style && Enum.IsDefined(style.Size) && Enum.IsDefined(style.Color) && Enum.IsDefined(style.Background)
            ? style
            : SubtitleStyle.Default,
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : Default.Volume,
    };
}
