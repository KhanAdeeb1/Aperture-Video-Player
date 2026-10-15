using System.Globalization;

namespace Aperture.Core.Playback;

/// <summary>Short, readable names for tracks in the audio and subtitle menus.</summary>
public static class TrackLabels
{
    // Matroska and MP4 often carry ISO 639-2/B codes; .NET knows the /T form.
    private static readonly Dictionary<string, string> BibliographicToTerminology = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces",
        ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl",
        ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk",
        ["tib"] = "bod", ["wel"] = "cym",
    };

    private static readonly Lazy<Dictionary<string, string>> LanguageNames = new(BuildLanguageNames);

    /// <summary>
    /// The language's English name for a tag such as "eng", "en", "fre" or "pt-BR", or null
    /// when the tag is missing, "und", or unknown.
    /// </summary>
    public static string? LanguageName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = tag.Trim();
        if (BibliographicToTerminology.TryGetValue(key, out var terminology))
        {
            key = terminology;
        }

        if (LanguageNames.Value.TryGetValue(key, out var name))
        {
            return name;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(key);
            return culture.EnglishName.Length > 0 && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) && culture.EnglishName != key
                ? culture.EnglishName
                : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The menu label for a track: "English", "Japanese · Commentary", "Track 3", with codec
    /// and channel detail in <see cref="Detail"/>.
    /// </summary>
    public static string Name(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var language = LanguageName(track.Language);
        var title = string.IsNullOrWhiteSpace(track.Title) ? null : track.Title.Trim();
        if (title is not null && language is not null && title.Equals(language, StringComparison.OrdinalIgnoreCase))
        {
            title = null;
        }

        var name = (language, title) switch
        {
            (not null, not null) => $"{language} · {title}",
            (not null, null) => language,
            (null, not null) => title,
            _ => track.IsExternal && track.ExternalFileName is { } file ? Path.GetFileName(file) : $"Track {track.Id}",
        };

        return track.IsForced ? $"{name} (forced)" : name;
    }

    /// <summary>Codec and layout detail: "AAC · 5.1", "SubRip · external", "H.264 · 1920×1080".</summary>
    public static string Detail(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var parts = new List<string>(3);
        if (CodecName(track.Codec) is { } codec)
        {
            parts.Add(codec);
        }

        switch (track.Kind)
        {
            case TrackKind.Audio when track.Channels is { } channels:
                parts.Add(ChannelLayout(channels));
                break;
            case TrackKind.Video when track.Width is { } w && track.Height is { } h:
                parts.Add(FormattableString.Invariant($"{w}×{h}"));
                break;
        }

        if (track.IsExternal)
        {
            parts.Add("external");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Readable codec names for the common short names engines report.</summary>
    public static string? CodecName(string? codec) =>
        string.IsNullOrEmpty(codec) ? null : KnownCodecName(codec) ?? codec.ToUpperInvariant();

    /// <summary>The readable name for a codec Aperture knows, or null for any other.</summary>
    public static string? KnownCodecName(string? codec) => codec?.ToLowerInvariant() switch
    {
        null or "" => null,
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp8" => "VP8",
        "vp9" => "VP9",
        "mpeg2video" => "MPEG-2",
        "mpeg4" => "MPEG-4",
        "prores" => "ProRes",
        "ffv1" => "FFV1",
        "wmv1" or "wmv2" or "wmv3" => "WMV",
        "vc1" => "VC-1",
        "aac" => "AAC",
        "ac3" => "Dolby Digital",
        "eac3" => "Dolby Digital Plus",
        "truehd" => "Dolby TrueHD",
        "dts" => "DTS",
        "mp3" => "MP3",
        "mp2" => "MP2",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "flac" => "FLAC",
        "alac" => "ALAC",
        "wmav1" or "wmav2" => "WMA",
        "subrip" => "SubRip",
        "ass" or "ssa" => "ASS",
        "webvtt" => "WebVTT",
        "mov_text" => "Timed text",
        "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "VobSub",
        "dvb_subtitle" => "DVB",
        var other when other.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
        _ => null,
    };

    /// <summary>"Mono", "Stereo", "5.1", "7.1", or "N channels".</summary>
    public static string ChannelLayout(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        _ => FormattableString.Invariant($"{channels} channels"),
    };

    private static Dictionary<string, string> BuildLanguageNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (string.IsNullOrEmpty(culture.Name) || culture.EnglishName.Length == 0)
            {
                continue;
            }

            names.TryAdd(culture.ThreeLetterISOLanguageName, culture.EnglishName);
            names.TryAdd(culture.TwoLetterISOLanguageName, culture.EnglishName);
        }

        return names;
    }
}
