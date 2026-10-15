using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Aperture.Core.Library;
using Aperture.Core.Server;
using Aperture.Data;
using Aperture.Server.Library;
using Aperture.Server.Media;
using Aperture.Server.Security;
using Microsoft.AspNetCore.Http;

namespace Aperture.Server.Hosting;

/// <summary>A direct-play address handed to one device for one item.</summary>
/// <param name="DeviceId">The device it was issued to.</param>
/// <param name="ItemId">The item it plays.</param>
/// <param name="Plan">For a continuous converted MP4: how to convert. Null for the original file.</param>
/// <param name="StartSeconds">For a continuous converted MP4: where it starts.</param>
internal sealed record DirectStream(long DeviceId, long ItemId, Core.Server.StreamPlan? Plan = null, double StartSeconds = 0)
{
    public DateTimeOffset LastAccessUtc { get; set; }
}

/// <summary>Everything the request handlers share, for the life of one server run.</summary>
public sealed class ServerContext(
    ServerPaths paths,
    SqliteServerStore store,
    SharedLibrary library,
    SqliteLibraryStore libraryStore,
    IPlaybackHistory history,
    Core.Settings.ISettingsStore appSettings,
    FfmpegTools ffmpeg,
    ConversionManager conversions,
    Thumbnails thumbnails,
    PlaybackActivity activity,
    LoginThrottle throttle,
    ServerCertificates certificates,
    TimeProvider clock)
{
    /// <summary>Name of the sign-in cookie. "__Host-" makes browsers require HTTPS, the whole site and no domain.</summary>
    public const string CookieName = "__Host-aperture";

    /// <summary>Header every changing API request must carry, which a form on another site cannot add.</summary>
    public const string CsrfHeader = "X-Aperture";

    private static readonly TimeSpan SettingsLife = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DirectStreamLife = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, DirectStream> _direct = new(StringComparer.Ordinal);
    private (DateTimeOffset At, ServerSettings Settings)? _settings;

    public ServerPaths Paths { get; } = paths;

    public SqliteServerStore Store { get; } = store;

    public SharedLibrary Library { get; } = library;

    public SqliteLibraryStore LibraryStore { get; } = libraryStore;

    public IPlaybackHistory History { get; } = history;

    public Core.Settings.ISettingsStore AppSettings { get; } = appSettings;

    public FfmpegTools Ffmpeg { get; } = ffmpeg;

    public ConversionManager Conversions { get; } = conversions;

    public Thumbnails Thumbnails { get; } = thumbnails;

    public PlaybackActivity Activity { get; } = activity;

    public LoginThrottle Throttle { get; } = throttle;

    public ServerCertificates Certificates { get; } = certificates;

    public TimeProvider Clock { get; } = clock;

    /// <summary>Token for the loopback control requests; new for every run.</summary>
    public string ControlToken { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>The ports actually listening (known once Kestrel has started).</summary>
    public int Port { get; internal set; }

    public int SetupPort { get; internal set; }

    /// <summary>Casting to TVs (set once the server has started).</summary>
    public Casting.CastManager Casting { get; internal set; } = null!;

    /// <summary>True while the server asks Windows to stay awake.</summary>
    public Func<bool> KeepingAwake { get; internal set; } = () => false;

    /// <summary>Asks the host to shut down.</summary>
    public Action RequestStop { get; internal set; } = () => { };

    /// <summary>Where the remote address of a request comes from. Tests replace it to act as a public address.</summary>
    internal Func<HttpContext, IPAddress?> RemoteAddress { get; set; } = c => c.Connection.RemoteIpAddress;

    public async Task<ServerSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        if (_settings is { } cached && Clock.GetUtcNow() - cached.At < SettingsLife)
        {
            return cached.Settings;
        }

        var settings = await Store.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        _settings = (Clock.GetUtcNow(), settings);
        return settings;
    }

    /// <summary>A new direct-play token for the device and item.</summary>
    internal string AddDirectStream(long deviceId, long itemId, Core.Server.StreamPlan? plan = null, double startSeconds = 0)
    {
        foreach (var old in _direct.Where(p => p.Value.DeviceId == deviceId).Select(p => p.Key).ToList())
        {
            _direct.TryRemove(old, out _);
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _direct[token] = new DirectStream(deviceId, itemId, plan, startSeconds) { LastAccessUtc = Clock.GetUtcNow() };
        return token;
    }

    internal DirectStream? FindDirectStream(string token)
    {
        if (!_direct.TryGetValue(token, out var stream))
        {
            return null;
        }

        var now = Clock.GetUtcNow();
        if (now - stream.LastAccessUtc > DirectStreamLife)
        {
            _direct.TryRemove(token, out _);
            return null;
        }

        stream.LastAccessUtc = now;
        return stream;
    }

    /// <summary>
    /// Re-checks a stream on every request: the device must still be signed in and the item
    /// still shared. Returns the item, or null when either has changed.
    /// </summary>
    internal async Task<LibraryItem?> AuthorizeStreamAsync(long deviceId, long itemId, CancellationToken cancellationToken)
    {
        var devices = await Store.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (!devices.Any(d => d.Id == deviceId && d.ExpiresUtc > Clock.GetUtcNow()))
        {
            return null;
        }

        return await Library.FindSharedAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    internal void ForgetDevice(long deviceId)
    {
        foreach (var old in _direct.Where(p => p.Value.DeviceId == deviceId).Select(p => p.Key).ToList())
        {
            _direct.TryRemove(old, out _);
        }
    }
}
