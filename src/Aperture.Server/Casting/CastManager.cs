using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Aperture.Core.Casting;
using Aperture.Core.Media;
using Aperture.Core.Server;
using Aperture.Server.Hosting;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Casting;

/// <summary>How often a cast looks at the TV and how long it waits. Tests shorten them.</summary>
/// <param name="Poll">Between reports from the TV while it answers.</param>
/// <param name="StartGrace">How long a TV may stay stopped after Play before that counts as a failure.</param>
/// <param name="StartTimeout">How long a TV may take to start at all.</param>
/// <param name="GiveUp">How long to keep trying a TV that stopped answering.</param>
/// <param name="SaveEvery">How often a moving position is written to history.</param>
public sealed record CastTimings(TimeSpan Poll, TimeSpan StartGrace, TimeSpan StartTimeout, TimeSpan GiveUp, TimeSpan SaveEvery)
{
    public static CastTimings Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10));
}

/// <summary>One address a TV fetches media from: a random token that works for that TV's address only, while its cast lasts.</summary>
internal sealed class CastStream(string token, CastSession session, TvPlan plan, double offsetSeconds, IPAddress renderer)
{
    public string Token { get; } = token;

    public CastSession Session { get; } = session;

    public TvPlan Plan { get; } = plan;

    /// <summary>Where in the file a converted stream starts.</summary>
    public double OffsetSeconds { get; } = offsetSeconds;

    public IPAddress Renderer { get; } = renderer;

    /// <summary>The converted stream being sent now, cancelled when the TV asks again or the cast ends.</summary>
    private CancellationTokenSource? _current;

    /// <summary>Cuts the converted stream being sent now, if any.</summary>
    public void CancelCurrent() => Cancel(Volatile.Read(ref _current));

    /// <summary>Makes <paramref name="next"/> the stream being sent, cutting the one before.</summary>
    public void Replace(CancellationTokenSource next) => Cancel(Interlocked.Exchange(ref _current, next));

    /// <summary>The stream sent with <paramref name="source"/> has ended.</summary>
    public void Release(CancellationTokenSource source) => Interlocked.CompareExchange(ref _current, null, source);

    private static void Cancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It had already finished.
        }
    }
}

/// <summary>
/// Casting to DLNA TVs: finds renderers, runs at most one cast at a time and hands out the
/// addresses TVs fetch media from. Lives in the media server so a cast keeps going, and keeps
/// saving its position, when the desktop app is closed.
/// </summary>
public sealed class CastManager : IAsyncDisposable
{
    /// <summary>DLNA delivery flags for the original file: byte seeking, streaming transfer.</summary>
    internal const string DirectFeatures = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    /// <summary>DLNA delivery flags for a converted stream: no seeking by bytes, converted content.</summary>
    internal const string StreamFeatures = "DLNA.ORG_OP=00;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    /// <summary>The activity id a cast records, so the computer stays awake while the TV plays.</summary>
    internal const long ActivityId = -1;

    private readonly ConcurrentDictionary<string, UpnpRenderer> _renderers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CastStream> _streams = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly ServerContext _server;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private CastSession? _current;
    private bool _searched;

    public CastManager(ServerContext server, ILogger log)
    {
        _server = server;
        _log = log;

        // Renderers are on the home network: no proxy, and a short timeout so a TV that has gone is noticed.
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(4) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Aperture/{typeof(CastManager).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}");
    }

    internal CastTimings Timings { get; set; } = CastTimings.Default;

    /// <summary>Finds renderers' description addresses. Tests replace it; the real one is an SSDP search.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<Uri>>> Search { get; set; } = ct => SsdpDiscovery.SearchAsync(TimeSpan.FromSeconds(3), ct);

    internal ServerContext Server => _server;

    internal ILogger Log => _log;

    /// <summary>The current cast's status, or the last one's once it has ended.</summary>
    public CastStatus Status => _current?.Status ?? CastStatus.None;

    /// <summary>Renderers found so far; <paramref name="search"/> (or the first call) looks again.</summary>
    public async Task<IReadOnlyList<CastRenderer>> RenderersAsync(bool search, CancellationToken cancellationToken)
    {
        if (search || !_searched)
        {
            await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var locations = await Search(cancellationToken).ConfigureAwait(false);
                var found = await Task.WhenAll(locations.Distinct().Select(l => DescribeAsync(l, cancellationToken))).ConfigureAwait(false);
                var keep = found.OfType<UpnpRenderer>().Select(r => r.Info.Id).ToHashSet(StringComparer.Ordinal);
                if (_current?.Renderer.Info.Id is { } casting)
                {
                    keep.Add(casting);
                }

                foreach (var gone in _renderers.Keys.Where(id => !keep.Contains(id)).ToList())
                {
                    _renderers.TryRemove(gone, out _);
                }

                _searched = true;
                _log.LogInformation("Found {Count} TVs or other renderers on the network", _renderers.Count);
            }
            finally
            {
                _searchGate.Release();
            }
        }

        return [.. _renderers.Values.Select(r => r.Info).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Reads a renderer's description and remembers it. Null when it isn't a usable renderer.</summary>
    internal async Task<UpnpRenderer?> DescribeAsync(Uri location, CancellationToken cancellationToken)
    {
        if (location.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(location.Host.Trim('[', ']'), out var host) || !UpnpDescription.IsAllowedAddress(host))
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[UpnpDescription.MaxBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            if (UpnpDescription.Parse(Encoding.UTF8.GetString(buffer, 0, total), location) is not { } description)
            {
                return null;
            }

            var renderer = new UpnpRenderer(description, _http);
            _renderers[description.Renderer.Id] = renderer;
            return renderer;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Starts casting, replacing any cast in progress. Returns the first status, or why it can't start.</summary>
    public async Task<(CastStatus? Status, string? Problem)> StartAsync(CastStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_renderers.TryGetValue(request.RendererId ?? string.Empty, out var renderer))
        {
            return (null, "That TV wasn't found on the network. Search again.");
        }

        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || SharedMedia.HasRelativeSegments(path) || !MediaFileTypes.IsPlayableFile(path) || !File.Exists(path))
        {
            return (null, "That file can't be found.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is { } previous)
            {
                // The same TV simply gets the new video; another TV is told to stop.
                await previous.StopAsync(CastState.Stopped, "Casting moved to another video.", sendStop: previous.Renderer.Info.Id != renderer.Info.Id).ConfigureAwait(false);
            }

            var session = new CastSession(this, renderer, request with { Path = path, Title = string.IsNullOrWhiteSpace(request.Title) ? MediaFileTypes.TitleFromPath(path) : request.Title });
            _current = session;
            session.Begin();
            _log.LogInformation("Casting to {Model}", renderer.Info.Model ?? "a renderer");
            return (session.Status, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> PlayAsync() => _current is { } s && await s.PlayAsync().ConfigureAwait(false);

    public async Task<bool> PauseAsync() => _current is { } s && await s.PauseAsync().ConfigureAwait(false);

    public async Task<bool> SeekAsync(double seconds) => _current is { } s && await s.SeekAsync(seconds).ConfigureAwait(false);

    /// <summary>Stops the cast on the TV and ends it here, saving the last position.</summary>
    public async Task<bool> StopAsync()
    {
        if (_current is not { } session)
        {
            return false;
        }

        await session.StopAsync(CastState.Stopped, "Stopped casting.", sendStop: true).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_current is { } session)
        {
            // The server is stopping; the TV can't be fed any more, so tell it to stop too.
            await session.StopAsync(CastState.Stopped, "The media server stopped.", sendStop: true).ConfigureAwait(false);
        }

        _http.Dispose();
        _gate.Dispose();
        _searchGate.Dispose();
    }

    /// <summary>A new media address for the session's TV.</summary>
    internal CastStream AddStream(CastSession session, TvPlan plan, double offsetSeconds)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var stream = new CastStream(token, session, plan, offsetSeconds, IPAddress.Parse(session.Renderer.Info.Address));
        _streams[token] = stream;
        return stream;
    }

    /// <summary>Retires a media address; a converted stream still being sent on it is cut.</summary>
    internal void RemoveStream(string token)
    {
        if (_streams.TryRemove(token, out var stream))
        {
            stream.CancelCurrent();
        }
    }

    /// <summary>The stream for a token, only for the TV it was made for and only while its cast lasts.</summary>
    internal CastStream? FindStream(string token, IPAddress? remote)
    {
        if (remote is null || !_streams.TryGetValue(token, out var stream) || !stream.Session.IsActive)
        {
            return null;
        }

        var from = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        return from.Equals(stream.Renderer) || (IPAddress.IsLoopback(from) && IPAddress.IsLoopback(stream.Renderer)) ? stream : null;
    }

    /// <summary>The address on this computer that the TV can reach us at.</summary>
    internal static IPAddress LocalAddressFor(IPAddress renderer)
    {
        if (IPAddress.IsLoopback(renderer))
        {
            return renderer;
        }

        using var socket = new Socket(renderer.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(renderer, 9); // no packet is sent; this only picks the route
        return ((IPEndPoint)socket.LocalEndPoint!).Address;
    }
}
