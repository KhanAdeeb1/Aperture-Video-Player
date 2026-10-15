using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Aperture.Core.Casting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Tests;

/// <summary>
/// A DLNA media renderer on loopback that behaves like a TV: it answers SOAP actions, fetches
/// the media it is given (and fails if that doesn't work), and plays in real time. Tests can
/// unplug it, make it refuse the original file, refuse seeking, or have something else take it over.
/// </summary>
internal sealed class FakeRenderer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private double _base;
    private double? _playingSince;

    private FakeRenderer(WebApplication app, string sink)
    {
        _app = app;
        Sink = sink;
    }

    public const string Udn = "uuid:fa4e0000-0000-4000-8000-000000000001";

    public string Sink { get; }

    public Uri Location { get; private set; } = null!;

    /// <summary>Requests are cut off, as if the TV were switched off at the wall.</summary>
    public bool Unplugged { get; set; }

    /// <summary>The TV "can't play" anything that isn't MPEG-TS, as some TVs fail on files they claim to accept.</summary>
    public bool FailOriginals { get; set; }

    /// <summary>Seek answers UPnP error 710.</summary>
    public bool RefuseSeek { get; set; }

    /// <summary>How fast playback runs (1 = real time).</summary>
    public double Rate { get; set; } = 1;

    public string State { get; private set; } = "NO_MEDIA_PRESENT";

    public string Status { get; private set; } = "OK";

    public string? Uri { get; private set; }

    public string? Metadata { get; private set; }

    public List<string> Actions { get; } = [];

    /// <summary>What the TV got when it fetched the media: status, content type, DLNA header and the first bytes.</summary>
    public List<(HttpStatusCode Status, string? Type, string? Features, byte[] Start)> Fetches { get; } = [];

    public double Duration { get; set; }

    public double Position
    {
        get
        {
            lock (_lock)
            {
                var position = _base + (_playingSince is { } since ? (_clock.Elapsed.TotalSeconds - since) * Rate : 0);
                return Duration > 0 ? Math.Min(position, Duration) : position;
            }
        }
    }

    public static async Task<FakeRenderer> StartAsync(string sink = "http-get:*:video/mp4:*,http-get:*:video/mpeg:*,http-get:*:audio/mpeg:*")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var renderer = new FakeRenderer(app, sink);
        app.Run(renderer.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        renderer.Location = new Uri(new Uri(address), "/description.xml");
        return renderer;
    }

    /// <summary>Another controller (the TV's own menu, a phone app) plays something else.</summary>
    public void TakeOver(string uri)
    {
        lock (_lock)
        {
            Uri = uri;
            State = "PLAYING";
            _base = 0;
            _playingSince = _clock.Elapsed.TotalSeconds;
        }
    }

    /// <summary>Reaches the end and stops, as a TV does when a video finishes.</summary>
    public void PlayToEnd()
    {
        lock (_lock)
        {
            _base = Duration;
            _playingSince = null;
            State = "STOPPED";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _http.Dispose();
    }

    private async Task HandleAsync(HttpContext http)
    {
        if (Unplugged)
        {
            http.Abort();
            return;
        }

        if (http.Request.Path == "/description.xml")
        {
            http.Response.ContentType = "text/xml";
            await http.Response.WriteAsync(Description);
            return;
        }

        var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
        var call = XDocument.Parse(body).Descendants().First(e => e.Name.LocalName == "Body").Elements().First();
        string Arg(string name) => call.Elements().First(e => e.Name.LocalName == name).Value;
        var action = call.Name.LocalName;
        lock (Actions)
        {
            Actions.Add(action);
        }

        string result;
        switch (action)
        {
            case "GetProtocolInfo":
                result = $"<Source></Source><Sink>{System.Security.SecurityElement.Escape(Sink)}</Sink>";
                break;
            case "SetAVTransportURI":
                lock (_lock)
                {
                    Uri = Arg("CurrentURI");
                    Metadata = Arg("CurrentURIMetaData");
                    State = "STOPPED";
                    Status = "OK";
                    _base = 0;
                    _playingSince = null;
                }

                result = string.Empty;
                break;
            case "Play":
                if (State != "PAUSED_PLAYBACK")
                {
                    State = "TRANSITIONING";
                    _ = LoadAsync(Uri!);
                }
                else
                {
                    lock (_lock)
                    {
                        State = "PLAYING";
                        _playingSince = _clock.Elapsed.TotalSeconds;
                    }
                }

                result = string.Empty;
                break;
            case "Pause":
                lock (_lock)
                {
                    _base = Position;
                    _playingSince = null;
                    State = "PAUSED_PLAYBACK";
                }

                result = string.Empty;
                break;
            case "Stop":
                lock (_lock)
                {
                    _base = 0;
                    _playingSince = null;
                    State = "STOPPED";
                }

                result = string.Empty;
                break;
            case "Seek":
                if (RefuseSeek)
                {
                    await FaultAsync(http, 710, "Seek mode not supported");
                    return;
                }

                lock (_lock)
                {
                    _base = DlnaTime.Parse(Arg("Target")) ?? 0;
                    _playingSince = State == "PLAYING" ? _clock.Elapsed.TotalSeconds : null;
                }

                result = string.Empty;
                break;
            case "GetTransportInfo":
                result = $"<CurrentTransportState>{State}</CurrentTransportState><CurrentTransportStatus>{Status}</CurrentTransportStatus><CurrentSpeed>1</CurrentSpeed>";
                break;
            case "GetPositionInfo":
                result = $"<Track>1</Track><TrackDuration>{(Duration > 0 ? DlnaTime.Format(Duration) : "0:00:00")}</TrackDuration><TrackMetaData></TrackMetaData>"
                    + $"<TrackURI>{System.Security.SecurityElement.Escape(Uri ?? string.Empty)}</TrackURI><RelTime>{DlnaTime.Format(Position)}</RelTime><AbsTime>NOT_IMPLEMENTED</AbsTime>";
                break;
            default:
                await FaultAsync(http, 401, "Invalid Action");
                return;
        }

        http.Response.ContentType = "text/xml; charset=\"utf-8\"";
        await http.Response.WriteAsync(
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
            + $"<u:{action}Response xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">{result}</u:{action}Response></s:Body></s:Envelope>");
    }

    // Fetches the media like a TV: if it can't be read (or the TV "can't play" originals), the TV stops with an error.
    private async Task LoadAsync(string uri)
    {
        var ok = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var start = new byte[188];
            var read = 0;
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                int n;
                while (read < start.Length && (n = await stream.ReadAsync(start.AsMemory(read))) > 0)
                {
                    read += n;
                }
            }

            lock (Fetches)
            {
                Fetches.Add((response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                    response.Headers.TryGetValues("contentFeatures.dlna.org", out var f) ? f.First() : null, start[..read]));
            }

            var transportStream = read > 0 && start[0] == 0x47;
            ok = response.IsSuccessStatusCode && read > 0 && (!FailOriginals || transportStream);
        }
        catch (HttpRequestException)
        {
            ok = false;
        }

        lock (_lock)
        {
            if (Uri != uri)
            {
                return; // replaced meanwhile
            }

            if (ok)
            {
                State = "PLAYING";
                _base = 0;
                _playingSince = _clock.Elapsed.TotalSeconds;
            }
            else
            {
                State = "STOPPED";
                Status = "ERROR_OCCURRED";
            }
        }
    }

    private static async Task FaultAsync(HttpContext http, int code, string description)
    {
        http.Response.StatusCode = 500;
        http.Response.ContentType = "text/xml; charset=\"utf-8\"";
        await http.Response.WriteAsync(
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>"
            + $"<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{code.ToString(CultureInfo.InvariantCulture)}</errorCode><errorDescription>{description}</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>");
    }

    private const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <friendlyName>Living Room TV</friendlyName>
            <manufacturer>Test</manufacturer>
            <modelName>Fake TV</modelName>
            <UDN>uuid:fa4e0000-0000-4000-8000-000000000001</UDN>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                <controlURL>/upnp/control/AVTransport</controlURL>
              </service>
              <service>
                <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
                <controlURL>/upnp/control/ConnectionManager</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;
}
