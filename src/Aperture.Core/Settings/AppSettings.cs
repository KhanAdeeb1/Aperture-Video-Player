namespace Aperture.Core.Settings;

/// <summary>The preferences shown on the settings screen. Every value here changes real behaviour.</summary>
/// <param name="ResumePlayback">Start files from where they were left.</param>
/// <param name="RememberHistory">Keep a record of played files for the home screen.</param>
/// <param name="ControlsHideDelaySeconds">Idle time before playback controls and the cursor hide.</param>
public sealed record AppSettings(
    bool ResumePlayback,
    bool RememberHistory,
    int ControlsHideDelaySeconds)
{
    /// <summary>The choices offered for <see cref="ControlsHideDelaySeconds"/>.</summary>
    public static IReadOnlyList<int> HideDelayChoices { get; } = [2, 3, 5, 10];

    /// <summary>Defaults for a first launch.</summary>
    public static AppSettings Default { get; } = new(ResumePlayback: true, RememberHistory: true, ControlsHideDelaySeconds: 3);

    /// <summary>Returns a copy with out-of-range values replaced by defaults.</summary>
    public AppSettings Normalized() =>
        HideDelayChoices.Contains(ControlsHideDelaySeconds)
            ? this
            : this with { ControlsHideDelaySeconds = Default.ControlsHideDelaySeconds };
}
