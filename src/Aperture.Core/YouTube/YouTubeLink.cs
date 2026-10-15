using System.Globalization;
using System.Text.RegularExpressions;

namespace Aperture.Core.YouTube;

/// <summary>A YouTube video named by a link or an id, with the time the link starts at.</summary>
/// <param name="VideoId">The 11-character video id.</param>
/// <param name="Start">Where the link asks playback to start (<c>t=</c> or <c>start=</c>), or zero.</param>
public sealed partial record YouTubeLink(string VideoId, TimeSpan Start)
{
    private static readonly string[] Hosts =
    [
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com",
        "youtube-nocookie.com", "www.youtube-nocookie.com", "youtu.be", "www.youtu.be",
    ];

    /// <summary>True for a well-formed video id (11 characters of letters, digits, '-' and '_').</summary>
    public static bool IsVideoId(string? value) => value is not null && VideoIdPattern().IsMatch(value);

    /// <summary>
    /// Reads a video from what someone pasted: a watch, share, Shorts, embed or live link, or a
    /// bare video id. Playlists, channels and anything not on YouTube's own hosts give null.
    /// </summary>
    public static YouTubeLink? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (IsVideoId(text))
        {
            return new YouTubeLink(text, TimeSpan.Zero);
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = ParseQuery(uri.Query);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            id = segments.FirstOrDefault();
        }
        else if (segments.Length == 1 && segments[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            id = query.GetValueOrDefault("v");
        }
        else if (segments.Length >= 2 && segments[0].ToLowerInvariant() is "shorts" or "embed" or "live" or "v")
        {
            id = segments[1];
        }

        if (!IsVideoId(id))
        {
            return null;
        }

        var start = ParseTime(query.GetValueOrDefault("t") ?? query.GetValueOrDefault("start"));
        if (start == TimeSpan.Zero && uri.Fragment.StartsWith("#t=", StringComparison.Ordinal))
        {
            start = ParseTime(uri.Fragment[3..]);
        }

        return new YouTubeLink(id!, start);
    }

    /// <summary>The link people share for a video, starting at <paramref name="start"/> when given.</summary>
    public static Uri WatchUri(string videoId, TimeSpan start = default)
    {
        var seconds = (long)Math.Floor(start.TotalSeconds);
        return new Uri(seconds > 0
            ? string.Create(CultureInfo.InvariantCulture, $"https://www.youtube.com/watch?v={videoId}&t={seconds}s")
            : $"https://www.youtube.com/watch?v={videoId}");
    }

    /// <summary>Reads "90", "90s", "1m30s" or "1h2m3s". Anything else is zero.</summary>
    internal static TimeSpan ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return TimeSpan.Zero;
        }

        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var plain))
        {
            return TimeSpan.FromSeconds(plain);
        }

        var match = TimePattern().Match(value);
        if (!match.Success || match.Length == 0)
        {
            return TimeSpan.Zero;
        }

        static int Part(Group group) => group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 0;
        return new TimeSpan(Part(match.Groups["h"]), Part(match.Groups["m"]), Part(match.Groups["s"]));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            values.TryAdd(key, value);
        }

        return values;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    [GeneratedRegex("^(?:(?<h>[0-9]{1,3})h)?(?:(?<m>[0-9]{1,4})m)?(?:(?<s>[0-9]{1,6})s)?$")]
    private static partial Regex TimePattern();
}
