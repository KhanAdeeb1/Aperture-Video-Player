namespace Aperture.Video;

/// <summary>Which frame path the video surface uses.</summary>
public enum VideoRendererKind
{
    /// <summary>The GPU path (ANGLE), falling back to software if it cannot start.</summary>
    Automatic,

    /// <summary>Always the software path; for diagnosis and comparison.</summary>
    Software,
}
