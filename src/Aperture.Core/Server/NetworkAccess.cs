using System.Net;
using System.Net.Sockets;

namespace Aperture.Core.Server;

/// <summary>Where a request comes from, as far as its address tells.</summary>
public enum NetworkZone
{
    /// <summary>This computer.</summary>
    Loopback,

    /// <summary>The home network: private IPv4 ranges, link-local and IPv6 unique-local addresses.</summary>
    Local,

    /// <summary>A private overlay network such as Tailscale (100.64.0.0/10, fd7a:115c:a1e0::/48).</summary>
    PrivateNetwork,

    /// <summary>Anything else, including the public internet.</summary>
    Public,
}

/// <summary>
/// Decides which addresses may talk to the media server. The server never accepts requests
/// from public addresses: remote access goes through a private network the user set up
/// (a VPN such as Tailscale or WireGuard), and only once they turn that on.
/// </summary>
public static class NetworkAccess
{
    /// <summary>Classifies an address. IPv4 addresses mapped into IPv6 are treated as IPv4.</summary>
    public static NetworkZone Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return NetworkZone.Loopback;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                10 => NetworkZone.Local,
                172 when b[1] is >= 16 and <= 31 => NetworkZone.Local,
                192 when b[1] == 168 => NetworkZone.Local,
                169 when b[1] == 254 => NetworkZone.Local,
                100 when b[1] is >= 64 and <= 127 => NetworkZone.PrivateNetwork, // shared address space, used by Tailscale
                _ => NetworkZone.Public,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0)
            {
                return NetworkZone.PrivateNetwork; // Tailscale's IPv6 range
            }

            if (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
            {
                return NetworkZone.Local;
            }
        }

        return NetworkZone.Public;
    }

    /// <summary>True when a request from <paramref name="address"/> may reach the server.</summary>
    public static bool IsAllowed(IPAddress? address, bool allowPrivateNetwork)
    {
        if (address is null)
        {
            return false;
        }

        return Classify(address) switch
        {
            NetworkZone.Loopback or NetworkZone.Local => true,
            NetworkZone.PrivateNetwork => allowPrivateNetwork,
            _ => false,
        };
    }
}
