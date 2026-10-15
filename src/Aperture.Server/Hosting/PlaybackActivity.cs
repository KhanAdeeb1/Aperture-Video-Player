using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Hosting;

/// <summary>Which devices are streaming right now (a media request in the last minute).</summary>
public sealed class PlaybackActivity(TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastByDevice = new();

    public void Record(long deviceId) => _lastByDevice[deviceId] = clock.GetUtcNow();

    public int ActiveDevices
    {
        get
        {
            var now = clock.GetUtcNow();
            return _lastByDevice.Count(p => now - p.Value < Window);
        }
    }
}

/// <summary>
/// While a device is playing and the user allowed it, asks Windows not to sleep because the
/// computer is idle (<c>SetThreadExecutionState</c>, as video players do). It changes no power
/// setting: closing the lid, the power button and Start › Sleep still sleep, and the request
/// ends as soon as playback stops or the server exits.
/// </summary>
public sealed partial class KeepAwake : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private readonly PlaybackActivity _activity;
    private readonly Func<bool> _enabled;
    private readonly ILogger<KeepAwake> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread? _thread;

    public KeepAwake(PlaybackActivity activity, Func<bool> enabled, ILogger<KeepAwake> log)
    {
        _activity = activity;
        _enabled = enabled;
        _log = log;
        if (OperatingSystem.IsWindows())
        {
            // The request belongs to the thread that makes it, so one thread owns it throughout.
            _thread = new Thread(Run) { IsBackground = true, Name = "Aperture keep-awake" };
            _thread.Start();
        }
    }

    /// <summary>True while the server is asking Windows to stay awake.</summary>
    public bool Active { get; private set; }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            var want = _enabled() && _activity.ActiveDevices > 0;
            if (want != Active)
            {
                _ = SetThreadExecutionState(want ? EsContinuous | EsSystemRequired : EsContinuous);
                Active = want;
                if (want)
                {
                    _log.LogInformation("Asking Windows to stay awake while a device plays");
                }
                else
                {
                    _log.LogInformation("No device is playing; Windows may sleep again");
                }
            }

            _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
        }

        if (Active)
        {
            _ = SetThreadExecutionState(EsContinuous);
            Active = false;
        }
    }
}
