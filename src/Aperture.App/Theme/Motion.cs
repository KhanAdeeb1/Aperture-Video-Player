namespace Aperture.App.Theme;

/// <summary>Motion tokens applied from code. Keep in step with docs/design-system.md.</summary>
internal static class Motion
{
    /// <summary>Hover and press feedback.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);

    /// <summary>Playback overlays fading in and out.</summary>
    public static readonly TimeSpan Medium = TimeSpan.FromMilliseconds(200);

    /// <summary>Switching between screens.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(300);
}
