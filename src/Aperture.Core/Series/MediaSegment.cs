namespace Aperture.Core.Series;

/// <summary>A part of an episode the user may want to skip.</summary>
public enum SegmentKind
{
    /// <summary>"Previously on…": scenes from earlier episodes at the start.</summary>
    Recap,

    /// <summary>The opening titles and theme.</summary>
    Intro,

    /// <summary>The closing credits.</summary>
    Credits,
}

/// <summary>Where a segment's times came from.</summary>
public enum SegmentSource
{
    /// <summary>Measured by comparing this episode's sound and picture with other episodes.</summary>
    Detected,

    /// <summary>A chapter in the file named for it ("Opening", "End Credits").</summary>
    Chapter,

    /// <summary>Set or confirmed by the user. Always wins, never replaced by detection.</summary>
    Manual,
}

/// <summary>
/// A recap, intro or credits sequence in one file, with how sure Aperture is about it.
/// Times are the file's own: a segment is never copied from another episode.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Confidence">From 0 to 1; see <see cref="SegmentPolicy"/> for what each level allows.</param>
/// <param name="Source">How it was found.</param>
/// <param name="Evidence">A short explanation for the correction panel ("Matched 4 other episodes").</param>
public sealed record MediaSegment(SegmentKind Kind, TimeSpan Start, TimeSpan End, double Confidence, SegmentSource Source, string? Evidence = null)
{
    public TimeSpan Length => End - Start;

    /// <summary>True when <paramref name="position"/> is inside the segment.</summary>
    public bool Contains(TimeSpan position) => position >= Start && position < End;

    /// <summary>"Intro", "Recap", "Credits".</summary>
    public static string Label(SegmentKind kind) => kind switch
    {
        SegmentKind.Recap => "Recap",
        SegmentKind.Intro => "Intro",
        SegmentKind.Credits => "Credits",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>
/// A user's correction for one kind of segment in one file: new times, or "this episode has
/// none" (both times null).
/// </summary>
/// <param name="Kind">The segment corrected.</param>
/// <param name="Start">The corrected start, or null with <paramref name="End"/> for "none".</param>
/// <param name="End">The corrected end, or null with <paramref name="Start"/> for "none".</param>
public sealed record SegmentCorrection(SegmentKind Kind, TimeSpan? Start, TimeSpan? End)
{
    /// <summary>Shortest segment a correction may describe.</summary>
    public static readonly TimeSpan MinimumLength = TimeSpan.FromSeconds(1);

    /// <summary>True when the user said the file has no segment of this kind.</summary>
    public bool IsNone => Start is null || End is null;

    /// <summary>Checks a correction before it is saved; returns why it can't be, or null.</summary>
    public static string? Validate(TimeSpan start, TimeSpan end, TimeSpan duration)
    {
        if (start < TimeSpan.Zero || (duration > TimeSpan.Zero && end > duration + TimeSpan.FromSeconds(1)))
        {
            return "The times must be inside the video.";
        }

        return end - start < MinimumLength ? "The end must be at least a second after the start." : null;
    }
}

/// <summary>Everything stored about one file's segments: what detection found and what the user corrected.</summary>
/// <param name="Detected">Segments found by detection or chapters.</param>
/// <param name="Corrections">The user's corrections, at most one per kind.</param>
public sealed record EpisodeSegments(IReadOnlyList<MediaSegment> Detected, IReadOnlyList<SegmentCorrection> Corrections)
{
    public static EpisodeSegments None { get; } = new([], []);

    /// <summary>The segment in effect: the user's correction when there is one, otherwise the most confident detection.</summary>
    public MediaSegment? Effective(SegmentKind kind)
    {
        if (Corrections.FirstOrDefault(c => c.Kind == kind) is { } correction)
        {
            return correction.IsNone ? null : new MediaSegment(kind, correction.Start!.Value, correction.End!.Value, 1.0, SegmentSource.Manual, "Set by you");
        }

        return Detected.Where(s => s.Kind == kind).MaxBy(s => s.Confidence);
    }

    /// <summary>The user's correction of one kind, if any.</summary>
    public SegmentCorrection? CorrectionFor(SegmentKind kind) => Corrections.FirstOrDefault(c => c.Kind == kind);

    /// <summary>The segment detection found, ignoring corrections.</summary>
    public MediaSegment? DetectedFor(SegmentKind kind) => Detected.Where(s => s.Kind == kind).MaxBy(s => s.Confidence);
}
