using System.Net;
using System.Security.Cryptography.X509Certificates;
using Aperture.Core.Server;
using Aperture.Server.Security;

namespace Aperture.Server.Tests;

public class ControlAndCertificateTests
{
    [Fact]
    public void Addresses_of_adapters_with_a_gateway_come_first_and_virtual_switches_are_left_out()
    {
        var wifi = System.Net.IPAddress.Parse("192.168.1.20");
        var hyperV = System.Net.IPAddress.Parse("172.25.240.1");
        var all = new[] { hyperV, wifi };

        Assert.Equal([wifi], Aperture.Server.Hosting.SetupEndpoints.PreferRouted(all, new HashSet<System.Net.IPAddress> { wifi }));
        // With no gateway anywhere (an isolated network), nothing is hidden.
        Assert.Equal(all, Aperture.Server.Hosting.SetupEndpoints.PreferRouted(all, new HashSet<System.Net.IPAddress>()));
    }

    [Fact]
    public async Task Control_requests_need_the_token_and_stop_the_server_on_their_own()
    {
        await using var test = await TestServer.StartAsync();
        using var setup = test.SetupClient();

        using (var noToken = await setup.GetAsync("control/status"))
        {
            Assert.Equal(HttpStatusCode.NotFound, noToken.StatusCode); // doesn't even admit the address exists
        }

        var wrong = new ServerStateFile(Environment.ProcessId, test.Server.Port, test.Server.SetupPort, new string('0', 64), DateTimeOffset.UtcNow);
        var control = new ServerControlClient(setup);
        Assert.Null(await control.GetStatusAsync(wrong));
        Assert.False(await control.StopAsync(wrong));

        var state = wrong with { ControlToken = test.Context.ControlToken };
        var status = await control.GetStatusAsync(state);
        Assert.NotNull(status);
        Assert.True(status.Running);
        Assert.True(status.HasPassphrase);
        Assert.Equal(CertificateAuthority.Fingerprint(test.Context.Certificates.Authority), status.CertificateFingerprint);

        Assert.True(await control.StopAsync(state));
        var stopped = test.Server.WaitForShutdownAsync();
        Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(20))));
    }

    [Fact]
    public async Task Control_requests_answer_this_computer_only()
    {
        await using var test = await TestServer.StartAsync();
        test.Context.RemoteAddress = _ => IPAddress.Parse("192.168.1.50"); // another device at home
        using var setup = test.SetupClient();
        var state = new ServerStateFile(Environment.ProcessId, test.Server.Port, test.Server.SetupPort, test.Context.ControlToken, DateTimeOffset.UtcNow);

        Assert.Null(await new ServerControlClient(setup).GetStatusAsync(state));
        Assert.False(await new ServerControlClient(setup).StopAsync(state));

        // The setup page itself is for other devices.
        using var page = await setup.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task The_setup_port_serves_no_library_or_media()
    {
        await using var test = await TestServer.StartAsync(async t =>
        {
            var films = await t.AddFolderAsync("Films", shared: true);
            await t.AddItemAsync(films, "Night Market.mp4", "Night Market");
        });
        using var setup = test.SetupClient();

        foreach (var path in new[] { "api/library", "api/session", "app.js", "media/x/file.mp4", "index.html" })
        {
            using var response = await setup.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var page = await setup.GetStringAsync("/");
        Assert.Contains(CertificateAuthority.Fingerprint(test.Context.Certificates.Authority), page, StringComparison.Ordinal);
        Assert.DoesNotContain("Night Market", page, StringComparison.Ordinal);

        var certificate = X509CertificateLoader.LoadCertificate(await setup.GetByteArrayAsync("aperture-ca.crt"));
        Assert.Equal(test.Context.Certificates.Authority.Thumbprint, certificate.Thumbprint);
        Assert.False(certificate.HasPrivateKey);
    }

    [Fact]
    public void The_authority_may_only_vouch_for_private_names()
    {
        using var authority = CertificateAuthority.Create("Test-PC");

        var constraints = Assert.Single(authority.Extensions.Cast<X509Extension>(), e => e.Oid?.Value == "2.5.29.30");
        Assert.False(constraints.Critical); // Apple and Android honour it either way; non-critical keeps older clients working
        Assert.True(authority.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);

        using var server = CertificateAuthority.IssueServerCertificate(
            authority,
            [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("100.100.1.2"), IPAddress.Parse("203.0.113.9")],
            "Test-PC");
        var names = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        var addresses = names.EnumerateIPAddresses().ToList();
        Assert.Contains(IPAddress.Parse("192.168.1.20"), addresses);
        Assert.Contains(IPAddress.Parse("100.100.1.2"), addresses);
        Assert.Contains(IPAddress.Loopback, addresses);
        Assert.DoesNotContain(IPAddress.Parse("203.0.113.9"), addresses);
        Assert.Contains("test-pc.local", names.EnumerateDnsNames());
        Assert.True(TestServer.ChainsTo(server, authority));
        Assert.False(server.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
    }

    [Fact]
    public async Task Phones_connect_over_tls_that_chains_to_the_authority()
    {
        await using var test = await TestServer.StartAsync();
        X509Certificate2? seen = null;
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                seen = certificate is null ? null : new X509Certificate2(certificate);
                return certificate is not null && TestServer.ChainsTo(certificate, test.Context.Certificates.Authority);
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = test.Address };

        using var response = await client.GetAsync("api/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(seen);
        Assert.NotEqual(test.Context.Certificates.Authority.Thumbprint, seen.Thumbprint);

        // A client that trusts only the system's authorities refuses it: nothing public vouches for it.
        using var strict = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = test.Address };
        await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync("api/session"));
    }
}
