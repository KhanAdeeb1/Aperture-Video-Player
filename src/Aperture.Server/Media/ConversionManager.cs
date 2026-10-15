using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aperture.Core.Server;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Media;

/// <summary>One file being repackaged or converted to HLS for one device.</summary>
public sealed class ConversionSession
{
    internal ConversionSession(string id, long deviceId, long itemId, double startSeconds, StreamPlan plan, string directory, string encoderLabel)
    {
        Id = id;
        DeviceId = deviceId;
        ItemId = itemId;
        StartSeconds = startSeconds;
        Plan = plan;
        Directory = directory;
        EncoderLabel = encoderLabel;
    }

    /// <summary>Unguessable id; also the capability in the stream's address.</summary>
    public string Id { get; }

    public long DeviceId { get; }

    public long ItemId { get; }

    /// <summary>Where in the file the stream starts.</summary>
    public double StartSeconds { get; }

    public StreamPlan Plan { get; }

    public string Directory { get; }

    /// <summary>"copy" for remuxing, or the encoder's label.</summary>
    public string EncoderLabel { get; }

    public DateTimeOffset LastAccessUtc { get; internal set; }

    /// <summary>How much faster than real time FFmpeg is working (2.0 = twice).</summary>
    public double Speed { get; internal set; }

    /// <summary>Seconds of the stream produced so far.</summary>
    public double ProducedSeconds { get; internal set; }

    public bool Finished { get; internal set; }

    /// <summary>Why FFmpeg stopped early, or null.</summary>
    public string? Error { get; internal set; }

    internal Process? Process { get; set; }

    public string PlaylistPath => Path.Combine(Directory, "index.m3u8");
}

/// <summary>
/// Runs FFmpeg to turn files into HLS streams in the server's Conversions folder, one at a time
/// per device and at most <see cref="ServerSettings.MaxConversions"/> overall. A stream nobody
/// has asked for in a while is stopped and its files deleted. Original files are only read.
/// </summary>
public sealed partial class ConversionManager : IAsyncDisposable
{
    /// <summary>Segment length in seconds.</summary>
    public const int SegmentSeconds = 4;

    private static readonly TimeSpan IdleWhileRunning = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleWhenFinished = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, ConversionSession> _sessions = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConversionManager> _log;
    private readonly ITimer _reaper;

    public ConversionManager(string root, TimeProvider clock, ILogger<ConversionManager> log)
    {
        _root = root;
        _clock = clock;
        _log = log;
        DeleteLeftovers();
        _reaper = clock.CreateTimer(_ => Reap(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private int _progressive;

    /// <summary>Conversions whose FFmpeg is still working (HLS and continuous MP4).</summary>
    public int RunningCount => _sessions.Values.Count(s => !s.Finished && s.Error is null) + Volatile.Read(ref _progressive);

    public IReadOnlyCollection<ConversionSession> Sessions => [.. _sessions.Values];

    /// <summary>
    /// Starts a stream, replacing this device's previous one. Null when <paramref name="limit"/>
    /// streams for other devices are already converting.
    /// </summary>
    public async Task<ConversionSession?> StartAsync(FfmpegInstall install, long deviceId, long itemId, string inputPath, double startSeconds, StreamPlan plan, int limit)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var old in _sessions.Values.Where(s => s.DeviceId == deviceId).ToList())
        {
            await StopAsync(old.Id).ConfigureAwait(false);
        }

        var working = _sessions.Values.Count(s => !s.Finished && s.Error is null && !s.Plan.CopyVideo) + Volatile.Read(ref _progressive);
        if (!plan.CopyVideo && working >= limit)
        {
            return null; // repackaging is cheap and doesn't count against the limit
        }

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var directory = Path.Combine(_root, id);
        System.IO.Directory.CreateDirectory(directory);
        var encoder = plan.CopyVideo || plan.AudioOnly ? null : install.Encoder;
        var session = new ConversionSession(id, deviceId, itemId, startSeconds, plan, directory, plan.AudioOnly ? "AAC" : encoder?.Label ?? "copy") { LastAccessUtc = _clock.GetUtcNow() };

        var start = new ProcessStartInfo(install.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildArguments(inputPath, directory, plan, startSeconds, encoder))
        {
            start.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var errors = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => ReadProgress(session, e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line && errors.Length < 2000)
            {
                errors.AppendLine(line);
            }
        };
        process.Exited += (_, _) =>
        {
            int code;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (code == 0)
            {
                session.Finished = true;
            }
            else if (session.Error is null && _sessions.ContainsKey(session.Id))
            {
                session.Error = "FFmpeg stopped while converting this file.";
                // FFmpeg's own message can include the file's path; keep it out of the log.
                _log.LogWarning("Conversion of item {Item} failed (exit {Code}): {Message}", session.ItemId, code, Redact(errors.ToString(), inputPath));
            }
        };

        session.Process = process;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _sessions[id] = session;
        _log.LogInformation(
            "Started {Mode} of item {Item} for device {Device} from {Start:0}s with {Encoder}",
            plan.Mode, itemId, deviceId, startSeconds, session.EncoderLabel);
        return session;
    }

    /// <summary>
    /// Runs FFmpeg and copies one continuous fragmented MP4 to <paramref name="output"/> until it
    /// ends or the device goes away (which cancels <paramref name="cancellationToken"/> and stops
    /// FFmpeg). Returns false without starting when the conversion limit is reached.
    /// </summary>
    public async Task<bool> StreamProgressiveAsync(
        FfmpegInstall install, long itemId, string inputPath, double startSeconds, StreamPlan plan, int limit,
        Func<Stream> openOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(openOutput);
        if (!plan.CopyVideo && RunningCount >= limit)
        {
            return false;
        }

        Interlocked.Increment(ref _progressive);
        var encoder = plan.CopyVideo || plan.AudioOnly ? null : install.Encoder;
        var start = new ProcessStartInfo(install.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildArguments(inputPath, null, plan, startSeconds, encoder))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _log.LogInformation("Started a continuous {Mode} of item {Item} from {Start:0}s", plan.Mode, itemId, startSeconds);
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(openOutput(), 64 * 1024, cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    _log.LogWarning("Continuous conversion of item {Item} failed (exit {Code}): {Message}", itemId, process.ExitCode, Redact(await errors.ConfigureAwait(false), inputPath));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The device stopped watching or moved elsewhere in the file.
            }

            return true;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Never started or already gone.
            }

            Interlocked.Decrement(ref _progressive);
        }
    }

    /// <summary>The session, marked as used, or null.</summary>
    public ConversionSession? Touch(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        session.LastAccessUtc = _clock.GetUtcNow();
        return session;
    }

    /// <summary>Waits until the playlist has its first segment, FFmpeg fails, or the time runs out.</summary>
    public async Task<string?> ReadPlaylistAsync(ConversionSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (_clock.GetUtcNow() < deadline && session.Error is null)
        {
            if (File.Exists(session.PlaylistPath))
            {
                var text = await File.ReadAllTextAsync(session.PlaylistPath, cancellationToken).ConfigureAwait(false);
                if (text.Contains("#EXTINF", StringComparison.Ordinal))
                {
                    return text;
                }
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>The path of a finished segment of the session, or null for any other name.</summary>
    public static string? SegmentPath(ConversionSession session, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!SegmentName().IsMatch(name))
        {
            return null;
        }

        var path = Path.Combine(session.Directory, name);
        return File.Exists(path) ? path : null;
    }

    public async Task StopAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return;
        }

        await KillAsync(session).ConfigureAwait(false);
        TryDelete(session.Directory);
    }

    public async ValueTask DisposeAsync()
    {
        await _reaper.DisposeAsync().ConfigureAwait(false);
        foreach (var id in _sessions.Keys.ToList())
        {
            await StopAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>FFmpeg's arguments for a plan. Every value is a separate argument; nothing passes through a shell.</summary>
    /// <param name="directory">The HLS folder, or null to write one fragmented MP4 to standard output.</param>
    internal static IReadOnlyList<string> BuildArguments(string input, string? directory, StreamPlan plan, double startSeconds, H264Encoder? encoder)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats" };
        if (directory is not null)
        {
            args.AddRange(["-progress", "pipe:1"]); // standard output is free for progress with HLS
        }

        if (startSeconds > 0)
        {
            args.AddRange(["-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-i", "file:" + input]);
        if (!plan.AudioOnly)
        {
            args.AddRange(["-map", "0:v:0"]);
        }

        args.AddRange(["-map", "0:a:0?", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);

        if (plan.AudioOnly)
        {
            // No picture to carry.
        }
        else if (plan.CopyVideo)
        {
            args.AddRange(["-c:v", "copy"]);
        }
        else
        {
            var name = encoder?.Name ?? "libx264";
            var kbps = plan.VideoBitrateKbps ?? 4000;
            var height = plan.TargetHeight ?? 720;
            var pixel = name == "h264_qsv" ? "nv12" : "yuv420p";
            args.AddRange(["-vf", $"scale=-2:'min({height},ih)':flags=bicubic,format={pixel}", "-c:v", name]);
            args.AddRange(name switch
            {
                "libx264" => ["-preset", "veryfast", "-profile:v", "high", "-level:v", "4.1"],
                "h264_nvenc" => ["-preset", "p4", "-profile:v", "high"],
                "h264_qsv" => ["-preset", "veryfast", "-profile:v", "high"],
                "h264_amf" => ["-quality", "speed", "-profile:v", "high"],
                _ => [],
            });
            args.AddRange([
                "-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k",
                "-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})",
            ]);
        }

        if (plan.CopyAudio)
        {
            args.AddRange(["-c:a", "copy"]);
        }
        else
        {
            args.AddRange(["-c:a", "aac", "-b:a", $"{StreamPlan.AudioBitrateKbps}k", "-ac", "2"]);
        }

        if (directory is null)
        {
            // Fragments that start on keyframes and need no index at the end, so playback starts at once.
            args.AddRange(["-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "2000000", "pipe:1"]);
            return args;
        }

        args.AddRange([
            "-f", "hls",
            "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_flags", "independent_segments+temp_file",
            "-hls_segment_type", "mpegts",
            "-hls_segment_filename", Path.Combine(directory, "seg%05d.ts"),
            Path.Combine(directory, "index.m3u8"),
        ]);
        return args;
    }

    [GeneratedRegex("^seg[0-9]{5}\\.ts$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentName();

    private static void ReadProgress(ConversionSession session, string? line)
    {
        if (line is null)
        {
            return;
        }

        var cut = line.IndexOf('=', StringComparison.Ordinal);
        if (cut <= 0)
        {
            return;
        }

        var key = line[..cut];
        var value = line[(cut + 1)..].Trim();
        if (key == "speed" && double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            session.Speed = speed;
        }
        else if (key == "out_time_us" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) && micros > 0)
        {
            session.ProducedSeconds = micros / 1_000_000.0;
        }
    }

    private static string Redact(string text, string path) =>
        text.Replace(path, "<file>", StringComparison.OrdinalIgnoreCase).Trim();

    private static async Task KillAsync(ConversionSession session)
    {
        if (session.Process is not { } process)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or refused to stop in time; the folder is deleted either way.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void Reap()
    {
        var now = _clock.GetUtcNow();
        foreach (var session in _sessions.Values)
        {
            var idle = now - session.LastAccessUtc;
            if (idle > (session.Finished || session.Error is not null ? IdleWhenFinished : IdleWhileRunning))
            {
                _log.LogInformation("Stopped the unused stream of item {Item}", session.ItemId);
                _ = StopAsync(session.Id);
            }
        }
    }

    private void DeleteLeftovers()
    {
        if (!System.IO.Directory.Exists(_root))
        {
            System.IO.Directory.CreateDirectory(_root);
            return;
        }

        foreach (var dir in System.IO.Directory.EnumerateDirectories(_root))
        {
            TryDelete(dir);
        }
    }

    private void TryDelete(string directory)
    {
        // Only folders inside the server's own Conversions folder are ever deleted.
        if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(200); // FFmpeg may still be letting go of a file on Windows
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Couldn't delete a finished stream's folder: {Message}", ex.Message);
                return;
            }
        }
    }
}
