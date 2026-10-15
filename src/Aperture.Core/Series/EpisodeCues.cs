namespace Aperture.Core.Series;

/// <summary>When the next-episode card is showing.</summary>
public enum NextUpMoment
{
    /// <summary>Not showing.</summary>
    None,

    /// <summary>The closing credits started and run to the end of the file.</summary>
    Credits,

    /// <summary>The file played to its end.</summary>
    Ended,
}

/// <summary>What the player should show or do at the current position.</summary>
/// <param name="Offer">A segment to offer a Skip button for, or null.</param>
/// <param name="SkipNow">A segment to skip automatically right now (once), or null.</param>
/// <param name="NextUp">Whether, and why, the next-episode card should show.</param>
public sealed record CueState(MediaSegment? Offer, MediaSegment? SkipNow, NextUpMoment NextUp)
{
    public static CueState Nothing { get; } = new(null, null, NextUpMoment.None);
}

/// <summary>
/// Decides, as an episode plays, when to offer Skip, when to skip by itself, and when to show
/// the next episode. Everything is based on this file's own segments; nothing is skipped
/// because of where another episode's intro was. Automatic skipping happens at most once per
/// segment and only when playback enters the segment at its start, so seeking back into an
/// intro to watch it is respected.
/// </summary>
public sealed class EpisodeCueTracker(EpisodeSegments segments, EpisodeRules rules, bool hasNext)
{
    /// <summary>Credits ending this close to the end of the file run to the end (no scene after them).</summary>
    public static readonly TimeSpan PostCreditsTolerance = TimeSpan.FromSeconds(15);

    /// <summary>How far into a segment playback may be for it still to be skipped automatically.</summary>
    public static readonly TimeSpan AutoSkipWindow = TimeSpan.FromSeconds(3);

    /// <summary>The Skip button disappears this long before the segment ends.</summary>
    public static readonly TimeSpan OfferTail = TimeSpan.FromSeconds(1);

    private static readonly SegmentKind[] Kinds = [SegmentKind.Recap, SegmentKind.Intro, SegmentKind.Credits];

    private readonly HashSet<SegmentKind> _autoSkipped = [];

    public EpisodeSegments Segments { get; private set; } = segments ?? throw new ArgumentNullException(nameof(segments));

    public EpisodeRules Rules { get; private set; } = rules ?? throw new ArgumentNullException(nameof(rules));

    public bool HasNext { get; } = hasNext;

    /// <summary>The credits in effect, when they are confident enough to act on.</summary>
    public MediaSegment? Credits =>
        Segments.Effective(SegmentKind.Credits) is { Confidence: >= SegmentPolicy.OfferThreshold } credits ? credits : null;

    /// <summary>Uses new segments (detection finished, or the user corrected one).</summary>
    public void SetSegments(EpisodeSegments updated) => Segments = updated ?? throw new ArgumentNullException(nameof(updated));

    /// <summary>Uses new rules (the user changed a setting while watching).</summary>
    public void SetRules(EpisodeRules updated) => Rules = updated ?? throw new ArgumentNullException(nameof(updated));

    /// <summary>True when <paramref name="credits"/> continue to the end of the file, with no scene after them.</summary>
    public static bool RunsToEnd(MediaSegment credits, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(credits);
        return duration <= TimeSpan.Zero || credits.End >= duration - PostCreditsTolerance;
    }

    /// <summary>What to show at <paramref name="position"/>.</summary>
    public CueState Evaluate(TimeSpan position, TimeSpan duration, bool ended)
    {
        MediaSegment? offer = null;
        MediaSegment? skipNow = null;
        if (!ended)
        {
            foreach (var kind in Kinds)
            {
                var segment = Segments.Effective(kind);
                var action = SegmentPolicy.Decide(segment, Rules.ModeFor(kind));
                if (action == SkipAction.None || segment is null || position < segment.Start || position >= segment.End - OfferTail)
                {
                    continue;
                }

                // Credits that run to the end lead into the next episode; its card is the way past them.
                if (kind == SegmentKind.Credits && HasNext && RunsToEnd(segment, duration))
                {
                    continue;
                }

                if (action == SkipAction.Skip && !_autoSkipped.Contains(kind) && position - segment.Start <= AutoSkipWindow)
                {
                    _autoSkipped.Add(kind);
                    skipNow = segment;
                }
                else
                {
                    offer = segment;
                }

                break;
            }
        }

        var nextUp = NextUpMoment.None;
        if (HasNext)
        {
            if (ended)
            {
                nextUp = NextUpMoment.Ended;
            }
            else if (Credits is { } credits && RunsToEnd(credits, duration) && position >= credits.Start)
            {
                nextUp = NextUpMoment.Credits;
            }
        }

        return new CueState(offer, skipNow, nextUp);
    }

    /// <summary>
    /// True when the next episode should count down and then play by itself. Never when autoplay
    /// is off, the user cancelled for this episode, or several episodes have already played
    /// on their own with nobody touching anything. During credits, only when Aperture is sure
    /// they are credits; otherwise the countdown waits for the real end of the file.
    /// </summary>
    public bool ShouldCountDown(NextUpMoment moment, bool cancelled, int consecutiveAutoplays) =>
        Rules.Autoplay
        && !cancelled
        && consecutiveAutoplays < Autoplay.ConsecutiveLimit
        && (moment == NextUpMoment.Ended || (moment == NextUpMoment.Credits && Credits is { Confidence: >= SegmentPolicy.AutoSkipThreshold }));
}

/// <summary>Autoplay limits.</summary>
public static class Autoplay
{
    /// <summary>After this many episodes in a row start by themselves, the next one waits for the user.</summary>
    public const int ConsecutiveLimit = 3;
}

/// <summary>The next-episode countdown: started, cancelled, and read against a clock.</summary>
public sealed class NextEpisodeCountdown(TimeProvider clock)
{
    private DateTimeOffset? _startedUtc;
    private TimeSpan _length;

    /// <summary>True while counting down.</summary>
    public bool IsRunning => _startedUtc is not null;

    /// <summary>Time left, or zero when not running.</summary>
    public TimeSpan Remaining => _startedUtc is { } started ? Max(TimeSpan.Zero, _length - (clock.GetUtcNow() - started)) : TimeSpan.Zero;

    /// <summary>True once the countdown has run out (until it is cancelled or restarted).</summary>
    public bool IsDue => IsRunning && Remaining <= TimeSpan.Zero;

    /// <summary>How much has elapsed, from 0 to 1.</summary>
    public double Fraction => IsRunning && _length > TimeSpan.Zero ? 1 - (Remaining / _length) : 0;

    /// <summary>Whole seconds left, rounded up, for "Next episode in 8 s".</summary>
    public int SecondsLeft => (int)Math.Ceiling(Remaining.TotalSeconds);

    public void Start(TimeSpan length)
    {
        if (length <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The countdown needs a positive length.");
        }

        _length = length;
        _startedUtc = clock.GetUtcNow();
    }

    public void Cancel() => _startedUtc = null;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
