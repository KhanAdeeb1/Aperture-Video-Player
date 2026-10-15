using Aperture.Server.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;

namespace Aperture.Server.Hosting;

/// <summary>
/// The media itself. Video players (Safari's in particular) don't always send cookies with
/// media requests, so each address carries an unguessable token issued to one signed-in device
/// for one item. Every request still checks that the device is signed in and the item shared.
/// </summary>
internal static class MediaEndpoints
{
    private static readonly FileExtensionContentTypeProvider Types = new();

    public static void Map(IEndpointRouteBuilder app, ServerContext server)
    {
        app.MapGet("/media/{token}/stream.mp4", async (string token, HttpContext http, CancellationToken ct) =>
        {
            if (server.FindDirectStream(token) is not { Plan: { } plan } stream)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var item = await server.AuthorizeStreamAsync(stream.DeviceId, stream.ItemId, ct);
            var settings = await server.SettingsAsync(ct);
            var (install, _) = await server.Ffmpeg.FindAsync(settings.FfmpegPath, ct);
            if (item is null || install is null || !File.Exists(item.Path))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            server.Activity.Record(stream.DeviceId);
            http.Response.ContentType = "video/mp4";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.AcceptRanges = "none";
            var started = await server.Conversions.StreamProgressiveAsync(
                install, item.Id, item.Path, stream.StartSeconds, plan, settings.MaxConversions,
                () =>
                {
                    server.Activity.Record(stream.DeviceId);
                    return http.Response.Body;
                },
                ct);
            if (!started)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });

        app.MapGet("/media/{token}/file{extension}", async (string token, string extension, CancellationToken ct) =>
        {
            if (server.FindDirectStream(token) is not { Plan: null } stream)
            {
                return Results.NotFound();
            }

            var item = await server.AuthorizeStreamAsync(stream.DeviceId, stream.ItemId, ct);
            if (item is null || !File.Exists(item.Path))
            {
                return Results.NotFound();
            }

            server.Activity.Record(stream.DeviceId);
            var type = Types.TryGetContentType(item.Path, out var known) ? known : "application/octet-stream";
            if (type == "video/quicktime")
            {
                type = "video/mp4"; // Android browsers refuse video/quicktime for H.264 .mov files they can play
            }

            // Range requests let the player seek and fetch only what it needs; the file is opened read-only.
            return Results.File(item.Path, type, enableRangeProcessing: true);
        });

        app.MapGet("/media/{id}/index.m3u8", async (string id, HttpContext http, CancellationToken ct) =>
        {
            if (server.Conversions.Touch(id) is not { } session
                || await server.AuthorizeStreamAsync(session.DeviceId, session.ItemId, ct) is null)
            {
                return Results.NotFound();
            }

            server.Activity.Record(session.DeviceId);
            var playlist = await server.Conversions.ReadPlaylistAsync(session, ct);
            if (playlist is null)
            {
                return Results.Problem(session.Error ?? "The stream didn't start in time.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.Text(playlist, "application/vnd.apple.mpegurl");
        });

        app.MapGet("/media/{id}/{segment}", async (string id, string segment, HttpContext http, CancellationToken ct) =>
        {
            if (server.Conversions.Touch(id) is not { } session
                || await server.AuthorizeStreamAsync(session.DeviceId, session.ItemId, ct) is null
                || ConversionManager.SegmentPath(session, segment) is not { } path)
            {
                return Results.NotFound();
            }

            server.Activity.Record(session.DeviceId);
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(path, "video/mp2t");
        });

        app.MapDelete("/media/{id}", async (string id, HttpContext http) =>
        {
            if (server.Conversions.Touch(id) is { } session && await Security.DeviceAuth.ResolveAsync(http, server, http.RequestAborted) is { } device && device.Id == session.DeviceId)
            {
                await server.Conversions.StopAsync(id);
            }

            return Results.NoContent();
        });
    }
}
