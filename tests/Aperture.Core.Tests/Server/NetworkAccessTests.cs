using System.Net;
using Aperture.Core.Server;

namespace Aperture.Core.Tests.Server;

public class NetworkAccessTests
{
    [Theory]
    [InlineData("127.0.0.1", NetworkZone.Loopback)]
    [InlineData("::1", NetworkZone.Loopback)]
    [InlineData("192.168.1.20", NetworkZone.Local)]
    [InlineData("10.0.0.5", NetworkZone.Local)]
    [InlineData("172.16.0.1", NetworkZone.Local)]
    [InlineData("172.31.255.254", NetworkZone.Local)]
    [InlineData("169.254.10.10", NetworkZone.Local)]
    [InlineData("fe80::1", NetworkZone.Local)]
    [InlineData("fd12:3456::1", NetworkZone.Local)]
    [InlineData("::ffff:192.168.1.20", NetworkZone.Local)]
    [InlineData("100.64.0.1", NetworkZone.PrivateNetwork)]
    [InlineData("100.127.255.254", NetworkZone.PrivateNetwork)]
    [InlineData("fd7a:115c:a1e0::1", NetworkZone.PrivateNetwork)]
    [InlineData("8.8.8.8", NetworkZone.Public)]
    [InlineData("172.32.0.1", NetworkZone.Public)]
    [InlineData("100.128.0.1", NetworkZone.Public)]
    [InlineData("192.169.1.1", NetworkZone.Public)]
    [InlineData("2001:4860:4860::8888", NetworkZone.Public)]
    [InlineData("fec0::1", NetworkZone.Public)]
    [InlineData("::ffff:8.8.8.8", NetworkZone.Public)]
    public void Classifies_addresses(string address, NetworkZone expected) =>
        Assert.Equal(expected, NetworkAccess.Classify(IPAddress.Parse(address)));

    [Fact]
    public void Public_addresses_are_never_allowed()
    {
        Assert.False(NetworkAccess.IsAllowed(IPAddress.Parse("8.8.8.8"), allowPrivateNetwork: false));
        Assert.False(NetworkAccess.IsAllowed(IPAddress.Parse("8.8.8.8"), allowPrivateNetwork: true));
        Assert.False(NetworkAccess.IsAllowed(null, allowPrivateNetwork: true));
    }

    [Fact]
    public void Private_networks_need_the_setting()
    {
        var tailscale = IPAddress.Parse("100.100.1.2");
        Assert.False(NetworkAccess.IsAllowed(tailscale, allowPrivateNetwork: false));
        Assert.True(NetworkAccess.IsAllowed(tailscale, allowPrivateNetwork: true));
        Assert.True(NetworkAccess.IsAllowed(IPAddress.Parse("192.168.0.4"), allowPrivateNetwork: false));
        Assert.True(NetworkAccess.IsAllowed(IPAddress.Loopback, allowPrivateNetwork: false));
    }
}
