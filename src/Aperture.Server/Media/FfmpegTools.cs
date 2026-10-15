using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aperture.Core.Server;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Media;

/// <summary>The H.264 encoder conversions use, chosen by trying each on a tiny test picture.</summary>
/// <param name="Name">FFmpeg's encoder name ("h264_nvenc", "libx264").</param>
/// <param name="Label">"NVIDIA NVENC", "Intel Quick Sync", "AMD AMF", "processor (x264)".</param>
/// <param name="IsHardware">True for a graphics-hardware encoder.</param>
public sealed record H264Encoder(string Name, string Label, bool IsHardware);

/// <summary>FFmpeg and FFprobe found on this computer.</summary>
/// <param name="FfmpegPath">ffmpeg executable.</param>
/// <param name="FfprobePath">ffprobe executable.</param>
/// <param name="Version">"7.1.1", as FFmpeg reports it.</param>
/// <param name="Encoder">The encoder that worked, or null when none did.</param>
public sealed record FfmpegInstall(string FfmpegPath, string FfprobePath, string Version, H264Encoder? Encoder)
{
    /// <summary>"FFmpeg 7.1.1, NVIDIA NVENC".</summary>
    public string Describe() => Encoder is { } e ? $"FFmpeg {Version}, {e.Label}" : $"FFmpeg {Version}";
}

/// <summary>
/// Finds FFmpeg (the user's choice, then an <c>ffmpeg</c> folder next to the server, then PATH),
/// picks an H.264 encoder, and reads media details with FFprobe. FFmpeg is optional: without it
/// the server plays files directly and says what else would need it.
/// </summary>
public sealed class FfmpegTools(ILogger<FfmpegTools> log) : IDisposable
{
    private static readonly (string Name, string Label)[] HardwareEncoders =
    [
        ("h264_nvenc", "NVIDIA NVENC"),
        ("h264_qsv", "Intel Quick Sync"),
        ("h264_amf", "AMD AMF"),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string? Setting, FfmpegInstall? Install, string? Problem)? _cached;

    /// <summary>FFmpeg, or null with a sentence in <c>Problem</c> saying why not. Cached until the setting changes.</summary>
    public async Task<(FfmpegInstall? Install, string? Problem)> FindAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        if (_cached is { } c && c.Setting == configuredPath)
        {
            return (c.Install, c.Problem);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } again && again.Setting == configuredPath)
            {
                return (again.Install, again.Problem);
            }

            var (install, problem) = await LocateAsync(configuredPath, cancellationToken).ConfigureAwait(false);
            _cached = (configuredPath, install, problem);
            if (install is not null)
            {
                log.LogInformation("Conversion uses {Converter}", install.Describe());
            }
            else
            {
                log.LogInformation("Conversion is unavailable: {Problem}", problem);
            }

            return (install, problem);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Reads a file's container, first video and first audio stream with FFprobe. Null when FFprobe can't read it.</summary>
    public async Task<SourceMedia?> InspectAsync(FfmpegInstall install, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(install);
        var result = await RunAsync(
            install.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "file:" + path],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            log.LogWarning("FFprobe could not read an item (exit {Code})", result.ExitCode);
            return null;
        }

        return ParseProbe(result.Output, Path.GetExtension(path));
    }

    /// <summary>Turns FFprobe's JSON into <see cref="SourceMedia"/>.</summary>
    internal static SourceMedia? ParseProbe(string json, string extension)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var format))
        {
            return null;
        }

        var formatName = format.TryGetProperty("format_name", out var fn) ? fn.GetString() ?? string.Empty : string.Empty;
        string? video = null, audio = null;
        int? width = null, height = null, depth = null, channels = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                var attached = stream.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && video is null && !attached && codec is not null)
                {
                    video = codec;
                    width = Int(stream, "width");
                    height = Int(stream, "height");
                    depth = Int(stream, "bits_per_raw_sample")
                        ?? (stream.TryGetProperty("pix_fmt", out var pf) && pf.GetString() is { } pix && (pix.Contains("10", StringComparison.Ordinal) || pix.Contains("12", StringComparison.Ordinal)) ? 10 : 8);
                }
                else if (type == "audio" && audio is null && codec is not null)
                {
                    audio = codec;
                    channels = Int(stream, "channels");
                }
            }
        }

        var container = formatName switch
        {
            _ when formatName.Contains("mp4", StringComparison.Ordinal) || formatName.Contains("mov", StringComparison.Ordinal) => "mp4",
            _ when formatName.Contains("matroska", StringComparison.Ordinal) =>
                extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "webm" : "matroska",
            _ => formatName.Split(',')[0],
        };
        TimeSpan? duration = format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
        long? bitrate = format.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps) ? bps : null;
        return new SourceMedia(container, video, width, height, depth, bitrate, audio, channels, duration);
    }

    /// <summary>Runs a tool with arguments passed one by one (never through a shell).</summary>
    internal static async Task<(int ExitCode, string Output)> RunAsync(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (-2, string.Empty);
        }

        _ = await errors.ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false));
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number ? value.GetInt32()
            : int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
            : null;

    private static async Task<(FfmpegInstall?, string?)> LocateAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var probeExe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(Directory.Exists(configuredPath) ? Path.Combine(configuredPath, exe) : configuredPath);
            var bin = Path.Combine(configuredPath, "bin", exe);
            if (Directory.Exists(configuredPath))
            {
                candidates.Add(bin);
            }
        }
        else
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe));
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim('"'), exe));
            }
        }

        var ffmpeg = candidates.FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            return (null, configuredPath is null
                ? "FFmpeg isn't installed, so files the device can't play as they are can't be converted."
                : "FFmpeg wasn't found where you pointed. Choose ffmpeg.exe again in Aperture's settings, or let Aperture find it automatically.");
        }

        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, probeExe);
        if (!File.Exists(ffprobe))
        {
            return (null, "FFprobe, which comes with FFmpeg, is missing next to ffmpeg. Install the full FFmpeg package.");
        }

        var version = await RunAsync(ffmpeg, ["-hide_banner", "-version"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (version.ExitCode != 0)
        {
            return (null, "FFmpeg is there but didn't run.");
        }

        var firstLine = version.Output.Split('\n', 2)[0];
        var number = firstLine.StartsWith("ffmpeg version ", StringComparison.Ordinal) ? firstLine["ffmpeg version ".Length..].Split(' ')[0] : "(unknown version)";

        H264Encoder? encoder = null;
        foreach (var (name, label) in HardwareEncoders)
        {
            if (await TryEncoderAsync(ffmpeg, name, cancellationToken).ConfigureAwait(false))
            {
                encoder = new H264Encoder(name, label, true);
                break;
            }
        }

        if (encoder is null && await TryEncoderAsync(ffmpeg, "libx264", cancellationToken).ConfigureAwait(false))
        {
            encoder = new H264Encoder("libx264", "processor (x264)", false);
        }

        return encoder is null
            ? (new FfmpegInstall(ffmpeg, ffprobe, number, null), "This FFmpeg has no H.264 encoder, so it can repackage files but not convert them.")
            : (new FfmpegInstall(ffmpeg, ffprobe, number, encoder), null);
    }

    private static async Task<bool> TryEncoderAsync(string ffmpeg, string encoder, CancellationToken cancellationToken)
    {
        // A real one-frame encode: listing encoders isn't enough, because hardware encoders are
        // compiled in whether or not this computer has the hardware.
        var result = await RunAsync(
            ffmpeg,
            ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=320x180:d=0.2", "-frames:v", "2", "-c:v", encoder, "-f", "null", "-"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }
}
