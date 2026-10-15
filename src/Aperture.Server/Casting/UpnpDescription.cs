using System.Net;
using System.Xml;
using System.Xml.Linq;
using Aperture.Core.Casting;
using Aperture.Core.Server;

namespace Aperture.Server.Casting;

/// <summary>A media renderer's description: who it is and where its services answer.</summary>
/// <param name="Renderer">Its id, name, model and address.</param>
/// <param name="AvTransport">AVTransport control address and service type (play, pause, seek, position).</param>
/// <param name="ConnectionManager">ConnectionManager control address and service type (what it accepts), when it has one.</param>
public sealed record UpnpDescription(CastRenderer Renderer, UpnpService AvTransport, UpnpService? ConnectionManager)
{
    /// <summary>Largest description Aperture reads; real ones are a few kilobytes.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>
    /// Reads a device description fetched from <paramref name="location"/>. Null when it describes
    /// no media renderer, or when a service would be reached anywhere but the address the
    /// description came from (Aperture never sends requests elsewhere on a renderer's say-so).
    /// </summary>
    public static UpnpDescription? Parse(string xml, Uri location)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(location);
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (doc.Root is null || !IPAddress.TryParse(location.Host.Trim('[', ']'), out var address))
        {
            return null;
        }

        var baseText = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();
        var baseUri = Uri.TryCreate(baseText, UriKind.Absolute, out var declared) ? declared : location;

        // The renderer may be the root device or one embedded in it.
        foreach (var device in doc.Descendants().Where(e => e.Name.LocalName == "device"))
        {
            var services = device.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceList")?.Elements().Where(e => e.Name.LocalName == "service").ToList() ?? [];
            var transport = FindService(services, "AVTransport", baseUri, location);
            if (transport is null)
            {
                continue;
            }

            var udn = Text(device, "UDN");
            var name = Text(device, "friendlyName");
            if (string.IsNullOrWhiteSpace(udn))
            {
                continue;
            }

            var manufacturer = Text(device, "manufacturer");
            var modelName = Text(device, "modelName");
            var model = string.Join(' ', new[] { manufacturer, modelName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            var renderer = new CastRenderer(
                udn.Trim(),
                string.IsNullOrWhiteSpace(name) ? (model.Length > 0 ? model : "TV") : Clean(name),
                model.Length > 0 ? Clean(model) : null,
                (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString());
            return new UpnpDescription(renderer, transport, FindService(services, "ConnectionManager", baseUri, location));
        }

        return null;
    }

    private static UpnpService? FindService(IEnumerable<XElement> services, string kind, Uri baseUri, Uri location)
    {
        foreach (var service in services)
        {
            var type = Text(service, "serviceType");
            var control = Text(service, "controlURL");
            if (type is null || control is null || !type.Contains($":service:{kind}:", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, control.Trim(), out var url)
                || url.Scheme != Uri.UriSchemeHttp
                || !string.Equals(url.Host, location.Host, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new UpnpService(type.Trim(), url);
        }

        return null;
    }

    private static string? Text(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    // Names come from the network; keep them to one short line of printable text.
    private static string Clean(string text)
    {
        var line = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return line.Length > 80 ? line[..80] : line;
    }

    /// <summary>True when a renderer at this address may be used: this computer or the home network.</summary>
    public static bool IsAllowedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return NetworkAccess.Classify(address) is NetworkZone.Local or NetworkZone.Loopback;
    }
}

/// <summary>One UPnP service of a renderer.</summary>
/// <param name="ServiceType">Its exact type ("urn:schemas-upnp-org:service:AVTransport:1").</param>
/// <param name="ControlUrl">Where its SOAP actions go.</param>
public sealed record UpnpService(string ServiceType, Uri ControlUrl);
