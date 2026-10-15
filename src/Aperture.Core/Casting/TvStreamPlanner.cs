using Aperture.Core.Server;

namespace Aperture.Core.Casting;

/// <summary>
/// What a renderer says it accepts, from its ConnectionManager's sink protocol list
/// ("http-get:*:video/mp4:DLNA.ORG_PN=AVC_MP4_HP_HD_AAC,http-get:*:video/x-matroska:*").
/// The list names file types, and only sometimes codecs (in DLNA profile names), so it is a
/// guide, not a promise: a file the TV then fails to play is retried converted.
/// </summary>
public sealed class RendererFormats
{
    private readonly HashSet<string> _mimes;
    private readonly string _profiles;

    private RendererFormats(HashSet<string> mimes, string profiles, bool acceptsAll)
    {
        _mimes = mimes;
        _profiles = profiles;
        AcceptsAll = acceptsAll;
    }

    /// <summary>The renderer accepts any type ("*"), or didn't say what it accepts.</summary>
    public bool AcceptsAll { get; }

    public IReadOnlyCollection<string> Mimes => _mimes;

    /// <summary>A renderer that didn't answer: assume the common types and let a failure fall back to conversion.</summary>
    public static RendererFormats Unknown { get; } = new(new HashSet<string>(StringComparer.Ordinal), string.Empty, acceptsAll: true);

    public static RendererFormats Parse(string? sink)
    {
        if (string.IsNullOrWhiteSpace(sink))
        {
            return Unknown;
        }

        var mimes = new HashSet<string>(StringComparer.Ordinal);
        var profiles = new List<string>();
        var all = false;
        foreach (var entry in sink.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = entry.Split(':', 4);
            if (fields.Length < 3 || !fields[0].Equals("http-get", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mime = fields[2].Trim().ToLowerInvariant();
            if (mime == "*")
            {
                all = true;
            }
            else if (mime.Length > 0)
            {
                mimes.Add(mime);
            }

            if (fields.Length == 4)
            {
                profiles.Add(fields[3].ToUpperInvariant());
            }
        }

        return new RendererFormats(mimes, string.Join(';', profiles), all || mimes.Count == 0);
    }

    /// <summary>The first of <paramref name="alternatives"/> the renderer lists, or the first one when it accepts anything; null when it lists none.</summary>
    public string? Pick(IReadOnlyList<string> alternatives)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        foreach (var mime in alternatives)
        {
            if (_mimes.Contains(mime))
            {
                return mime;
            }
        }

        return AcceptsAll && alternatives.Count > 0 ? alternatives[0] : null;
    }

    /// <summary>True when a DLNA profile or type mentions <paramref name="word"/> ("HEVC").</summary>
    public bool Mentions(string word) =>
        _profiles.Contains(word, StringComparison.OrdinalIgnoreCase) || _mimes.Any(m => m.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How a file goes to a TV: the stream plan, and the type to announce.</summary>
/// <param name="Plan">Direct play, or a continuous MPEG-TS (remuxed or converted), or unavailable.</param>
/// <param name="Mime">The MIME type to send, chosen from the renderer's own list.</param>
public sealed record TvPlan(StreamPlan Plan, string Mime)
{
    /// <summary>The original file, which the TV can seek in by bytes.</summary>
    public bool IsDirect => Plan.Mode == StreamMode.DirectPlay;
}

/// <summary>
/// Chooses between sending a file as it is and sending a continuous MPEG-TS, for a DLNA TV.
/// TVs reliably play H.264 with AAC, Dolby Digital or MP3; anything else is only trusted when
/// the TV names it. MPEG-TS is the stream format DLNA TVs handle most widely.
/// </summary>
public static class TvStreamPlanner
{
    private static readonly HashSet<string> TvAudio = new(StringComparer.Ordinal) { "aac", "ac3", "eac3", "mp3" };

    private static readonly string[] TransportMimes = ["video/mpeg", "video/mp2t", "video/vnd.dlna.mpeg-tts", "video/x-mpegts"];

    /// <param name="source">What FFprobe found, or null without FFmpeg (then the extension decides).</param>
    /// <param name="extension">The file's extension (".mkv").</param>
    /// <param name="formats">What the renderer accepts.</param>
    /// <param name="canConvert">True when FFmpeg is available.</param>
    /// <param name="avoidDirect">True when the TV already failed to play the original.</param>
    public static TvPlan Decide(SourceMedia? source, string extension, RendererFormats formats, bool canConvert, bool avoidDirect = false)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(formats);

        var container = source?.Container ?? ContainerFromExtension(extension);
        var audioOnly = source is not null ? source.VideoCodec is null : Media.MediaFileTypes.IsAudioFile("x" + extension);
        var directMime = formats.Pick(MimeAlternatives(container, extension, audioOnly));
        var transportMime = formats.Pick(TransportMimes) ?? TransportMimes[0];

        if (!avoidDirect && directMime is not null)
        {
            if (source is null)
            {
                return new TvPlan(new StreamPlan(StreamMode.DirectPlay, "Plays the original file. Without FFmpeg Aperture can't check what's inside it first."), directMime);
            }

            var videoOk = source.VideoCodec is null || VideoOk(source, formats);
            var audioOk = source.AudioCodec is null || TvAudio.Contains(source.AudioCodec) || formats.Mentions(source.AudioCodec);
            if (videoOk && audioOk)
            {
                return new TvPlan(new StreamPlan(StreamMode.DirectPlay, $"Plays the original file ({source.Describe()})."), directMime);
            }
        }

        string what;
        if (avoidDirect)
        {
            what = "The TV couldn't play the original file";
        }
        else if (directMime is null)
        {
            what = $"The TV doesn't accept {StreamPlanner.ContainerName(container)} files";
        }
        else if (source?.VideoCodec is { } v && !VideoOk(source, formats))
        {
            what = $"The TV may not play {StreamPlanner.CodecName(v)}{(v == "h264" ? " with 10-bit colour" : string.Empty)} video";
        }
        else
        {
            what = source?.AudioCodec is { } a ? $"The TV may not play {StreamPlanner.CodecName(a)} sound" : "The TV may not play this file";
        }

        if (!canConvert)
        {
            return new TvPlan(new StreamPlan(StreamMode.Unavailable, $"{what}, and converting it needs FFmpeg on this computer."), directMime ?? transportMime);
        }

        if (audioOnly)
        {
            return new TvPlan(new StreamPlan(StreamMode.Unavailable, $"{what}, and Aperture only converts videos for TVs."), directMime ?? transportMime);
        }

        if (source is { VideoCodec: "h264", BitDepth: null or <= 8 } && !avoidDirect)
        {
            var copyAudio = source.AudioCodec is null || TvAudio.Contains(source.AudioCodec);
            var reason = copyAudio
                ? $"{what}, so the H.264 video is repackaged without re-encoding."
                : $"{what}, so the H.264 video is repackaged without re-encoding and the sound converted to AAC.";
            return new TvPlan(new StreamPlan(StreamMode.Remux, reason, CopyVideo: true, CopyAudio: copyAudio, Progressive: true, TransportStream: true), transportMime);
        }

        var (height, kbps) = StreamPlanner.ChooseRung(source?.Height, null);
        return new TvPlan(
            new StreamPlan(StreamMode.Convert, $"{what}, so it is converted to H.264 {height}p.", TargetHeight: height, VideoBitrateKbps: kbps, Progressive: true, TransportStream: true),
            transportMime);
    }

    /// <summary>Container name for a file FFprobe couldn't read.</summary>
    public static string ContainerFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".mov" or ".m4a" => "mp4",
        ".mkv" => "matroska",
        ".webm" => "webm",
        ".avi" => "avi",
        ".ts" or ".m2ts" or ".mts" => "mpegts",
        ".mp3" => "mp3",
        ".flac" => "flac",
        ".wav" => "wav",
        ".ogg" or ".oga" => "ogg",
        ".aac" => "aac",
        _ => extension.TrimStart('.').ToLowerInvariant(),
    };

    /// <summary>The MIME types a renderer may list for a container, most common spelling first.</summary>
    public static IReadOnlyList<string> MimeAlternatives(string container, string extension, bool audioOnly)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(extension);
        return container switch
        {
            "mp4" when audioOnly => ["audio/mp4", "audio/x-m4a", "audio/aac"],
            "mp4" => ["video/mp4", "video/x-m4v", "video/quicktime"],
            "matroska" => ["video/x-matroska", "video/x-mkv", "video/mkv"],
            "webm" => ["video/webm"],
            "avi" => ["video/x-msvideo", "video/avi", "video/msvideo", "video/divx"],
            "mpegts" => [.. TransportMimes],
            "mp3" => ["audio/mpeg", "audio/mp3"],
            "flac" => ["audio/flac", "audio/x-flac"],
            "wav" => ["audio/wav", "audio/x-wav", "audio/l16"],
            "ogg" => ["audio/ogg", "application/ogg"],
            "aac" => ["audio/aac", "audio/x-aac", "audio/mp4"],
            _ => [],
        };
    }

    private static bool VideoOk(SourceMedia source, RendererFormats formats) => source.VideoCodec switch
    {
        "h264" => source.BitDepth is null or <= 8,
        "hevc" => formats.Mentions("HEVC") || formats.Mentions("H265"),
        "vp9" => formats.Mentions("VP9"),
        "av1" => formats.Mentions("AV1"),
        _ => false,
    };
}
