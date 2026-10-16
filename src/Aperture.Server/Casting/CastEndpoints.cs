using System.Text.Json;
using Aperture.Core.Casting;
using Aperture.Core.Server;
using Aperture.Server.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Casting;

/// <summary>
/// The casting requests on the plain-HTTP port. TVs can't use Aperture's own certificate, so
/// they fetch media over HTTP, from <c>/cast/{token}/…</c>: the token is random, made for one TV
/// and one cast, answers only that TV's address, and stops working when the cast ends. The
/// <c>/control/cast/…</c> requests come from the desktop app and are checked like every
/// control request (this computer only, with the control token).
/// </summary>
internal static class CastEndpoints
{
    private static readonly JsonSerializerOptions Json = ServerControlClient.SerializerOptions;

    /// <summary>Handles <c>/control/cast/…</c>; the caller has already checked it is this computer with the token.</summary>
    public static async Task HandleControlAsync(HttpContext http, ServerContext server, string path)
    {
        var cast = server.Casting;
        var method = http.Request.Method;
        switch (path)
        {
            case "/control/cast/renderers" when HttpMethods.IsGet(method):
                var renderers = await cast.RenderersAsync(http.Request.Query["search"] == "1", http.RequestAborted);
                await http.Response.WriteAsJsonAsync(renderers, Json, http.RequestAborted);
                return;
            case "/control/cast/status" when HttpMethods.IsGet(method):
                await http.Response.WriteAsJsonAsync(cast.Status, Json, http.RequestAborted);
                return;
            case "/control/cast/start" when HttpMethods.IsPost(method):
                var request = await ReadAsync<CastStartRequest>(http);
                if (request is null)
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var (status, problem) = await cast.StartAsync(request, http.RequestAborted);
                if (status is null)
                {
                    http.Response.StatusCode = StatusCodes.Status409Conflict;
                    await http.Response.WriteAsJsonAsync(new { error = problem }, Json, http.RequestAborted);
                    return;
                }

                await http.Response.WriteAsJsonAsync(status, Json, http.RequestAborted);
                return;
            case "/control/cast/play" when HttpMethods.IsPost(method):
                Answer(http, await cast.PlayAsync());
                return;
            case "/control/cast/pause" when HttpMethods.IsPost(method):
                Answer(http, await cast.PauseAsync());
                return;
            case "/control/cast/stop" when HttpMethods.IsPost(method):
                Answer(http, await cast.StopAsync());
                return;
            case "/control/cast/seek" when HttpMethods.IsPost(method):
                Answer(http, await ReadAsync<CastSeek>(http) is { } seek && await cast.SeekAsync(seek.Seconds));
                return;
            default:
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    /// <summary>Handles <c>/cast/{token}/{name}</c>, the media a TV fetches.</summary>
    public static async Task HandleMediaAsync(HttpContext http, ServerContext server, ILogger log, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
            || server.Casting.FindStream(parts[1], server.RemoteAddress(http)) is not { } stream)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var file = stream.Session.Status.Path;
        if (file is null || !File.Exists(file))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        server.Activity.Record(CastManager.ActivityId);
        var headers = http.Response.Headers;
        headers["transferMode.dlna.org"] = "Streaming";
        headers["contentFeatures.dlna.org"] = stream.Plan.IsDirect ? CastManager.DirectFeatures : CastManager.StreamFeatures;

        if (stream.Plan.IsDirect)
        {
            // The original, read-only, with byte ranges so the TV can seek.
            await Results.File(file, stream.Plan.Mime, enableRangeProcessing: true).ExecuteAsync(http);
            return;
        }

        http.Response.ContentType = stream.Plan.Mime;
        headers.AcceptRanges = "none";
        if (HttpMethods.IsHead(http.Request.Method))
        {
            return;
        }

        // A TV often opens the stream twice (to look, then to play); only the newest is fed.
        var current = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        stream.Replace(current);
        try
        {
            var settings = await server.SettingsAsync(http.RequestAborted);
            var (install, _) = await server.Ffmpeg.FindAsync(settings.FfmpegPath, http.RequestAborted);
            if (install is null)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            // After a seek the stream before may take a moment to let go of its conversion slot.
            var started = false;
            for (var attempt = 0; attempt < 20 && !started && !current.IsCancellationRequested; attempt++)
            {
                if (attempt > 0)
                {
                    try
                    {
                        await Task.Delay(250, current.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // the TV went away or asked again
                    }
                }

                started = await server.Conversions.StreamProgressiveAsync(
                    install, 0, file, stream.OffsetSeconds, stream.Plan.Plan, settings.MaxConversions,
                    () =>
                    {
                        server.Activity.Record(CastManager.ActivityId);
                        return http.Response.Body;
                    },
                    current.Token);
            }

            if (!started)
            {
                log.LogWarning("A TV asked for a converted stream, but {Limit} conversions are already running", settings.MaxConversions);
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        }
        finally
        {
            stream.Release(current);
            current.Dispose();
        }
    }

    private static void Answer(HttpContext http, bool done) =>
        http.Response.StatusCode = done ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict;

    private static async Task<T?> ReadAsync<T>(HttpContext http)
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(Json, http.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return default;
        }
    }
}
