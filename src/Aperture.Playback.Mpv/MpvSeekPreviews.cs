using System.Globalization;
using System.Runtime.InteropServices;
using Aperture.Core.Playback;
using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv;

/// <summary>A small picture of a video at one moment, as 32-bit BGRA pixels (opaque, rows packed).</summary>
public sealed record PreviewFrame(TimeSpan Time, int Width, int Height, byte[] Pixels);

/// <summary>
/// Pictures for the timeline's seek preview, made by a second, hidden libmpv instance that
/// only reads the file: no sound, no subtitles, software decoding on two threads, and keyframe
/// seeks, so each picture costs one decoded frame. Pictures are kept in memory only (never
/// written to disk) and the newest request wins, so moving the pointer quickly doesn't queue
/// work. Everything native runs on one dedicated thread, which libmpv's render API requires.
/// </summary>
public sealed class MpvSeekPreviews : IDisposable
{
    private const int CacheSize = 120;
    private static readonly TimeSpan OpenLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SeekLimit = TimeSpan.FromSeconds(3);

    private readonly string _path;
    private readonly int _boxWidth;
    private readonly int _boxHeight;
    private readonly Action<string, string> _log;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AutoResetEvent _work = new(false);
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly ManualResetEventSlim _restarted = new(false);
    private readonly object _gate = new();
    private readonly Dictionary<long, LinkedListNode<PreviewFrame>> _cache = [];
    private readonly LinkedList<PreviewFrame> _recent = new();
    private (TimeSpan Time, TaskCompletionSource<PreviewFrame?> Result)? _request;
    private volatile bool _stopping;
    private MpvClient? _client;
    private MpvRenderContext? _render;
    private int _width;
    private int _height;
    private bool _disposed;

    private MpvSeekPreviews(string path, int boxWidth, int boxHeight, Action<string, string> log)
    {
        _path = path;
        _boxWidth = boxWidth;
        _boxHeight = boxHeight;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Seek previews", Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>Picture size in pixels, known once opened.</summary>
    public (int Width, int Height) Size => (_width, _height);

    /// <summary>
    /// Opens <paramref name="path"/> for previews no larger than the box (in pixels). Returns
    /// null when the file has no moving picture or libmpv can't read it; never throws for that.
    /// </summary>
    public static async Task<MpvSeekPreviews?> OpenAsync(string path, int boxWidth, int boxHeight, Action<string, string> log, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        var previews = new MpvSeekPreviews(path, Math.Max(16, boxWidth), Math.Max(16, boxHeight), log);
        previews._thread.Start();
        bool ok;
        try
        {
            ok = await previews._opened.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            previews.Dispose();
            throw;
        }

        if (!ok)
        {
            previews.Dispose();
            return null;
        }

        return previews;
    }

    /// <summary>
    /// The picture at <paramref name="time"/>, or null when it couldn't be made or a newer
    /// request replaced this one before it started.
    /// </summary>
    public Task<PreviewFrame?> GetAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = (long)Math.Round(time.TotalMilliseconds);
        TaskCompletionSource<PreviewFrame?>? replaced = null;
        var result = new TaskCompletionSource<PreviewFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return Task.FromResult<PreviewFrame?>(node.Value);
            }

            replaced = _request?.Result;
            _request = (time, result);
        }

        replaced?.TrySetResult(null);
        _work.Set();
        return result.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _work.Set();
        _frameReady.Set();
        _restarted.Set();
        if (Thread.CurrentThread != _thread && _thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log("error", "The seek preview thread did not stop within 5 seconds.");
            return;
        }

        _work.Dispose();
        _frameReady.Dispose();
        _restarted.Dispose();
    }

    private void Run()
    {
        try
        {
            if (!Open())
            {
                _opened.TrySetResult(false);
                return;
            }

            _opened.TrySetResult(true);
            while (!_stopping)
            {
                _work.WaitOne();
                (TimeSpan Time, TaskCompletionSource<PreviewFrame?> Result)? request;
                lock (_gate)
                {
                    request = _request;
                    _request = null;
                }

                if (request is { } next && !_stopping)
                {
                    next.Result.TrySetResult(Make(next.Time));
                }
            }
        }
        catch (Exception error) when (error is MpvUnavailableException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            _log("warn", $"Seek previews stopped: {error.Message}");
            _opened.TrySetResult(false);
        }
        finally
        {
            lock (_gate)
            {
                _request?.Result.TrySetResult(null);
                _request = null;
            }

            // The render context belongs to this thread and must go before the instance.
            _render?.Dispose();
            _client?.Dispose();
        }
    }

    private bool Open()
    {
        _client = MpvClient.Create(
        [
            new("config", "no"),
            new("load-scripts", "no"),
            new("input-default-bindings", "no"),
            new("osc", "no"),
            new("osd-level", "0"),
            new("terminal", "no"),
            new("ytdl", "no"),
            new("idle", "yes"),
            new("vo", "libmpv"),
            new("ao", "null"),
            new("aid", "no"),
            new("sid", "no"),
            new("sub-auto", "no"),
            new("audio-file-auto", "no"),
            new("audio-display", "no"),
            new("hwdec", "no"),
            new("pause", "yes"),
            new("keep-open", "always"),
            new("hr-seek", "no"),
            new("cache", "no"),
            new("vd-lavc-threads", "2"),
            new("vd-lavc-skiploopfilter", "all"),
            new("sws-scaler", "bilinear"),
            new("zimg-scaler", "bilinear"),
            new("resume-playback", "no"),
        ], "error", _log);
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.MessageReceived += message =>
        {
            switch (message.Id)
            {
                case MpvEventId.FileLoaded:
                    loaded.TrySetResult(true);
                    break;
                case MpvEventId.EndFile when message.EndReason is MpvEndFileReason.Error or MpvEndFileReason.Eof:
                    loaded.TrySetResult(false);
                    break;
                case MpvEventId.PlaybackRestart:
                    _restarted.Set();
                    break;
            }
        };

        _render = MpvRenderContext.CreateSoftware(_client, out var error);
        if (_render is null)
        {
            _log("warn", $"Seek previews are unavailable: {error}");
            return false;
        }

        _render.SetUpdateCallback(() => _frameReady.Set());
        _restarted.Reset();
        if (_client.CommandAsync("loadfile", _path, "replace").GetAwaiter().GetResult() < 0
            || !loaded.Task.Wait(OpenLimit) || !loaded.Task.Result)
        {
            return false;
        }

        // Only moving pictures get previews: not sound files, nor cover art in a music file.
        if (string.IsNullOrEmpty(_client.GetString("current-tracks/video/id"))
            || _client.GetString("current-tracks/video/image") == "yes"
            || _client.GetString("current-tracks/video/albumart") == "yes")
        {
            return false;
        }

        // The first frame tells the picture's shape (including anamorphic video). Frames offered
        // meanwhile are drawn to a scratch picture, since libmpv's video output waits for that.
        var scratch = new byte[16 * 4 * 16];
        (_width, _height) = (16, 16);
        var deadline = DateTime.UtcNow + OpenLimit;
        while (!_restarted.IsSet && !_stopping && DateTime.UtcNow < deadline)
        {
            if (_render.HasNewFrame())
            {
                Draw(scratch);
            }

            _frameReady.WaitOne(20);
        }
        var aspect = _client.GetDouble("video-params/aspect")
            ?? (_client.GetDouble("width") is { } w && _client.GetDouble("height") is { } h && h > 0 ? w / h : 16.0 / 9);
        (_width, _height) = SeekPreview.Size(aspect, _boxWidth, _boxHeight);
        return true;
    }

    private PreviewFrame? Make(TimeSpan time)
    {
        if (_client is null || _render is null)
        {
            return null;
        }

        _restarted.Reset();
        var seconds = Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        if (_client.CommandAsync("seek", seconds, "absolute+keyframes").GetAwaiter().GetResult() < 0)
        {
            return null;
        }

        // Frames are drawn as soon as libmpv offers them (its video output waits for that), and
        // the picture is complete once the seek has finished and the frame after it was drawn.
        var pixels = new byte[_width * 4 * _height];
        var drawn = false;
        DateTime? restartedAt = null;
        var deadline = DateTime.UtcNow + SeekLimit;
        while (!_stopping && DateTime.UtcNow < deadline)
        {
            if (restartedAt is null && _restarted.IsSet)
            {
                restartedAt = DateTime.UtcNow;
                drawn = false;
            }

            if (_render.HasNewFrame())
            {
                if (!Draw(pixels))
                {
                    return null;
                }

                drawn = true;
            }

            if (restartedAt is { } at && (drawn || DateTime.UtcNow - at > TimeSpan.FromMilliseconds(300)))
            {
                if (!drawn && !Draw(pixels))
                {
                    return null;
                }

                var frame = new PreviewFrame(time, _width, _height, pixels);
                Remember(frame);
                return frame;
            }

            _frameReady.WaitOne(20);
        }

        return null;
    }

    /// <summary>Draws the current frame into <paramref name="pixels"/> (packed BGRA, opaque).</summary>
    private bool Draw(byte[] pixels)
    {
        var stride = _width * 4;
        var alignedStride = (stride + 63) & ~63;
        var buffer = Marshal.AllocHGlobal(alignedStride * _height);
        try
        {
            var result = _render!.RenderSoftware(_width, _height, (nuint)alignedStride, buffer);
            if (result < 0)
            {
                _log("warn", $"A seek preview could not be drawn: {MpvRenderContext.Describe(result)}");
                return false;
            }

            for (var row = 0; row < _height; row++)
            {
                Marshal.Copy(buffer + (row * alignedStride), pixels, row * stride, stride);
            }

            // libmpv leaves the fourth byte undefined ("bgr0"); the picture is opaque.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Remember(PreviewFrame frame)
    {
        var key = (long)Math.Round(frame.Time.TotalMilliseconds);
        lock (_gate)
        {
            if (_cache.ContainsKey(key))
            {
                return;
            }

            _cache[key] = _recent.AddFirst(frame);
            while (_recent.Count > CacheSize)
            {
                var oldest = _recent.Last!;
                _recent.RemoveLast();
                _cache.Remove((long)Math.Round(oldest.Value.Time.TotalMilliseconds));
            }
        }
    }
}
