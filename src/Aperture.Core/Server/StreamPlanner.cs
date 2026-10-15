using System.Globalization;

namespace Aperture.Core.Server;

/// <summary>What inspecting a file found, in the terms a stream decision needs. Any part may be unknown.</summary>
/// <param name="Container">"mp4", "matroska", "webm", "avi", "mpegts" or another name.</param>
/// <param name="VideoCodec">"h264", "hevc", "vp9", "av1"… or null when there is no video or it is unknown.</param>
/// <param name="Width">Picture width.</param>
/// <param name="Height">Picture height.</param>
/// <param name="BitDepth">Bits per colour sample (8, 10).</param>
/// <param name="BitrateBps">Average bitrate of the whole file.</param>
/// <param name="AudioCodec">"aac", "ac3", "eac3", "dts", "opus", "mp3", "flac"… or null when there is no audio.</param>
/// <param name="AudioChannels">Channels of the first audio track.</param>
/// <param name="Duration">Length.</param>
public sealed record SourceMedia(
    string Container,
    string? VideoCodec,
    int? Width,
    int? Height,
    int? BitDepth,
    long? BitrateBps,
    string? AudioCodec,
    int? AudioChannels,
    TimeSpan? Duration)
{
    /// <summary>"H.264 1920×1080, AAC stereo, MP4, 8.2 Mbps", for diagnostics.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (VideoCodec is not null)
        {
            parts.Add(Width is { } w && Height is { } h ? $"{StreamPlanner.CodecName(VideoCodec)} {w}×{h}" : StreamPlanner.CodecName(VideoCodec));
        }

        if (AudioCodec is not null)
        {
            parts.Add($"{StreamPlanner.CodecName(AudioCodec)} {StreamPlanner.ChannelName(AudioChannels)}".TrimEnd());
        }

        parts.Add(StreamPlanner.ContainerName(Container));
        if (BitrateBps is { } bps)
        {
            parts.Add(StreamPlanner.FormatRate(bps));
        }

        return string.Join(", ", parts);
    }
}

/// <summary>What a browser said it can play. Codec names match <see cref="SourceMedia"/>.</summary>
/// <param name="VideoCodecs">Video codecs it plays in MP4 (and WebM for vp8, vp9, av1 when <paramref name="WebM"/>).</param>
/// <param name="AudioCodecs">Audio codecs it plays.</param>
/// <param name="Hls">True when it plays HTTP Live Streaming natively (Safari, iPhone, Chrome on Android).</param>
/// <param name="WebM">True when it plays WebM files.</param>
public sealed record ClientCapabilities(IReadOnlySet<string> VideoCodecs, IReadOnlySet<string> AudioCodecs, bool Hls, bool WebM)
{
    /// <summary>Builds capabilities from what a client sent, keeping only names Aperture knows.</summary>
    public static ClientCapabilities From(IEnumerable<string>? video, IEnumerable<string>? audio, bool hls, bool webm) => new(
        (video ?? []).Select(c => c.ToLowerInvariant()).Where(StreamPlanner.KnownVideo.Contains).ToHashSet(StringComparer.Ordinal),
        (audio ?? []).Select(c => c.ToLowerInvariant()).Where(StreamPlanner.KnownAudio.Contains).ToHashSet(StringComparer.Ordinal),
        hls,
        webm);
}

/// <summary>How a file reaches the device.</summary>
public enum StreamMode
{
    /// <summary>The original file, byte for byte.</summary>
    DirectPlay,

    /// <summary>The original video in a new container (HLS), with the sound converted only if needed. Cheap.</summary>
    Remux,

    /// <summary>Video and sound converted to H.264 and AAC (HLS). Uses the processor or graphics hardware.</summary>
    Convert,

    /// <summary>It cannot be played on this device; <see cref="StreamPlan.Reason"/> says why.</summary>
    Unavailable,
}

/// <summary>The decision for one playback.</summary>
/// <param name="Mode">How the file is sent.</param>
/// <param name="Reason">One sentence for the diagnostics panel, or for the device when it cannot play.</param>
/// <param name="CopyVideo">Remux: the video is copied as it is.</param>
/// <param name="CopyAudio">Remux or convert: the sound is copied as it is.</param>
/// <param name="TargetHeight">Convert: picture height of the stream.</param>
/// <param name="VideoBitrateKbps">Convert: video bitrate of the stream.</param>
/// <param name="Warning">A note for the device, such as "may pause to load" when the connection is slow.</param>
/// <param name="AudioOnly">The file has no picture (a podcast or music).</param>
/// <param name="Progressive">Sent as one continuous fragmented MP4 instead of HLS, for browsers without HLS.
/// It can't be seeked past what has arrived; starting elsewhere starts a new stream.</param>
public sealed record StreamPlan(StreamMode Mode, string Reason, bool CopyVideo = false, bool CopyAudio = false, int? TargetHeight = null, int? VideoBitrateKbps = null, string? Warning = null, bool AudioOnly = false, bool Progressive = false)
{
    /// <summary>Kbps of converted sound.</summary>
    public const int AudioBitrateKbps = 160;
}

/// <summary>
/// Chooses direct play, remuxing or conversion for one file and one device. Direct play wins
/// whenever the device can play the file and the connection can carry it; conversion is the
/// last resort and needs FFmpeg.
/// </summary>
public static class StreamPlanner
{
    /// <summary>Share of the measured connection a stream may use, leaving room for variation.</summary>
    public const double BandwidthHeadroom = 0.75;

    internal static readonly HashSet<string> KnownVideo = new(StringComparer.Ordinal) { "h264", "hevc", "vp8", "vp9", "av1" };

    internal static readonly HashSet<string> KnownAudio = new(StringComparer.Ordinal) { "aac", "mp3", "opus", "vorbis", "flac", "ac3", "eac3", "alac" };

    private static readonly HashSet<string> WebMVideo = new(StringComparer.Ordinal) { "vp8", "vp9", "av1" };

    private static readonly HashSet<string> WebMAudio = new(StringComparer.Ordinal) { "opus", "vorbis" };

    // Containers browsers open for sound alone, when they know the codec.
    private static readonly HashSet<string> AudioContainers = new(StringComparer.Ordinal) { "mp4", "mp3", "aac", "flac", "ogg", "wav", "webm" };

    // Audio HLS (MPEG-TS) carries without converting.
    private static readonly HashSet<string> HlsAudio = new(StringComparer.Ordinal) { "aac", "mp3" };

    /// <summary>Picture heights and video bitrates for converted streams, best first.</summary>
    public static IReadOnlyList<(int Height, int Kbps)> Ladder { get; } = [(1080, 8000), (720, 4000), (480, 1800), (360, 900)];

    /// <param name="source">What the file holds.</param>
    /// <param name="client">What the device plays.</param>
    /// <param name="bandwidthBps">The device's measured download rate, or null when unknown.</param>
    /// <param name="canConvert">True when FFmpeg is available.</param>
    /// <param name="avoidDirectPlay">True when the device already failed to play the original.</param>
    public static StreamPlan Decide(SourceMedia source, ClientCapabilities client, long? bandwidthBps, bool canConvert, bool avoidDirectPlay = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(client);

        var video = source.VideoCodec;
        var audio = source.AudioCodec;
        var budget = bandwidthBps is > 0 ? (long)(bandwidthBps.Value * BandwidthHeadroom) : (long?)null;
        var fits = budget is not { } b || source.BitrateBps is not { } rate || rate <= b;

        var videoOk = video is null || (client.VideoCodecs.Contains(video) && !(video == "h264" && source.BitDepth > 8));
        var audioOk = audio is null || client.AudioCodecs.Contains(audio);
        var containerOk = video is null ? AudioContainers.Contains(source.Container) : source.Container switch
        {
            "mp4" => video is null || !WebMVideo.Contains(video) || video == "av1",
            "webm" => client.WebM && (video is null || WebMVideo.Contains(video)) && (audio is null || WebMAudio.Contains(audio)),
            _ => false,
        };
        var playable = videoOk && audioOk && containerOk && !avoidDirectPlay;

        if (playable && fits)
        {
            return new StreamPlan(StreamMode.DirectPlay, video is null && audio is not null
                ? $"Plays the original file: this device plays {CodecName(audio)} sound."
                : $"Plays the original file: this device supports {Supported(source)}.");
        }

        var slowWarning = budget is { } bb && source.BitrateBps is { } r
            ? $"The file needs about {FormatRate(r)} and your connection measured {FormatRate(bandwidthBps!.Value)}, so it may pause to load."
            : null;

        if (!canConvert)
        {
            if (playable)
            {
                return new StreamPlan(StreamMode.DirectPlay, "Plays the original file. A smaller version needs FFmpeg on the server.", Warning: slowWarning);
            }

            return Unavailable(source, client, "Converting it needs FFmpeg on the computer running the server.");
        }

        // Conversions produce H.264 and AAC; a device that plays neither gains nothing from one.
        if ((video is not null && !client.VideoCodecs.Contains("h264")) || (audio is not null && video is null && !client.AudioCodecs.Contains("aac")))
        {
            if (video == "h264")
            {
                return new StreamPlan(StreamMode.Unavailable, "This device can't play H.264 video, which is also what Aperture converts to.");
            }

            return Unavailable(source, client, video is not null
                ? "It also can't play H.264, which is what Aperture converts to."
                : "It also can't play AAC, which is what Aperture converts to.");
        }

        // Browsers without HLS (Firefox, most desktop browsers) get one continuous MP4 instead.
        var progressive = !client.Hls;

        if (video is null)
        {
            return new StreamPlan(
                StreamMode.Convert,
                audio is not null && !audioOk ? $"Converted to AAC: this device can't play {CodecName(audio)} sound." : "Converted to AAC so this device can play it.",
                AudioOnly: true,
                Progressive: progressive);
        }

        // The video is fine and fits, but the container or the sound isn't: repackage, convert only the sound.
        if (video is "h264" && source.BitDepth is null or <= 8 && client.VideoCodecs.Contains("h264") && fits && !avoidDirectPlay)
        {
            var copyAudio = audio is null || (HlsAudio.Contains(audio) && client.AudioCodecs.Contains(audio));
            var why = !containerOk
                ? $"This device can't open {ContainerName(source.Container)} files, so the H.264 video is repackaged without re-encoding"
                : "The H.264 video is repackaged without re-encoding";
            why += copyAudio || audio is null ? "." : $" and the {CodecName(audio)} sound is converted to AAC.";
            return new StreamPlan(StreamMode.Remux, why, CopyVideo: true, CopyAudio: copyAudio, Progressive: progressive);
        }

        var (height, kbps) = ChooseRung(source.Height, bandwidthBps);
        var reason = video is not null && !videoOk && !avoidDirectPlay
            ? $"Converted to H.264 {height}p: this device can't play {CodecName(video)}{(video == "h264" ? " with 10-bit colour" : string.Empty)}."
            : !fits && slowWarning is not null
                ? $"Converted to {height}p at {FormatRate(kbps * 1000L)}: your connection measured {FormatRate(bandwidthBps!.Value)} and the original needs {FormatRate(source.BitrateBps!.Value)}."
                : avoidDirectPlay
                    ? $"Converted to H.264 {height}p because this device couldn't play the original."
                    : $"Converted to H.264 {height}p: this device can't play {(audio is not null && !audioOk ? CodecName(audio) + " sound" : ContainerName(source.Container) + " files")}.";
        var copySound = audio is not null && HlsAudio.Contains(audio) && client.AudioCodecs.Contains(audio) && audio == "aac";
        return new StreamPlan(StreamMode.Convert, reason, CopyAudio: copySound, TargetHeight: height, VideoBitrateKbps: kbps, Progressive: progressive);
    }

    /// <summary>The best ladder rung that fits the connection and is no taller than the source.</summary>
    public static (int Height, int Kbps) ChooseRung(int? sourceHeight, long? bandwidthBps)
    {
        var budgetKbps = bandwidthBps is > 0 ? bandwidthBps.Value * BandwidthHeadroom / 1000 - StreamPlan.AudioBitrateKbps : double.MaxValue;
        var rungs = Ladder.Where(r => sourceHeight is not { } h || r.Height <= h).ToList();
        if (rungs.Count == 0)
        {
            return (EvenHeight(sourceHeight!.Value), 600); // smaller than every rung: keep its size
        }

        foreach (var rung in rungs)
        {
            if (rung.Kbps <= budgetKbps)
            {
                return rung;
            }
        }

        return rungs[^1];
    }

    /// <summary>"H.264", "HEVC", "AAC"…</summary>
    public static string CodecName(string codec) => codec switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC (H.265)",
        "vp8" => "VP8",
        "vp9" => "VP9",
        "av1" => "AV1",
        "mpeg4" => "MPEG-4 Part 2",
        "mpeg2video" => "MPEG-2",
        "aac" => "AAC",
        "mp3" => "MP3",
        "ac3" => "Dolby Digital",
        "eac3" => "Dolby Digital Plus",
        "truehd" => "Dolby TrueHD",
        "dts" => "DTS",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "flac" => "FLAC",
        "alac" => "ALAC",
        _ when codec.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        _ => codec.ToUpperInvariant(),
    };

    /// <summary>"MP4", "MKV"…</summary>
    public static string ContainerName(string container) => container switch
    {
        "mp4" => "MP4",
        "matroska" => "MKV",
        "webm" => "WebM",
        "avi" => "AVI",
        "mpegts" => "MPEG-TS",
        _ => container.ToUpperInvariant(),
    };

    /// <summary>"stereo", "5.1"…</summary>
    public static string ChannelName(int? channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        { } n => $"{n} channels",
        null => string.Empty,
    };

    /// <summary>"8.2 Mbps", "900 kbps".</summary>
    public static string FormatRate(long bitsPerSecond) => bitsPerSecond >= 1_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1_000_000.0:0.#} Mbps")
        : string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1000} kbps");

    private static int EvenHeight(int height) => Math.Max(2, height & ~1);

    private static string Supported(SourceMedia source)
    {
        var parts = new List<string>();
        if (source.VideoCodec is { } v)
        {
            parts.Add(CodecName(v));
        }

        if (source.AudioCodec is { } a)
        {
            parts.Add(CodecName(a));
        }

        return $"{string.Join(" and ", parts)} in {ContainerName(source.Container)}";
    }

    private static StreamPlan Unavailable(SourceMedia source, ClientCapabilities client, string fix)
    {
        string what;
        if (source.VideoCodec is { } v && !client.VideoCodecs.Contains(v))
        {
            what = $"This device can't play {CodecName(v)} video.";
        }
        else if (source.AudioCodec is { } a && !client.AudioCodecs.Contains(a))
        {
            what = $"This device can't play {CodecName(a)} sound.";
        }
        else
        {
            what = $"This device can't play {ContainerName(source.Container)} files as they are.";
        }

        return new StreamPlan(StreamMode.Unavailable, $"{what} {fix}");
    }
}
