using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aperture.Core.Casting;

namespace Aperture.Server.Casting;

/// <summary>The renderer answered with a UPnP error (it is there, but refused the action).</summary>
public sealed class UpnpFaultException(string action, int code, string? description)
    : Exception($"{action} was refused with UPnP error {code}{(string.IsNullOrWhiteSpace(description) ? string.Empty : ": " + description)}")
{
    public int Code { get; } = code;
}

/// <summary>The renderer didn't answer (switched off, asleep, or off the network).</summary>
public sealed class RendererUnreachableException(string action, Exception inner)
    : Exception($"The renderer didn't answer {action}.", inner);

/// <summary>What a renderer says about its transport.</summary>
/// <param name="State">"PLAYING", "PAUSED_PLAYBACK", "TRANSITIONING", "STOPPED", "NO_MEDIA_PRESENT".</param>
/// <param name="Status">"OK" or "ERROR_OCCURRED".</param>
public sealed record TransportInfo(string State, string Status)
{
    public bool HasError => Status.Equals("ERROR_OCCURRED", StringComparison.OrdinalIgnoreCase);

    public CastState ToCastState() => State.ToUpperInvariant() switch
    {
        "PLAYING" => CastState.Playing,
        "PAUSED_PLAYBACK" or "PAUSED_RECORDING" => CastState.Paused,
        "TRANSITIONING" => CastState.Buffering,
        _ => CastState.Stopped,
    };
}

/// <summary>What a renderer says about where it is.</summary>
/// <param name="TrackUri">The address it is playing, when it says.</param>
/// <param name="RelativeSeconds">Position in the current stream.</param>
/// <param name="DurationSeconds">The stream's length, when it knows.</param>
public sealed record PositionInfo(string? TrackUri, double? RelativeSeconds, double? DurationSeconds);

/// <summary>
/// Sends UPnP AV actions (SOAP over plain HTTP, as the standard requires) to one renderer.
/// Every request goes to the renderer's own address on the home network, with a short timeout,
/// so a TV that has gone away is noticed quickly and never holds anything up.
/// </summary>
public sealed class UpnpRenderer(UpnpDescription description, HttpClient http)
{
    private const string InstanceId = "0";

    public UpnpDescription Description { get; } = description;

    public CastRenderer Info => Description.Renderer;

    /// <summary>The renderer's sink protocol list, or <see cref="RendererFormats.Unknown"/> when it won't say.</summary>
    public async Task<RendererFormats> GetFormatsAsync(CancellationToken cancellationToken)
    {
        if (Description.ConnectionManager is not { } manager)
        {
            return RendererFormats.Unknown;
        }

        try
        {
            var result = await InvokeAsync(manager, "GetProtocolInfo", [], cancellationToken).ConfigureAwait(false);
            return RendererFormats.Parse(Value(result, "Sink"));
        }
        catch (UpnpFaultException)
        {
            return RendererFormats.Unknown;
        }
    }

    public Task SetUriAsync(Uri media, string metadata, CancellationToken cancellationToken) =>
        InvokeAsync(Description.AvTransport, "SetAVTransportURI", [("InstanceID", InstanceId), ("CurrentURI", media.AbsoluteUri), ("CurrentURIMetaData", metadata)], cancellationToken);

    public Task PlayAsync(CancellationToken cancellationToken) =>
        InvokeAsync(Description.AvTransport, "Play", [("InstanceID", InstanceId), ("Speed", "1")], cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken) =>
        InvokeAsync(Description.AvTransport, "Pause", [("InstanceID", InstanceId)], cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        InvokeAsync(Description.AvTransport, "Stop", [("InstanceID", InstanceId)], cancellationToken);

    /// <summary>Seeks by time in the stream: REL_TIME first, then ABS_TIME for renderers that only know that.</summary>
    public async Task SeekAsync(double seconds, CancellationToken cancellationToken)
    {
        var target = DlnaTime.Format(seconds);
        try
        {
            await InvokeAsync(Description.AvTransport, "Seek", [("InstanceID", InstanceId), ("Unit", "REL_TIME"), ("Target", target)], cancellationToken).ConfigureAwait(false);
        }
        catch (UpnpFaultException fault) when (fault.Code is 710 or 402)
        {
            await InvokeAsync(Description.AvTransport, "Seek", [("InstanceID", InstanceId), ("Unit", "ABS_TIME"), ("Target", target)], cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TransportInfo> GetTransportInfoAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(Description.AvTransport, "GetTransportInfo", [("InstanceID", InstanceId)], cancellationToken).ConfigureAwait(false);
        return new TransportInfo(Value(result, "CurrentTransportState") ?? string.Empty, Value(result, "CurrentTransportStatus") ?? "OK");
    }

    public async Task<PositionInfo> GetPositionInfoAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(Description.AvTransport, "GetPositionInfo", [("InstanceID", InstanceId)], cancellationToken).ConfigureAwait(false);
        return new PositionInfo(Value(result, "TrackURI"), DlnaTime.Parse(Value(result, "RelTime")), DlnaTime.Parse(Value(result, "TrackDuration")));
    }

    /// <summary>DIDL-Lite metadata for the media: title, class and the resource with its protocol info. Samsung TVs refuse media without it.</summary>
    public static string Metadata(string title, Uri media, string mime, string contentFeatures, double durationSeconds, bool video)
    {
        ArgumentNullException.ThrowIfNull(media);
        var duration = durationSeconds > 0 ? $" duration=\"{DlnaTime.FormatDuration(durationSeconds)}\"" : string.Empty;
        return "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">"
            + "<item id=\"aperture\" parentID=\"0\" restricted=\"1\">"
            + $"<dc:title>{SecurityElement.Escape(title)}</dc:title>"
            + $"<upnp:class>{(video ? "object.item.videoItem" : "object.item.audioItem.musicTrack")}</upnp:class>"
            + $"<res protocolInfo=\"http-get:*:{SecurityElement.Escape(mime)}:{SecurityElement.Escape(contentFeatures)}\"{duration}>{SecurityElement.Escape(media.AbsoluteUri)}</res>"
            + "</item></DIDL-Lite>";
    }

    private async Task<XElement> InvokeAsync(UpnpService service, string action, (string Name, string Value)[] arguments, CancellationToken cancellationToken)
    {
        var body = new StringBuilder()
            .Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>")
            .Append(CultureInfo.InvariantCulture, $"<u:{action} xmlns:u=\"{SecurityElement.Escape(service.ServiceType)}\">");
        foreach (var (name, value) in arguments)
        {
            body.Append(CultureInfo.InvariantCulture, $"<{name}>{SecurityElement.Escape(value)}</{name}>");
        }

        body.Append(CultureInfo.InvariantCulture, $"</u:{action}></s:Body></s:Envelope>");

        using var request = new HttpRequestMessage(HttpMethod.Post, service.ControlUrl)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml"),
        };
        request.Content.Headers.ContentType!.CharSet = "\"utf-8\"";
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service.ServiceType}#{action}\"");

        string text;
        HttpStatusCode status;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            text = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new RendererUnreachableException(action, ex);
        }

        XElement? envelopeBody;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            envelopeBody = XDocument.Load(reader).Descendants().FirstOrDefault(e => e.Name.LocalName == "Body");
        }
        catch (XmlException) when (status != HttpStatusCode.OK)
        {
            throw new UpnpFaultException(action, (int)status, null);
        }
        catch (XmlException ex)
        {
            throw new RendererUnreachableException(action, ex);
        }

        if (status != HttpStatusCode.OK)
        {
            var error = envelopeBody?.Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
            var code = int.TryParse(error?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : (int)status;
            throw new UpnpFaultException(action, code, error?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value);
        }

        return envelopeBody?.Elements().FirstOrDefault() ?? new XElement("empty");
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[UpnpDescription.MaxBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string? Value(XElement result, string name) =>
        result.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
}
