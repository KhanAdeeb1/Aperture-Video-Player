namespace Aperture.Core.Series;

/// <summary>What the player does with a recap or intro. Stored by name in settings; do not rename.</summary>
public enum SkipMode
{
    /// <summary>Play it; offer nothing.</summary>
    Never,

    /// <summary>Show a Skip button while it plays.</summary>
    Button,

    /// <summary>Skip it without asking, but only when Aperture is sure; otherwise show the button.</summary>
    Automatic,
}

/// <summary>What to do about a segment right now.</summary>
public enum SkipAction
{
    /// <summary>Nothing: no segment, the user turned skipping off, or detection is not sure enough.</summary>
    None,

    /// <summary>Show a Skip button.</summary>
    Offer,

    /// <summary>Skip straight away (with an Undo).</summary>
    Skip,
}

/// <summary>
/// The confidence rules. Uncertain detections are never acted on without the user: below
/// <see cref="OfferThreshold"/> nothing is shown, and only at <see cref="AutoSkipThreshold"/>
/// or above may anything be skipped automatically.
/// </summary>
public static class SegmentPolicy
{
    /// <summary>Lowest confidence for which a Skip button is shown.</summary>
    public const double OfferThreshold = 0.6;

    /// <summary>Lowest confidence for which content may be skipped without the user pressing anything.</summary>
    public const double AutoSkipThreshold = 0.85;

    /// <summary>Segments shorter than this are not worth a button.</summary>
    public static readonly TimeSpan MinimumLength = TimeSpan.FromSeconds(3);

    /// <summary>What to do about <paramref name="segment"/> under <paramref name="mode"/>.</summary>
    public static SkipAction Decide(MediaSegment? segment, SkipMode mode)
    {
        if (segment is null || mode == SkipMode.Never || segment.Length < MinimumLength || segment.Confidence < OfferThreshold)
        {
            return SkipAction.None;
        }

        return mode == SkipMode.Automatic && segment.Confidence >= AutoSkipThreshold ? SkipAction.Skip : SkipAction.Offer;
    }

    /// <summary>"Sure", "Fairly sure", "Unsure" for the correction panel.</summary>
    public static string DescribeConfidence(double confidence) =>
        confidence >= AutoSkipThreshold ? "Sure" : confidence >= OfferThreshold ? "Fairly sure" : "Unsure";
}
