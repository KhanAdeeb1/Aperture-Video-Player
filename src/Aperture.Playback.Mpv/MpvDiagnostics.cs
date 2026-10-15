using System.Globalization;
using Aperture.Core.Playback;

namespace Aperture.Playback.Mpv;

/// <summary>
/// Builds the playback information panel from libmpv properties. Takes a property reader so
/// the formatting can be tested without a running player. Values libmpv does not report are
/// left out rather than shown as blanks or guesses.
/// </summary>
internal static class MpvDiagnostics
{
    public static PlaybackDiagnostics Build(Func<string, string?> read, string? renderer)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read("path") is null)
        {
            return PlaybackDiagnostics.Empty;
        }

        var sections = new List<DiagnosticSection>(5);

        var file = new List<DiagnosticItem>();
        Add(file, "Name", read("filename"));
        Add(file, "Container", read("file-format"));
        Add(file, "Size", Bytes(read("file-size")));
        Add(file, "Length", Seconds(read("duration"), TimeFormat.Clock));
        AddSection(sections, "File", file);

        var video = new List<DiagnosticItem>();
        if (read("current-tracks/video/image") != "yes")
        {
            Add(video, "Codec", Codec(read("video-format"), read("video-codec")));
            Add(video, "Resolution", Size(read("video-params/w"), read("video-params/h")));
            Add(video, "Shown at", Size(read("video-out-params/dw"), read("video-out-params/dh")));
            Add(video, "Frame rate", FrameRate(read("container-fps"), read("estimated-vf-fps")));
            Add(video, "Pixel format", read("video-params/hw-pixelformat") is { } hw
                ? $"{read("video-params/pixelformat")} ({hw})"
                : read("video-params/pixelformat"));
            Add(video, "Colour", Join(read("video-params/colormatrix"), read("video-params/primaries"), read("video-params/gamma")));
            Add(video, "Decoding", Hardware(read("hwdec-current")));
            Add(video, "Bitrate", Bitrate(read("video-bitrate")));
            Add(video, "Dropped frames", Dropped(read("frame-drop-count"), read("decoder-frame-drop-count")));
        }

        AddSection(sections, "Video", video);

        var audio = new List<DiagnosticItem>();
        Add(audio, "Codec", Codec(read("audio-codec-name"), read("audio-codec")));
        Add(audio, "Channels", Channels(read("audio-params/channel-count"), read("audio-params/hr-channels")));
        Add(audio, "Sample rate", SampleRate(read("audio-params/samplerate")));
        Add(audio, "Bitrate", Bitrate(read("audio-bitrate")));
        Add(audio, "Output", read("current-ao"));
        AddSection(sections, "Audio", audio);

        var subtitles = new List<DiagnosticItem>();
        if (read("current-tracks/sub/codec") is { } subCodec)
        {
            Add(subtitles, "Format", TrackLabels.CodecName(subCodec));
            Add(subtitles, "Source", read("current-tracks/sub/external") == "yes" ? "External file" : "In the video file");
        }

        AddSection(sections, "Subtitles", subtitles);

        var playback = new List<DiagnosticItem>();
        Add(playback, "Speed", Number(read("speed")) is { } speed ? PlaybackSpeed.Label(speed) : null);
        Add(playback, "A/V sync", Number(read("avsync")) is { } sync ? Invariant($"{sync * 1000:+0;-0;0} ms") : null);
        Add(playback, "Sound offset", Number(read("audio-delay")) is { } audioDelay && Math.Abs(audioDelay) >= 0.001 ? SyncOffset.Label(TimeSpan.FromSeconds(audioDelay)) : null);
        Add(playback, "Subtitle offset", Number(read("sub-delay")) is { } subDelay && Math.Abs(subDelay) >= 0.001 ? SyncOffset.Label(TimeSpan.FromSeconds(subDelay)) : null);
        Add(playback, "Buffered ahead", Seconds(read("demuxer-cache-duration"), t => Invariant($"{t.TotalSeconds:0.0} s")));
        Add(playback, "Renderer", renderer);
        Add(playback, "Engine", read("mpv-version"));
        AddSection(sections, "Playback", playback);

        return new PlaybackDiagnostics(sections);
    }

    private static void Add(List<DiagnosticItem> items, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            items.Add(new DiagnosticItem(label, value));
        }
    }

    private static void AddSection(List<DiagnosticSection> sections, string title, List<DiagnosticItem> items)
    {
        if (items.Count > 0)
        {
            sections.Add(new DiagnosticSection(title, items));
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }

    // FFmpeg's long names ("H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10") only help for codecs
    // Aperture has no short name for.
    private static string? Codec(string? shortName, string? description) =>
        TrackLabels.KnownCodecName(shortName) ?? description ?? TrackLabels.CodecName(shortName);

    private static string? Size(string? width, string? height) =>
        Number(width) is { } w && Number(height) is { } h && w > 0 && h > 0 ? Invariant($"{w:0}×{h:0}") : null;

    private static string? FrameRate(string? container, string? measured)
    {
        var declared = Number(container);
        var actual = Number(measured);
        return (declared, actual) switch
        {
            (null, null) => null,
            ({ } d, null) => Invariant($"{d:0.###} fps"),
            (null, { } a) => Invariant($"{a:0.###} fps measured"),
            ({ } d, { } a) when Math.Abs(d - a) < 0.05 => Invariant($"{d:0.###} fps"),
            ({ } d, { } a) => Invariant($"{d:0.###} fps (measured {a:0.##})"),
        };
    }

    private static string? Hardware(string? hwdec) => hwdec switch
    {
        null or "" => null,
        "no" => "Software",
        var name => $"Hardware ({name})",
    };

    private static string? Bitrate(string? bitsPerSecond) => Number(bitsPerSecond) switch
    {
        null or <= 0 => null,
        >= 1_000_000 and var b => Invariant($"{b / 1_000_000:0.0} Mbit/s"),
        var b => Invariant($"{b / 1000:0} kbit/s"),
    };

    private static string? Bytes(string? bytes) => Number(bytes) switch
    {
        null or < 0 => null,
        >= 1L << 30 and var b => Invariant($"{b / (1L << 30):0.00} GB"),
        >= 1L << 20 and var b => Invariant($"{b / (1L << 20):0.0} MB"),
        var b => Invariant($"{b / 1024:0} KB"),
    };

    private static string? Seconds(string? seconds, Func<TimeSpan, string> format) =>
        Number(seconds) is { } s && s >= 0 ? format(TimeSpan.FromSeconds(s)) : null;

    private static string? Channels(string? count, string? layout)
    {
        if (Number(count) is not { } channels)
        {
            return layout;
        }

        var name = TrackLabels.ChannelLayout((int)channels);
        return layout is null || name.Equals(layout, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({layout})";
    }

    private static string? SampleRate(string? hertz) =>
        Number(hertz) is { } hz && hz > 0 ? Invariant($"{hz / 1000:0.#} kHz") : null;

    private static string? Dropped(string? output, string? decoder)
    {
        var o = Number(output);
        var d = Number(decoder);
        if (o is null && d is null)
        {
            return null;
        }

        return Invariant($"{(o ?? 0) + (d ?? 0):0}");
    }
}
