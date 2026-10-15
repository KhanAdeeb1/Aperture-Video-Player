using System.Globalization;
using Aperture.Core.Library;
using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv;

/// <summary>
/// Reads a file's length, picture size and codec for the library, using a separate headless
/// libmpv instance: nothing is shown or heard, nothing is decoded beyond what opening needs,
/// and the file is only read. One file at a time; each has a time limit so a damaged file
/// cannot stall a scan.
/// </summary>
public sealed class MpvMediaProbe : IMediaProbe, IDisposable
{
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(15);

    private readonly MpvClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<bool>? _pending;
    private bool _disposed;

    private MpvMediaProbe(MpvClient client)
    {
        _client = client;
        _client.MessageReceived += OnMessage;
    }

    /// <summary>Creates a probe. Throws <see cref="MpvUnavailableException"/> when libmpv cannot start.</summary>
    public static MpvMediaProbe Create(Action<string, string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var client = MpvClient.Create(
            [
                new("config", "no"),
                new("load-scripts", "no"),
                new("input-default-bindings", "no"),
                new("osc", "no"),
                new("terminal", "no"),
                new("ytdl", "no"),
                new("idle", "yes"),
                new("pause", "yes"),
                new("vo", "null"),
                new("ao", "null"),
                new("sid", "no"),
                new("hwdec", "no"),
                new("sub-auto", "no"),
                new("audio-file-auto", "no"),
                new("cache", "no"),
            ],
            "error",
            log);
        return new MpvMediaProbe(client);
    }

    public async Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pending, pending);
            if (await _client.CommandAsync("loadfile", path, "replace").ConfigureAwait(false) < 0)
            {
                return null;
            }

            var finished = await Task.WhenAny(pending.Task, Task.Delay(TimeLimit, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != pending.Task || !pending.Task.Result)
            {
                return null;
            }

            var albumArt = _client.GetString("current-tracks/video/albumart") == "yes";
            var width = albumArt ? null : Int(_client.GetString("current-tracks/video/demux-w"));
            var height = albumArt ? null : Int(_client.GetString("current-tracks/video/demux-h"));
            var codec = albumArt ? null : _client.GetString("current-tracks/video/codec");
            var duration = _client.GetDouble("duration") is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            return new MediaProbeResult(duration, width, height, codec);
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            if (!_disposed)
            {
                await _client.CommandAsync("stop").ConfigureAwait(false);
            }

            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.MessageReceived -= OnMessage;
        _client.Dispose();
        _gate.Dispose();
    }

    private void OnMessage(MpvMessage message)
    {
        switch (message.Id)
        {
            case MpvEventId.FileLoaded:
                Volatile.Read(ref _pending)?.TrySetResult(true);
                break;
            // A stopped earlier file is not news; only errors and empty files end a probe.
            case MpvEventId.EndFile when message.EndReason is MpvEndFileReason.Error or MpvEndFileReason.Eof:
                Volatile.Read(ref _pending)?.TrySetResult(false);
                break;
        }
    }

    private static int? Int(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
}
