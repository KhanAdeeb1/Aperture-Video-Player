namespace Aperture.Core.Library;

/// <summary>Picture-size labels used to tell copies of the same video apart.</summary>
public static class MediaQuality
{
    /// <summary>
    /// "4K", "1080p", "720p", "SD", or null when unknown. Judged from the larger of the
    /// width-based and height-based classes, so 1920×800 scope films still read as 1080p.
    /// </summary>
    public static string? Label(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0)
        {
            return null;
        }

        var w = width.Value;
        var h = height.Value;
        // Portrait videos (phones) are judged by their long side the same way.
        if (h > w)
        {
            (w, h) = (h, w);
        }

        return w >= 3200 || h >= 1800 ? "4K"
            : w >= 1800 || h >= 1000 ? "1080p"
            : w >= 1200 || h >= 700 ? "720p"
            : "SD";
    }

    /// <summary>A number for ordering copies: higher is better.</summary>
    public static int Rank(int? width, int? height) => Label(width, height) switch
    {
        "4K" => 4,
        "1080p" => 3,
        "720p" => 2,
        "SD" => 1,
        _ => 0,
    };
}
