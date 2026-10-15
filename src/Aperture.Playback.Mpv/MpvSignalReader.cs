using System.Globalization;
using System.Text.RegularExpressions;
using Aperture.Core.Series;
using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv;

/// <summary>
/// Reads what intro and credits detection needs, using a separate headless libmpv instance for
/// each request: nothing is shown or heard and the file is only read. Sound is decoded faster
/// than real time to a temporary file in Aperture's data folder (never next to the media) and
/// deleted afterwards; dark stretches come from FFmpeg's blackdetect filter on a reduced
/// picture; chapters are read from the container.
/// </summary>
public sealed partial class MpvSignalReader(string workDirectory, Action<string, string> log) : IMediaSignals
{
    /// <summary>Longest a single request may take, so a damaged file cannot stall analysis.</summary>
    public static TimeSpan TimeLimit { get; set; } = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan OpenLimit = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan LogQuiet = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan LogDrainLimit = TimeSpan.FromSeconds(3);

    public async Task<AudioSamples?> ReadAudioAsync(string path, TimeSpan start, TimeSpan? length, int sampleRate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);
        Directory.CreateDirectory(workDirectory);
        var output = Path.Combine(workDirectory, $"audio-{Guid.NewGuid():N}.pcm");
        try
        {
            List<KeyValuePair<string, string>> options =
            [
                .. Headless,
                new("vid", "no"),
                new("ao", "pcm"),
                new("ao-pcm-file", output),
                new("ao-pcm-waveheader", "no"),
                new("audio-format", "s16"),
                new("audio-channels", "mono"),
                new("audio-samplerate", sampleRate.ToString(CultureInfo.InvariantCulture)),
                new("replaygain", "no"),
                new("hr-seek", "yes"),
                new("start", Seconds(start)),
            ];
            if (length is { } span)
            {
                options.Add(new("length", Seconds(span)));
            }

            var hasAudio = await PlayThroughAsync(path, options, "current-tracks/audio/id", null, cancellationToken).ConfigureAwait(false);
            if (!hasAudio)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
            return samples.Length == 0 ? null : new AudioSamples(sampleRate, start, samples);
        }
        finally
        {
            TryDelete(output);
        }
    }

    public async Task<IReadOnlyList<TimeRange>?> FindDarkStretchesAsync(string path, TimeSpan start, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var found = new List<TimeRange>();
        var gate = new Lock();
        double? duration = null;
        void OnLog(MpvMessage message)
        {
            if (message.LogPrefix != "ffmpeg" || message.LogText is not { } text || BlackLine().Match(text) is not { Success: true } match)
            {
                return;
            }

            var from = double.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
            var to = double.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture);
            lock (gate)
            {
                found.Add(new TimeRange(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to)));
            }
        }

        List<KeyValuePair<string, string>> options =
        [
            .. Headless,
            new("aid", "no"),
            new("ao", "null"),
            new("vo", "null"),
            new("untimed", "yes"),
            new("hr-seek", "yes"),
            new("vd-lavc-skiploopfilter", "all"),
            new("vd-lavc-threads", "2"),
            // A small picture is enough to tell a dark frame, and much cheaper to test.
            new("vf", "lavfi=[scale=256:-2,blackdetect=d=1:pic_th=0.85:pix_th=0.12]"),
            new("start", Seconds(start)),
        ];
        var hasVideo = await PlayThroughAsync(path, options, "current-tracks/video/id", OnLog, cancellationToken, client => duration = client.GetDouble("duration")).ConfigureAwait(false);
        if (!hasVideo)
        {
            return null;
        }

        lock (gate)
        {
            // blackdetect reports a stretch that runs to the end at its last frame's start; reach the real end.
            if (duration is { } total && found.Count > 0 && total - found[^1].End.TotalSeconds is > 0 and < 0.5)
            {
                found[^1] = found[^1] with { End = TimeSpan.FromSeconds(total) };
            }

            return found.OrderBy(r => r.Start).ToList();
        }
    }

    public async Task<IReadOnlyList<ChapterMark>> ReadChaptersAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var chapters = new List<ChapterMark>();
        using var client = await Task.Run(() => MpvClient.Create([.. Headless, new("pause", "yes"), new("vo", "null"), new("ao", "null")], "error", log), cancellationToken).ConfigureAwait(false);
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += message =>
        {
            if (message.Id == MpvEventId.FileLoaded)
            {
                loaded.TrySetResult(true);
            }
            else if (message.Id == MpvEventId.EndFile && message.EndReason is MpvEndFileReason.Error or MpvEndFileReason.Eof)
            {
                loaded.TrySetResult(false);
            }
        };

        if (await client.CommandAsync("loadfile", path, "replace").ConfigureAwait(false) < 0)
        {
            throw new IOException($"libmpv could not open {Path.GetFileName(path)}.");
        }

        if (!await WaitAsync(loaded.Task, OpenLimit, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException($"{Path.GetFileName(path)} could not be opened to read its chapters.");
        }

        var count = (int)(client.GetDouble("chapter-list/count") ?? 0);
        for (var i = 0; i < count; i++)
        {
            // A first chapter can sit a few milliseconds before zero (the container's start offset).
            if (client.GetDouble($"chapter-list/{i}/time") is { } time && time > -1)
            {
                var title = client.GetString($"chapter-list/{i}/title");
                chapters.Add(new ChapterMark(string.IsNullOrWhiteSpace(title) ? null : title, TimeSpan.FromSeconds(Math.Max(0, time))));
            }
        }

        return chapters;
    }

    private static IEnumerable<KeyValuePair<string, string>> Headless =>
    [
        new("config", "no"),
        new("load-scripts", "no"),
        new("input-default-bindings", "no"),
        new("osc", "no"),
        new("terminal", "no"),
        new("ytdl", "no"),
        new("idle", "yes"),
        new("sid", "no"),
        new("hwdec", "no"),
        new("sub-auto", "no"),
        new("audio-file-auto", "no"),
        new("audio-display", "no"),
        new("cache", "no"),
        new("resume-playback", "no"),
        new("save-position-on-quit", "no"),
    ];

    /// <summary>
    /// Plays <paramref name="path"/> to its end with <paramref name="options"/>. Returns false when the
    /// file has no track named by <paramref name="trackProperty"/>; throws when it cannot be read.
    /// </summary>
    private async Task<bool> PlayThroughAsync(
        string path,
        List<KeyValuePair<string, string>> options,
        string trackProperty,
        Action<MpvMessage>? onLog,
        CancellationToken cancellationToken,
        Action<MpvClient>? onLoaded = null)
    {
        var level = onLog is null ? "error" : "v";
        var client = await Task.Run(() => MpvClient.Create(options, level, log), cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource<(MpvEndFileReason Reason, int Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastLog = Environment.TickCount64;
            client.MessageReceived += message =>
            {
                switch (message.Id)
                {
                    case MpvEventId.LogMessage:
                        Interlocked.Exchange(ref lastLog, Environment.TickCount64);
                        onLog?.Invoke(message);
                        if (message.LogLevel is "error" or "fatal")
                        {
                            log("warn", $"Analysing {Path.GetFileName(path)}: {message.LogPrefix}: {message.LogText}");
                        }

                        break;
                    case MpvEventId.FileLoaded:
                        loaded.TrySetResult(true);
                        break;
                    case MpvEventId.EndFile when message.EndReason is MpvEndFileReason.Error or MpvEndFileReason.Eof:
                        Interlocked.Exchange(ref lastLog, Environment.TickCount64);
                        loaded.TrySetResult(false);
                        ended.TrySetResult((message.EndReason, message.EndError));
                        break;
                }
            };

            if (await client.CommandAsync("loadfile", path, "replace").ConfigureAwait(false) < 0)
            {
                throw new IOException($"libmpv could not open {Path.GetFileName(path)}.");
            }

            var opened = await WaitAsync(loaded.Task, OpenLimit, cancellationToken).ConfigureAwait(false);
            if (!opened)
            {
                // Ended before loading: either unreadable, or nothing of the wanted kind to play.
                if (ended.Task.IsCompleted && (ended.Task.Result.Reason == MpvEndFileReason.Eof || ended.Task.Result.Error == MpvError.NothingToPlay))
                {
                    return false;
                }

                throw new IOException($"{Path.GetFileName(path)} could not be opened for analysis.");
            }

            if (string.IsNullOrEmpty(client.GetString(trackProperty)))
            {
                return false;
            }

            onLoaded?.Invoke(client);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeLimit);
            try
            {
                var (reason, _) = await ended.Task.WaitAsync(limit.Token).ConfigureAwait(false);
                if (reason == MpvEndFileReason.Error)
                {
                    throw new IOException($"{Path.GetFileName(path)} could not be read to the end.");
                }

                if (onLog is not null)
                {
                    // libmpv hands out events before log lines, so the end of the file can arrive
                    // ahead of the last lines a filter wrote (blackdetect reports a dark stretch
                    // that reaches the end only as the file ends). Read on until the log goes quiet.
                    var deadline = Environment.TickCount64 + (long)LogDrainLimit.TotalMilliseconds;
                    while (Environment.TickCount64 - Interlocked.Read(ref lastLog) < LogQuiet.TotalMilliseconds && Environment.TickCount64 < deadline)
                    {
                        await Task.Delay(LogQuiet / 4, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Analysing {Path.GetFileName(path)} took longer than {TimeLimit.TotalMinutes:0} minutes.");
            }

            return true;
        }
        finally
        {
            // Disposing ends playback and closes the sound file, so it is complete when read.
            await Task.Run(client.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitAsync(Task<bool> task, TimeSpan limit, CancellationToken cancellationToken)
    {
        var finished = await Task.WhenAny(task, Task.Delay(limit, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return finished == task && task.Result;
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log("warn", $"Could not delete the temporary analysis file {path}: {error.Message}");
        }
    }

    [GeneratedRegex(@"black_start:(?<start>[0-9.]+)\s+black_end:(?<end>[0-9.]+)")]
    private static partial Regex BlackLine();
}
