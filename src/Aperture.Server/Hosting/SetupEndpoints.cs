using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aperture.Core.Server;
using Aperture.Server.Casting;
using Aperture.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Hosting;

/// <summary>
/// The plain-HTTP port. It serves the phone setup page and Aperture's certificate (both
/// public), the control requests that answer this computer alone and need the token from
/// <c>server.json</c>, and media for a TV that is casting (<see cref="CastEndpoints"/>: one
/// random address per cast, answering only that TV). No library data is ever sent over it.
/// </summary>
internal static class SetupEndpoints
{
    public static async Task HandleAsync(HttpContext http, ServerContext server, ILogger log)
    {
        var path = http.Request.Path.Value ?? "/";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";

        if (path.StartsWith("/control/", StringComparison.Ordinal))
        {
            await HandleControlAsync(http, server, log, path);
            return;
        }

        if (path.StartsWith("/cast/", StringComparison.Ordinal))
        {
            await CastEndpoints.HandleMediaAsync(http, server, log, path);
            return;
        }

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        switch (path)
        {
            case "/aperture-ca.crt":
                http.Response.ContentType = "application/x-x509-ca-cert";
                http.Response.Headers.ContentDisposition = "attachment; filename=\"Aperture-media-server.crt\"";
                await http.Response.Body.WriteAsync(server.Certificates.Authority.Export(X509ContentType.Cert), http.RequestAborted);
                return;
            case "/":
                http.Response.ContentType = "text/html; charset=utf-8";
                http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                await http.Response.WriteAsync(Page(server, http), http.RequestAborted);
                return;
            default:
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    /// <summary>Addresses devices can use to reach this computer, best first.</summary>
    public static IReadOnlyList<string> Addresses(int port, string scheme, bool allowVpn)
    {
        var allowed = CertificateAuthority.LocalAddresses()
            .Where(a => NetworkAccess.IsAllowed(a, allowVpn) && NetworkAccess.Classify(a) != NetworkZone.Loopback)
            .ToList();
        return PreferRouted(allowed, RoutedAddresses())
            .OrderBy(a => NetworkAccess.Classify(a))
            .ThenBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .Select(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"{scheme}://[{a}]:{port}" : $"{scheme}://{a}:{port}")
            .ToList();
    }

    /// <summary>
    /// Keeps the addresses of adapters that have a default gateway (the Wi-Fi or Ethernet the
    /// phone is on) when there are any. Adapters without one are virtual switches for Hyper-V,
    /// WSL, Docker or virtual machines, which a phone can't reach; listing them first would put
    /// a useless address on the Copy address button. They still answer if someone types them.
    /// </summary>
    internal static IReadOnlyList<IPAddress> PreferRouted(IReadOnlyList<IPAddress> addresses, IReadOnlySet<IPAddress> routed)
    {
        var preferred = addresses.Where(routed.Contains).ToList();
        return preferred.Count > 0 ? preferred : addresses;
    }

    private static HashSet<IPAddress> RoutedAddresses()
    {
        var routed = new HashSet<IPAddress>();
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                if (!properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    routed.Add(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0 ? new IPAddress(address.GetAddressBytes()) : address);
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // No adapter information: every address is listed.
        }

        return routed;
    }

    private static async Task HandleControlAsync(HttpContext http, ServerContext server, ILogger log, string path)
    {
        var remote = server.RemoteAddress(http);
        var token = http.Request.Headers[ServerControlClient.TokenHeader].ToString();
        if (remote is null || !IPAddress.IsLoopback(remote) || !TokenMatches(token, server.ControlToken))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (path == "/control/status" && HttpMethods.IsGet(http.Request.Method))
        {
            var settings = await server.SettingsAsync(http.RequestAborted);
            var (install, problem) = await server.Ffmpeg.FindAsync(settings.FfmpegPath, http.RequestAborted);
            var status = new ServerStatus(
                Running: true,
                Addresses: Addresses(server.Port, "https", settings.AllowVpn),
                SetupAddresses: Addresses(server.SetupPort, "http", settings.AllowVpn),
                CertificateFingerprint: CertificateAuthority.Fingerprint(server.Certificates.Authority),
                HasPassphrase: await server.Store.GetPassphraseHashAsync(http.RequestAborted) is not null,
                SharedFolders: (await server.Library.SharedFoldersAsync(http.RequestAborted)).Count,
                ActiveStreams: server.Activity.ActiveDevices,
                Converter: install?.Describe(),
                ConverterProblem: problem,
                KeepingAwake: server.KeepingAwake());
            await http.Response.WriteAsJsonAsync(status, http.RequestAborted);
            return;
        }

        if (path.StartsWith("/control/cast/", StringComparison.Ordinal))
        {
            await CastEndpoints.HandleControlAsync(http, server, path);
            return;
        }

        if (path == "/control/stop" && HttpMethods.IsPost(http.Request.Method))
        {
            log.LogInformation("Stopping, as asked from this computer");
            http.Response.StatusCode = StatusCodes.Status202Accepted;
            server.RequestStop();
            return;
        }

        http.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static bool TokenMatches(string given, string expected) =>
        given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(expected));

    private static string Page(ServerContext server, HttpContext http)
    {
        var host = http.Request.Host.Host;
        var secure = $"https://{(host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host)}:{server.Port}/";
        var fingerprint = CertificateAuthority.Fingerprint(server.Certificates.Authority);
        var name = WebUtility.HtmlEncode(Environment.MachineName);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
            <meta name="color-scheme" content="dark">
            <title>Set up Aperture</title>
            <style>
              :root { --bg:#161719; --surface:#1f2023; --line:#2c2d31; --text:#ecebe8; --muted:#a3a29e; --accent:#f2a93b; }
              * { box-sizing:border-box; }
              body { margin:0; background:var(--bg); color:var(--text); font:16px/1.5 -apple-system, "Segoe UI Variable Text", "Segoe UI", Roboto, sans-serif; -webkit-text-size-adjust:100%; }
              main { max-width:560px; margin:0 auto; padding:max(32px, env(safe-area-inset-top)) 20px 48px; }
              .mark { width:28px; height:28px; border-radius:50%; border:3px solid var(--accent); margin-bottom:20px; }
              h1 { font-size:24px; line-height:1.25; font-weight:600; margin:0 0 6px; letter-spacing:-0.01em; }
              p { margin:0 0 12px; color:var(--muted); }
              ol { list-style:none; counter-reset:step; padding:0; margin:28px 0 0; }
              li { counter-increment:step; position:relative; padding:0 0 22px 40px; }
              li::before { content:counter(step); position:absolute; left:0; top:0; width:26px; height:26px; border-radius:50%; background:var(--surface); border:1px solid var(--line); color:var(--text); font-size:13px; font-weight:600; display:flex; align-items:center; justify-content:center; }
              h2 { font-size:16px; font-weight:600; margin:1px 0 4px; }
              a.button { display:inline-block; margin-top:6px; padding:10px 16px; border-radius:8px; background:var(--accent); color:#1a1408; font-weight:600; text-decoration:none; }
              a.link { color:var(--text); }
              details { margin-top:6px; color:var(--muted); }
              summary { cursor:pointer; color:var(--text); }
              code { font:12px/1.6 ui-monospace, "Cascadia Mono", Menlo, monospace; color:var(--muted); word-break:break-all; }
            </style>
            </head>
            <body>
            <main>
              <div class="mark" aria-hidden="true"></div>
              <h1>Watch from {{name}}</h1>
              <p>Aperture uses its own certificate so your phone and this computer talk privately on your network. Install it once, then open Aperture.</p>
              <ol>
                <li>
                  <h2>Install the certificate</h2>
                  <a class="button" href="/aperture-ca.crt">Download certificate</a>
                  <details>
                    <summary>On iPhone or iPad</summary>
                    <p>Tap Allow, then open Settings › Profile Downloaded › Install. Then go to Settings › General › About › Certificate Trust Settings and turn on Aperture Media Server.</p>
                  </details>
                  <details>
                    <summary>On Android</summary>
                    <p>Open Settings › Security › More security settings › Encryption &amp; credentials › Install a certificate › CA certificate, and choose the downloaded file. Names vary a little between phones.</p>
                  </details>
                  <details>
                    <summary>Check it’s the right certificate</summary>
                    <p>The SHA-256 fingerprint should match the one in Aperture › Settings › Media server on {{name}}:</p>
                    <code>{{fingerprint}}</code>
                  </details>
                </li>
                <li>
                  <h2>Open Aperture</h2>
                  <p>Sign in with the passphrase you set on {{name}}. Add the page to your home screen to open it like an app.</p>
                  <a class="link" href="{{WebUtility.HtmlEncode(secure)}}">{{WebUtility.HtmlEncode(secure)}}</a>
                </li>
              </ol>
            </main>
            </body>
            </html>
            """;
    }
}
