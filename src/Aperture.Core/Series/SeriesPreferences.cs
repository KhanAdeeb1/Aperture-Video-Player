using Aperture.Core.Settings;

namespace Aperture.Core.Series;

/// <summary>
/// Choices the user made for one show. Null means "as in Settings", so changing the default
/// later still applies to shows the user never set.
/// </summary>
/// <param name="Autoplay">Play the next episode automatically.</param>
/// <param name="Intro">What to do with intros.</param>
/// <param name="Recap">What to do with recaps.</param>
public sealed record SeriesPreferences(bool? Autoplay = null, SkipMode? Intro = null, SkipMode? Recap = null)
{
    public static SeriesPreferences Default { get; } = new();

    /// <summary>True when nothing differs from Settings.</summary>
    public bool IsDefault => Autoplay is null && Intro is null && Recap is null;

    /// <summary>The rules in effect for this show.</summary>
    public EpisodeRules Resolve(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new EpisodeRules(
            Autoplay ?? settings.AutoplayNextEpisode,
            Intro ?? settings.IntroSkip,
            Recap ?? settings.RecapSkip,
            settings.OfferSkipCredits,
            TimeSpan.FromSeconds(settings.NextEpisodeCountdownSeconds));
    }
}

/// <summary>The rules the player follows for the episode it is playing.</summary>
/// <param name="Autoplay">Start the next episode when the countdown ends.</param>
/// <param name="Intro">What to do with the intro.</param>
/// <param name="Recap">What to do with the recap.</param>
/// <param name="OfferSkipCredits">Show Skip credits.</param>
/// <param name="Countdown">How long the next-episode countdown runs.</param>
public sealed record EpisodeRules(bool Autoplay, SkipMode Intro, SkipMode Recap, bool OfferSkipCredits, TimeSpan Countdown)
{
    /// <summary>The skip mode for a kind of segment. Credits are only ever offered, never skipped unasked.</summary>
    public SkipMode ModeFor(SegmentKind kind) => kind switch
    {
        SegmentKind.Intro => Intro,
        SegmentKind.Recap => Recap,
        _ => OfferSkipCredits ? SkipMode.Button : SkipMode.Never,
    };

    /// <summary>"Show a Skip button", "Skip when sure", "Don't offer".</summary>
    public static string Label(SkipMode mode) => mode switch
    {
        SkipMode.Button => "Show a Skip button",
        SkipMode.Automatic => "Skip automatically when sure",
        SkipMode.Never => "Don’t offer",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
