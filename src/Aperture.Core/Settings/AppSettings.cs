using Aperture.Core.Playback;
using Aperture.Core.Series;

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
/// <param name="AutoplayNextEpisode">Play the next episode of a show when the countdown ends (each show can differ).</param>
/// <param name="NextEpisodeCountdownSeconds">Length of the next-episode countdown.</param>
/// <param name="IntroSkip">What to do with intros (each show can differ).</param>
/// <param name="RecapSkip">What to do with recaps (each show can differ).</param>
/// <param name="OfferSkipCredits">Show Skip credits during credits.</param>
/// <param name="FindSegments">Compare episodes in the background to find intros, recaps and credits.</param>
/// <param name="YouTubeAutoplay">Play the next video in a YouTube list when one ends (separate from episode autoplay).</param>
/// <param name="YouTubeAccepted">The user agreed to YouTube's Terms of Service and the privacy notes before using the YouTube section.</param>
/// <param name="Sound">Sound processing for every video unless a show or file says otherwise (both off by default).</param>
/// <param name="SeekPreviews">Show a picture of the video when pointing at the timeline.</param>
public sealed record AppSettings(
    ResumeMode Resume,
    bool RememberHistory,
    int ControlsHideDelaySeconds,
    bool PreservePitch = true,
    SubtitleStyle? Subtitles = null,
    double Volume = 1.0,
    bool Muted = false,
    bool OnlineMetadata = false,
    bool AutoplayNextEpisode = true,
    int NextEpisodeCountdownSeconds = 10,
    SkipMode IntroSkip = SkipMode.Button,
    SkipMode RecapSkip = SkipMode.Button,
    bool OfferSkipCredits = true,
    bool FindSegments = true,
    bool YouTubeAutoplay = false,
    bool YouTubeAccepted = false,
    SoundOptions? Sound = null,
    bool SeekPreviews = true)
{
    /// <summary>The choices offered for <see cref="ControlsHideDelaySeconds"/>.</summary>
    public static IReadOnlyList<int> HideDelayChoices { get; } = [2, 3, 5, 10];

    /// <summary>The choices offered for <see cref="NextEpisodeCountdownSeconds"/>.</summary>
    public static IReadOnlyList<int> CountdownChoices { get; } = [5, 10, 15, 20, 30];

    /// <summary>Defaults for a first launch.</summary>
    public static AppSettings Default { get; } = new(Resume: ResumeMode.Resume, RememberHistory: true, ControlsHideDelaySeconds: 3)
    {
        Subtitles = SubtitleStyle.Default,
        Sound = SoundOptions.Off,
    };

    /// <summary>Subtitle style, never null.</summary>
    public SubtitleStyle SubtitleStyle => Subtitles ?? SubtitleStyle.Default;

    /// <summary>Sound options, never null.</summary>
    public SoundOptions SoundDefaults => Sound ?? SoundOptions.Off;

    /// <summary>Returns a copy with out-of-range values replaced by defaults.</summary>
    public AppSettings Normalized() => this with
    {
        Resume = Enum.IsDefined(Resume) ? Resume : Default.Resume,
        ControlsHideDelaySeconds = HideDelayChoices.Contains(ControlsHideDelaySeconds) ? ControlsHideDelaySeconds : Default.ControlsHideDelaySeconds,
        Subtitles = Subtitles is { } style && Enum.IsDefined(style.Size) && Enum.IsDefined(style.Color) && Enum.IsDefined(style.Background)
            ? style
            : SubtitleStyle.Default,
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : Default.Volume,
        NextEpisodeCountdownSeconds = CountdownChoices.Contains(NextEpisodeCountdownSeconds) ? NextEpisodeCountdownSeconds : Default.NextEpisodeCountdownSeconds,
        IntroSkip = Enum.IsDefined(IntroSkip) ? IntroSkip : Default.IntroSkip,
        RecapSkip = Enum.IsDefined(RecapSkip) ? RecapSkip : Default.RecapSkip,
        Sound = Sound ?? SoundOptions.Off,
    };
}
