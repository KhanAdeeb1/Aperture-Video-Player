using System.Globalization;

namespace Aperture.Core.Casting;

/// <summary>UPnP AV times: "H+:MM:SS" with optional fractions ("0:01:02.500"), as renderers report and accept them.</summary>
public static class DlnaTime
{
    /// <summary>Seconds, or null for "NOT_IMPLEMENTED", empty or anything malformed.</summary>
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length != 3)
        {
            return null;
        }

        // Some renderers write fractions as "S.F0/F1"; only the whole seconds matter for those.
        var secondsText = parts[2];
        var slash = secondsText.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            var dot = secondsText.LastIndexOf('.', slash);
            secondsText = dot > 0 ? secondsText[..dot] : string.Empty;
        }

        if (!long.TryParse(parts[0].TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(secondsText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || minutes >= 60 || seconds >= 60 || hours > 1000)
        {
            return null;
        }

        return hours * 3600 + minutes * 60 + seconds;
    }

    /// <summary>"1:02:03" (whole seconds, which every renderer accepts).</summary>
    public static string Format(double seconds)
    {
        var whole = (long)Math.Max(0, Math.Floor(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}");
    }

    /// <summary>"1:02:03.000", the form DIDL-Lite's duration attribute uses.</summary>
    public static string FormatDuration(double seconds) => Format(seconds) + ".000";
}
