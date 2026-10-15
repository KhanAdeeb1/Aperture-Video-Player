using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv;

/// <summary>
/// One libmpv instance: creation and options, a dedicated event thread, asynchronous
/// commands and property writes, property observation, and orderly shutdown. Knows
/// nothing about players or files; <see cref="MpvPlaybackEngine"/> builds on it.
/// </summary>
internal sealed unsafe class MpvClient : IDisposable
{
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<int>> _pending = new();
    private readonly Thread _eventThread;
    private readonly Action<string, string> _log;
    private long _nextReply = 1L << 40;
    private volatile bool _stopping;
    private IntPtr _handle;

    private MpvClient(IntPtr handle, Action<string, string> log)
    {
        _handle = handle;
        _log = log;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "libmpv events" };
    }

    /// <summary>Raised on the event thread for every event that is not a reply to a request.</summary>
    public event Action<MpvMessage>? MessageReceived;

    /// <summary>The native handle, for the render API. Valid until <see cref="Dispose"/>.</summary>
    public IntPtr Handle => _handle;

    /// <summary>
    /// Creates and initialises libmpv with <paramref name="options"/> (set before
    /// initialisation, in order). An option this libmpv build does not know is logged and
    /// skipped rather than failing playback. <paramref name="log"/> receives (level, text).
    /// </summary>
    public static MpvClient Create(IEnumerable<KeyValuePair<string, string>> options, string logLevel, Action<string, string> log)
    {
        MpvLibrary.EnsureLoaded();
        var handle = MpvNative.Create();
        if (handle == IntPtr.Zero)
        {
            throw new MpvUnavailableException("libmpv could not create a player instance.");
        }

        foreach (var (name, value) in options)
        {
            var result = MpvNative.SetOptionString(handle, name, value);
            if (result < 0)
            {
                log("warn", $"libmpv option {name}={value} was not applied: {MpvNative.Describe(result)}");
            }
        }

        var init = MpvNative.Initialize(handle);
        if (init < 0)
        {
            MpvNative.TerminateDestroy(handle);
            throw new MpvUnavailableException($"libmpv could not start: {MpvNative.Describe(init)}");
        }

        MpvNative.RequestLogMessages(handle, logLevel);
        var client = new MpvClient(handle, log);
        client._eventThread.Start();
        return client;
    }

    /// <summary>Asks libmpv to report changes of a property as <see cref="MpvEventId.PropertyChange"/> events.</summary>
    public void Observe(ulong id, string name, MpvFormat format)
    {
        var result = MpvNative.ObserveProperty(_handle, id, name, format);
        if (result < 0)
        {
            _log("warn", $"libmpv cannot observe {name}: {MpvNative.Describe(result)}");
        }
    }

    /// <summary>Runs a command without waiting. Failures are logged.</summary>
    public void Command(params string[] args) => _ = CommandAsync(args);

    /// <summary>Runs a command; the task completes with libmpv's result code (0 or more is success).</summary>
    public Task<int> CommandAsync(params string[] args)
    {
        if (_stopping)
        {
            return Task.FromResult(MpvError.Generic);
        }

        var reply = (ulong)Interlocked.Increment(ref _nextReply);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reply] = completion;

        var native = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                native[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }

            int result;
            fixed (IntPtr* pointer = native)
            {
                result = MpvNative.CommandAsync(_handle, reply, pointer);
            }

            if (result < 0)
            {
                _pending.TryRemove(reply, out _);
                _log("warn", $"libmpv refused command {args[0]}: {MpvNative.Describe(result)}");
                completion.TrySetResult(result);
            }
        }
        finally
        {
            foreach (var pointer in native)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }

        return completion.Task.ContinueWith(
            task =>
            {
                if (task.Result < 0)
                {
                    _log("warn", $"libmpv command {args[0]} failed: {MpvNative.Describe(task.Result)}");
                }

                return task.Result;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Sets a property from its string form without waiting. Failures are logged.</summary>
    public void SetProperty(string name, string value)
    {
        if (_stopping)
        {
            return;
        }

        var text = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            var reply = Track(name);
            var result = MpvNative.SetPropertyAsync(_handle, reply, name, MpvFormat.String, &text);
            Untrack(reply, result, name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>Sets a numeric property without waiting.</summary>
    public void SetProperty(string name, double value)
    {
        if (!_stopping)
        {
            var reply = Track(name);
            Untrack(reply, MpvNative.SetPropertyAsync(_handle, reply, name, MpvFormat.Double, &value), name);
        }
    }

    /// <summary>Sets a yes/no property without waiting.</summary>
    public void SetProperty(string name, bool value)
    {
        if (!_stopping)
        {
            var flag = value ? 1 : 0;
            var reply = Track(name);
            Untrack(reply, MpvNative.SetPropertyAsync(_handle, reply, name, MpvFormat.Flag, &flag), name);
        }
    }

    /// <summary>Sets a property and waits; returns null on success or libmpv's reason.</summary>
    public string? SetPropertyNow(string name, string value)
    {
        var result = _stopping ? MpvError.Generic : MpvNative.SetPropertyString(_handle, name, value);
        return result < 0 ? MpvNative.Describe(result) : null;
    }

    /// <summary>Reads a property as text, or null when it is unavailable.</summary>
    public string? GetString(string name)
    {
        if (_stopping)
        {
            return null;
        }

        var value = MpvNative.GetPropertyString(_handle, name);
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return MpvNative.Utf8(value);
        }
        finally
        {
            MpvNative.Free(value);
        }
    }

    /// <summary>Reads a numeric property, or null when it is unavailable.</summary>
    public double? GetDouble(string name)
    {
        if (_stopping)
        {
            return null;
        }

        double value;
        return MpvNative.GetProperty(_handle, name, MpvFormat.Double, &value) >= 0 ? value : null;
    }

    /// <summary>Stops the event thread and destroys the instance. Render contexts must be freed first.</summary>
    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        _stopping = true;
        MpvNative.Wakeup(_handle);
        if (Thread.CurrentThread != _eventThread && !_eventThread.Join(TimeSpan.FromSeconds(5)))
        {
            // Destroying the handle while the event thread still uses it would be undefined
            // behaviour; leaking one player instance is the safer failure.
            _log("error", "The libmpv event thread did not stop; the player instance was not destroyed.");
            _handle = IntPtr.Zero;
            return;
        }

        MpvNative.TerminateDestroy(_handle);
        _handle = IntPtr.Zero;
        foreach (var completion in _pending.Values)
        {
            completion.TrySetResult(MpvError.Generic);
        }

        _pending.Clear();
    }

    private ulong Track(string name)
    {
        var reply = (ulong)Interlocked.Increment(ref _nextReply);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reply] = completion;
        completion.Task.ContinueWith(
            task =>
            {
                if (task.Result < 0)
                {
                    _log("warn", $"libmpv could not set {name}: {MpvNative.Describe(task.Result)}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return reply;
    }

    private void Untrack(ulong reply, int result, string name)
    {
        if (result < 0 && _pending.TryRemove(reply, out var completion))
        {
            _log("warn", $"libmpv refused to set {name}: {MpvNative.Describe(result)}");
            completion.TrySetResult(0);
        }
    }

    private void EventLoop()
    {
        while (true)
        {
            var native = MpvNative.WaitEvent(_handle, -1);
            if (native == null)
            {
                continue;
            }

            // Any event ends the loop once stopping: libmpv clears a pending wakeup whenever it
            // returns an event, so waiting for the wakeup's own empty event could block forever.
            if (native->EventId == MpvEventId.Shutdown || _stopping)
            {
                break;
            }

            if (native->EventId == MpvEventId.None)
            {
                continue;
            }

            MpvMessage message;
            try
            {
                message = Translate(native);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _log("error", $"Could not read a libmpv event: {error}");
                continue;
            }

            if (message.Id is MpvEventId.CommandReply or MpvEventId.SetPropertyReply or MpvEventId.GetPropertyReply)
            {
                if (_pending.TryRemove(message.ReplyUserData, out var completion))
                {
                    completion.TrySetResult(message.Error);
                }

                continue;
            }

            try
            {
                MessageReceived?.Invoke(message);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A handler bug must not kill the event thread; playback would silently freeze.
                _log("error", $"A libmpv event handler failed: {error}");
            }
        }
    }

    private static MpvMessage Translate(MpvEvent* native)
    {
        var message = new MpvMessage(native->EventId, native->Error, native->ReplyUserData);
        switch (native->EventId)
        {
            case MpvEventId.PropertyChange when native->Data != IntPtr.Zero:
                var property = (MpvEventProperty*)native->Data;
                message = message with
                {
                    PropertyName = MpvNative.Utf8(property->Name),
                    PropertyFormat = property->Data == IntPtr.Zero ? MpvFormat.None : property->Format,
                };
                if (property->Data != IntPtr.Zero)
                {
                    message = property->Format switch
                    {
                        MpvFormat.Double => message with { DoubleValue = *(double*)property->Data },
                        MpvFormat.Flag => message with { FlagValue = *(int*)property->Data != 0 },
                        MpvFormat.Int64 => message with { Int64Value = *(long*)property->Data },
                        MpvFormat.String or MpvFormat.OsdString => message with { StringValue = MpvNative.Utf8(*(IntPtr*)property->Data) },
                        _ => message,
                    };
                }

                break;
            case MpvEventId.StartFile when native->Data != IntPtr.Zero:
                message = message with { PlaylistEntryId = ((MpvEventStartFile*)native->Data)->PlaylistEntryId };
                break;
            case MpvEventId.EndFile when native->Data != IntPtr.Zero:
                var end = (MpvEventEndFile*)native->Data;
                message = message with { EndReason = end->Reason, EndError = end->Error, PlaylistEntryId = end->PlaylistEntryId };
                break;
            case MpvEventId.LogMessage when native->Data != IntPtr.Zero:
                var log = (MpvEventLogMessage*)native->Data;
                message = message with
                {
                    LogPrefix = MpvNative.Utf8(log->Prefix),
                    LogLevel = MpvNative.Utf8(log->Level),
                    LogText = MpvNative.Utf8(log->Text)?.TrimEnd(),
                };
                break;
        }

        return message;
    }
}
