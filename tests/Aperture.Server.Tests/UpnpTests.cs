using System.Net;
using Aperture.Server.Casting;

namespace Aperture.Server.Tests;

/// <summary>Reading what renderers send: SSDP answers and device descriptions, which come from the network and are checked.</summary>
public sealed class UpnpTests
{
    private static readonly Uri Location = new("http://192.168.1.40:52235/dmr/description.xml");

    [Fact]
    public void Reads_an_embedded_renderer_and_resolves_its_services()
    {
        var description = UpnpDescription.Parse(Describe("/upnp/control/AVTransport1", "/upnp/control/ConnectionManager1", embedded: true), Location);

        Assert.NotNull(description);
        Assert.Equal("uuid:abcd", description.Renderer.Id);
        Assert.Equal("Bedroom TV", description.Renderer.Name);
        Assert.Equal("Samsung UE43", description.Renderer.Model);
        Assert.Equal("192.168.1.40", description.Renderer.Address);
        Assert.Equal(new Uri("http://192.168.1.40:52235/upnp/control/AVTransport1"), description.AvTransport.ControlUrl);
        Assert.Equal("urn:schemas-upnp-org:service:AVTransport:1", description.AvTransport.ServiceType);
        Assert.NotNull(description.ConnectionManager);
    }

    [Fact]
    public void Never_sends_control_requests_anywhere_but_the_renderer()
    {
        Assert.Null(UpnpDescription.Parse(Describe("http://203.0.113.9/steal", "/cm"), Location));
        Assert.Null(UpnpDescription.Parse(Describe("https://192.168.1.40/avt", "/cm"), Location));

        // A ConnectionManager elsewhere is dropped; the renderer stays usable.
        var description = UpnpDescription.Parse(Describe("/avt", "http://192.168.1.99/cm"), Location);
        Assert.NotNull(description);
        Assert.Null(description.ConnectionManager);
    }

    [Fact]
    public void Refuses_documents_with_a_DTD_and_things_that_arent_renderers()
    {
        var withDtd = "<?xml version=\"1.0\"?><!DOCTYPE root [<!ENTITY x \"boom\">]>" + Describe("/avt", "/cm")[Describe("/avt", "/cm").IndexOf("<root", StringComparison.Ordinal)..];
        Assert.Null(UpnpDescription.Parse(withDtd, Location));
        Assert.Null(UpnpDescription.Parse("<root><device><UDN>uuid:x</UDN></device></root>", Location));
        Assert.Null(UpnpDescription.Parse("not xml", Location));
    }

    [Fact]
    public void Names_from_the_network_are_kept_to_one_short_line()
    {
        var description = UpnpDescription.Parse(Describe("/avt", "/cm", name: "TV\twith\nwith a very long name that goes on and on and on and on and on and on and on and on and on"), Location);

        Assert.DoesNotContain('\n', description!.Renderer.Name);
        Assert.True(description.Renderer.Name.Length <= 80);
    }

    [Fact]
    public void Accepts_renderer_answers_from_the_address_they_name()
    {
        var answer = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://192.168.1.40:52235/dmr/description.xml\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\nUSN: uuid:abcd::urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n";

        Assert.Equal(Location, SsdpDiscovery.ParseResponse(answer, IPAddress.Parse("192.168.1.40")));
        Assert.Equal(Location, SsdpDiscovery.ParseResponse(answer, IPAddress.Parse("192.168.1.40").MapToIPv6()));
        Assert.Null(SsdpDiscovery.ParseResponse(answer, IPAddress.Parse("192.168.1.41")));
    }

    [Fact]
    public void Ignores_other_answers_and_addresses_outside_the_home_network()
    {
        Assert.Null(SsdpDiscovery.ParseResponse("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.40/d.xml\r\nST: urn:schemas-upnp-org:device:MediaServer:1\r\n\r\n", IPAddress.Parse("192.168.1.40")));
        Assert.Null(SsdpDiscovery.ParseResponse("HTTP/1.1 200 OK\r\nLOCATION: http://8.8.8.8/d.xml\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n", IPAddress.Parse("8.8.8.8")));
        Assert.Null(SsdpDiscovery.ParseResponse("NOTIFY * HTTP/1.1\r\nLOCATION: http://192.168.1.40/d.xml\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n", IPAddress.Parse("192.168.1.40")));
        Assert.Null(SsdpDiscovery.ParseResponse("HTTP/1.1 200 OK\r\nLOCATION: http://tv.local/d.xml\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n", IPAddress.Parse("192.168.1.40")));
    }

    [Fact]
    public void Metadata_escapes_the_title_and_address()
    {
        var xml = UpnpRenderer.Metadata("Tom & Jerry <1>", new Uri("http://192.168.1.2:8080/cast/abc/media.mp4"), "video/mp4", "DLNA.ORG_OP=01", 125, video: true);

        Assert.Contains("<dc:title>Tom &amp; Jerry &lt;1&gt;</dc:title>", xml, StringComparison.Ordinal);
        Assert.Contains("protocolInfo=\"http-get:*:video/mp4:DLNA.ORG_OP=01\"", xml, StringComparison.Ordinal);
        Assert.Contains("duration=\"0:02:05.000\"", xml, StringComparison.Ordinal);
        Assert.Contains("object.item.videoItem", xml, StringComparison.Ordinal);
        System.Xml.Linq.XDocument.Parse(xml);
    }

    private static string Describe(string avTransport, string connectionManager, bool embedded = false, string name = "Bedroom TV")
    {
        var renderer = $"""
            <device>
              <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
              <friendlyName>{System.Security.SecurityElement.Escape(name)}</friendlyName>
              <manufacturer>Samsung</manufacturer>
              <modelName>UE43</modelName>
              <UDN>uuid:abcd</UDN>
              <serviceList>
                <service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>{avTransport}</controlURL></service>
                <service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType><controlURL>{connectionManager}</controlURL></service>
              </serviceList>
            </device>
            """;
        var device = embedded
            ? $"<device><deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType><friendlyName>Hub</friendlyName><UDN>uuid:root</UDN><deviceList>{renderer}</deviceList></device>"
            : renderer;
        return $"<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\">{device}</root>";
    }
}
