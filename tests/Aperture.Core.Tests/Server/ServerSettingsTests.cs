using Aperture.Core.Server;

namespace Aperture.Core.Tests.Server;

public class ServerSettingsTests
{
    [Fact]
    public void Defaults_are_local_only()
    {
        var settings = ServerSettings.Default;
        Assert.False(settings.AllowVpn);
        Assert.False(settings.StartWithWindows);
        Assert.Null(settings.FfmpegPath);
        Assert.Equal(1, settings.MaxConversions);
    }

    [Fact]
    public void Out_of_range_values_fall_back_to_defaults()
    {
        var settings = new ServerSettings(Port: 80, SetupPort: 70000, FfmpegPath: "  ", MaxConversions: 9).Normalized();

        Assert.Equal(8443, settings.Port);
        Assert.Equal(8080, settings.SetupPort);
        Assert.Null(settings.FfmpegPath);
        Assert.Equal(1, settings.MaxConversions);
    }

    [Fact]
    public void The_two_ports_are_never_the_same()
    {
        Assert.NotEqual(9000, new ServerSettings(Port: 9000, SetupPort: 9000).Normalized().SetupPort);
        var clash = new ServerSettings(Port: 8080, SetupPort: 8080).Normalized();
        Assert.NotEqual(clash.Port, clash.SetupPort);
    }
}
