using System.Net;
using System.Net.Http.Json;
using Aperture.Core.Server;
using Aperture.Server.Hosting;

namespace Aperture.Server.Tests;

public class SecurityTests
{
    [Fact]
    public async Task Everything_except_signing_in_needs_a_signed_in_device()
    {
        await using var test = await TestServer.StartAsync(async t =>
        {
            var folder = await t.AddFolderAsync("Films", shared: true);
            await t.AddItemAsync(folder, "Night Market.mp4", "Night Market");
        });
        using var client = test.Client();

        using var session = await client.GetAsync("api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.False((await TestServer.ReadJsonAsync(session)).GetProperty("signedIn").GetBoolean());

        foreach (var path in new[] { "api/library", "api/items/1", "api/items/1/image", "api/shows/abc", "api/speedtest" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var play = await client.PostAsJsonAsync("api/items/1/play", TestServer.PlayRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, play.StatusCode);
        using var progress = await client.PostAsJsonAsync("api/items/1/progress", new { position = 10 });
        Assert.Equal(HttpStatusCode.Unauthorized, progress.StatusCode);
    }

    [Fact]
    public async Task Signing_in_needs_the_passphrase_and_sets_a_locked_down_cookie()
    {
        await using var test = await TestServer.StartAsync();
        using var client = test.Client();

        using var wrong = await client.PostAsJsonAsync("api/session", new { passphrase = "not the passphrase", deviceName = "Phone" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(wrong.Headers.Contains("Set-Cookie"));

        using var right = await client.PostAsJsonAsync("api/session", new { passphrase = TestServer.Passphrase, deviceName = "Phone" });
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        var cookie = Assert.Single(right.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(ServerContext.CookieName + "=", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);

        using var library = await client.GetAsync("api/library");
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        Assert.Equal("Phone", Assert.Single(await test.Store.GetDevicesAsync()).Name);
    }

    [Fact]
    public async Task Without_a_passphrase_nobody_can_sign_in()
    {
        await using var test = await TestServer.StartAsync(withPassphrase: false);
        using var client = test.Client();

        using var response = await client.PostAsJsonAsync("api/session", new { passphrase = "", deviceName = "Phone" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await test.Store.GetDevicesAsync());
    }

    [Fact]
    public async Task A_made_up_or_signed_out_cookie_is_refused()
    {
        await using var test = await TestServer.StartAsync();
        using var forged = test.Client();
        forged.DefaultRequestHeaders.Add("Cookie", $"{ServerContext.CookieName}={DeviceToken.Create()}");
        using (var response = await forged.GetAsync("api/library"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var client = await test.SignedInClientAsync();
        using (var signOut = await client.DeleteAsync("api/session"))
        {
            Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        }

        Assert.Empty(await test.Store.GetDevicesAsync());
        using var after = await client.GetAsync("api/library");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Changing_the_passphrase_signs_devices_out()
    {
        await using var test = await TestServer.StartAsync();
        using var client = await test.SignedInClientAsync();

        await test.Store.SetPassphraseHashAsync(Passphrase.Hash("a different one 7", 1000));

        using var response = await client.GetAsync("api/library");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Changing_requests_need_the_aperture_header()
    {
        await using var test = await TestServer.StartAsync();
        using var client = test.Client(withHeader: false);

        using var response = await client.PostAsJsonAsync("api/session", new { passphrase = TestServer.Passphrase, deviceName = "Form on another site" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await test.Store.GetDevicesAsync());
    }

    [Fact]
    public async Task Repeated_wrong_passphrases_lock_the_address_out()
    {
        await using var test = await TestServer.StartAsync();
        using var client = test.Client();
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await client.PostAsJsonAsync("api/session", new { passphrase = $"guess number {i}", deviceName = "x" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Locked now: even the right passphrase waits.
        using (var locked = await client.PostAsJsonAsync("api/session", new { passphrase = TestServer.Passphrase, deviceName = "x" }))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
            Assert.NotNull(locked.Headers.RetryAfter);
        }

        test.Clock.Advance(TimeSpan.FromMinutes(16));
        using var later = await client.PostAsJsonAsync("api/session", new { passphrase = TestServer.Passphrase, deviceName = "x" });
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task Public_addresses_are_refused_everything()
    {
        await using var test = await TestServer.StartAsync();
        test.Context.RemoteAddress = _ => IPAddress.Parse("203.0.113.9");
        using var client = test.Client();
        using var setup = test.SetupClient();

        foreach (var path in new[] { "/", "app.js", "api/session", "api/library", "media/abc/file.mp4" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var signIn = await client.PostAsJsonAsync("api/session", new { passphrase = TestServer.Passphrase, deviceName = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        using var page = await setup.GetAsync("/");
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Empty(await test.Store.GetDevicesAsync());
    }

    [Fact]
    public async Task Private_network_addresses_need_the_setting()
    {
        await using var test = await TestServer.StartAsync();
        test.Context.RemoteAddress = _ => IPAddress.Parse("100.101.102.103");
        using var client = test.Client();
        using (var refused = await client.GetAsync("api/session"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        await test.Store.SaveSettingsAsync((await test.Store.LoadSettingsAsync()) with { AllowVpn = true });
        test.Clock.Advance(TimeSpan.FromSeconds(5)); // past the server's settings cache

        using var allowed = await client.GetAsync("api/session");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        await using var test = await TestServer.StartAsync();
        using var client = test.Client();

        using var page = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var csp = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(page.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(page.Headers.GetValues("Referrer-Policy")));
        Assert.False(page.Headers.Contains("Server"));
    }
}
