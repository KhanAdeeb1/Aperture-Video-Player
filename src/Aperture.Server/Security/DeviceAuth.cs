using Aperture.Core.Server;
using Aperture.Server.Hosting;
using Microsoft.AspNetCore.Http;

namespace Aperture.Server.Security;

/// <summary>Finds the signed-in device behind a request from its cookie.</summary>
internal static class DeviceAuth
{
    public const string ItemKey = "aperture.device";

    /// <summary>The device, or null when the cookie is missing, malformed, unknown, expired or signed out.</summary>
    public static async Task<RemoteDevice?> ResolveAsync(HttpContext http, ServerContext server, CancellationToken cancellationToken)
    {
        if (!http.Request.Cookies.TryGetValue(ServerContext.CookieName, out var token) || !DeviceToken.LooksValid(token))
        {
            return null;
        }

        var now = server.Clock.GetUtcNow();
        var device = await server.Store.FindDeviceAsync(DeviceToken.HashOf(token!), now, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return null;
        }

        // Sliding sign-in: refreshed at most once a minute so playback doesn't write on every request.
        if (now - device.LastSeenUtc > TimeSpan.FromMinutes(1))
        {
            var expires = now + DeviceToken.Lifetime;
            await server.Store.TouchDeviceAsync(device.Id, now, expires, server.RemoteAddress(http)?.ToString(), cancellationToken).ConfigureAwait(false);
            SetCookie(http, token!, expires);
        }

        return device;
    }

    /// <summary>The device the endpoint filter attached to this request.</summary>
    public static RemoteDevice Current(HttpContext http) =>
        http.Items[ItemKey] as RemoteDevice ?? throw new InvalidOperationException("No device is attached to this request.");

    public static void SetCookie(HttpContext http, string token, DateTimeOffset expires) =>
        http.Response.Cookies.Append(ServerContext.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expires,
            IsEssential = true,
        });

    public static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(ServerContext.CookieName, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
}
