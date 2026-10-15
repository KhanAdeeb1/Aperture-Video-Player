using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Aperture.Server.Casting;

/// <summary>
/// Finds media renderers with an SSDP search (UPnP's discovery): one multicast question on each
/// home-network interface, then the answers that arrive within a few seconds. Only answers from
/// home-network addresses whose description lives at that same address are kept.
/// </summary>
public static class SsdpDiscovery
{
    public const string RendererType = "urn:schemas-upnp-org:device:MediaRenderer:1";

    private static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 1900);

    /// <summary>Description addresses of the renderers that answered.</summary>
    public static async Task<IReadOnlyList<Uri>> SearchAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var found = new HashSet<Uri>();
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var local in LocalAddresses())
            {
                try
                {
                    var socket = new UdpClient(new IPEndPoint(local, 0));
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    sockets.Add(socket);
                }
                catch (SocketException)
                {
                    // An interface that can't send multicast is skipped.
                }
            }

            var request = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                $"ST: {RendererType}\r\n" +
                "USER-AGENT: Windows/10 UPnP/1.1 Aperture/1.0\r\n\r\n");

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(wait);
            var receivers = sockets.Select(s => ReceiveAsync(s, found, timer.Token)).ToList();

            // UDP can drop a packet; ask twice.
            for (var round = 0; round < 2 && !timer.IsCancellationRequested; round++)
            {
                foreach (var socket in sockets)
                {
                    try
                    {
                        await socket.SendAsync(request, Group, timer.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        // Keep listening on the others.
                    }
                }

                try
                {
                    await Task.Delay(400, timer.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await Task.WhenAll(receivers).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (found)
            {
                return [.. found];
            }
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>The LOCATION of an SSDP answer from <paramref name="sender"/>, or null when it isn't a renderer's answer or points anywhere else.</summary>
    public static Uri? ParseResponse(string text, IPAddress sender)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sender);
        var lines = text.Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) || !lines[0].Contains(" 200", StringComparison.Ordinal))
        {
            return null;
        }

        string? location = null;
        string? type = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
            {
                location = value;
            }
            else if (name.Equals("ST", StringComparison.OrdinalIgnoreCase))
            {
                type = value;
            }
        }

        if (type is null || !type.StartsWith("urn:schemas-upnp-org:device:MediaRenderer:", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var host))
        {
            return null;
        }

        var from = sender.IsIPv4MappedToIPv6 ? sender.MapToIPv4() : sender;
        return host.Equals(from) && UpnpDescription.IsAllowedAddress(host) ? uri : null;
    }

    /// <summary>This computer's IPv4 addresses on home networks (and loopback, for a renderer on this computer).</summary>
    internal static IEnumerable<IPAddress> LocalAddresses()
    {
        IEnumerable<NetworkInterface> interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast)
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && UpnpDescription.IsAllowedAddress(address))
                {
                    yield return address;
                }
            }
        }
    }

    private static async Task ReceiveAsync(UdpClient socket, HashSet<Uri> found, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            if (result.Buffer.Length > 4096)
            {
                continue;
            }

            if (ParseResponse(Encoding.ASCII.GetString(result.Buffer), result.RemoteEndPoint.Address) is { } location)
            {
                lock (found)
                {
                    found.Add(location);
                }
            }
        }
    }
}
