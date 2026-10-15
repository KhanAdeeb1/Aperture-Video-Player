namespace Aperture.Core.Playback;

/// <summary>Subtitle text size, relative to Aperture's default.</summary>
public enum SubtitleSize
{
    Small,
    Normal,
    Large,
    ExtraLarge,
}

/// <summary>Subtitle text colour.</summary>
public enum SubtitleColor
{
    White,
    Yellow,
}

/// <summary>What sits behind subtitle text.</summary>
public enum SubtitleBackground
{
    /// <summary>A dark outline and soft shadow around each letter.</summary>
    Outline,

    /// <summary>A dark box behind each line, for busy or bright pictures.</summary>
    Box,
}

/// <summary>
/// How text subtitles (SubRip, WebVTT, timed text) look. Styled subtitles (ASS/SSA) keep the
/// author's styling except for size; image subtitles (PGS, VobSub) only scale.
/// </summary>
public sealed record SubtitleStyle(SubtitleSize Size, SubtitleColor Color, SubtitleBackground Background)
{
    /// <summary>White text with an outline at the normal size.</summary>
    public static SubtitleStyle Default { get; } = new(SubtitleSize.Normal, SubtitleColor.White, SubtitleBackground.Outline);

    /// <summary>The scale factor applied to the default text size.</summary>
    public double Scale => Size switch
    {
        SubtitleSize.Small => 0.8,
        SubtitleSize.Large => 1.25,
        SubtitleSize.ExtraLarge => 1.5,
        _ => 1.0,
    };

    /// <summary>Text colour as #RRGGBB.</summary>
    public string ColorHex => Color == SubtitleColor.Yellow ? "#F5DC5A" : "#FFFFFF";

    public static string Label(SubtitleSize size) => size switch
    {
        SubtitleSize.Small => "Small",
        SubtitleSize.Normal => "Normal",
        SubtitleSize.Large => "Large",
        SubtitleSize.ExtraLarge => "Extra large",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null),
    };

    public static string Label(SubtitleColor color) => color switch
    {
        SubtitleColor.White => "White",
        SubtitleColor.Yellow => "Yellow",
        _ => throw new ArgumentOutOfRangeException(nameof(color), color, null),
    };

    public static string Label(SubtitleBackground background) => background switch
    {
        SubtitleBackground.Outline => "Outline",
        SubtitleBackground.Box => "Dark box",
        _ => throw new ArgumentOutOfRangeException(nameof(background), background, null),
    };
}
