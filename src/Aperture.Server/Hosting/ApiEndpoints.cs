using System.Net;
using System.Security.Cryptography;
using Aperture.Core.Library;
using Aperture.Core.Server;
using Aperture.Server.Media;
using Aperture.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Hosting;

/// <summary>Sign-in request.</summary>
public sealed record SignInRequest(string? Passphrase, string? DeviceName);

/// <summary>What the device can play, how fast its connection is, and where to start.</summary>
public sealed record PlayRequest(string[]? Video, string[]? Audio, bool Hls, bool WebM, long? BandwidthBps, double? StartSeconds, bool AvoidDirectPlay);

/// <summary>A position report.</summary>
public sealed record ProgressRequest(double Position, double? Duration);

/// <summary>The answer to <see cref="PlayRequest"/>.</summary>
public sealed record PlayResponse(string Mode, string? Url, double StartSeconds, string Reason, string? Warning, string? Source, string? Stream, string? Converter, string? StreamId);

/// <summary>The web client's API. Everything except signing in needs a signed-in device.</summary>
internal static class ApiEndpoints
{
    public static void Map(IEndpointRouteBuilder app, ServerContext server, ILogger log)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/session", async (HttpContext http, CancellationToken ct) =>
        {
            var device = await DeviceAuth.ResolveAsync(http, server, ct);
            var hasPassphrase = await server.Store.GetPassphraseHashAsync(ct) is not null;
            return Results.Ok(new { signedIn = device is not null, device = device?.Name, server = Environment.MachineName, hasPassphrase });
        });

        api.MapPost("/session", async (HttpContext http, SignInRequest request, CancellationToken ct) =>
        {
            var address = server.RemoteAddress(http) ?? IPAddress.None;
            var wait = server.Throttle.Check(address);
            if (wait > TimeSpan.Zero)
            {
                var seconds = (int)Math.Ceiling(wait.TotalSeconds);
                http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { error = $"Too many tries. Wait {Waiting(seconds)} and try again." }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var hash = await server.Store.GetPassphraseHashAsync(ct);
            if (hash is null)
            {
                return Results.Json(new { error = "This server has no passphrase yet. Set one in Aperture › Settings › Media server." }, statusCode: StatusCodes.Status409Conflict);
            }

            // Hashing takes a moment on purpose; it runs off the request thread's hot path.
            var ok = await Task.Run(() => Passphrase.Verify(request.Passphrase, hash), ct);
            if (!ok)
            {
                server.Throttle.Failed(address);
                log.LogWarning("A sign-in with a wrong passphrase was refused");
                return Results.Json(new { error = "That passphrase isn't right." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            server.Throttle.Succeeded(address);
            var token = DeviceToken.Create();
            var now = server.Clock.GetUtcNow();
            var name = CleanName(request.DeviceName);
            var device = await server.Store.AddDeviceAsync(DeviceToken.HashOf(token), name, now, now + DeviceToken.Lifetime, address.ToString(), ct);
            DeviceAuth.SetCookie(http, token, now + DeviceToken.Lifetime);
            log.LogInformation("Device {Device} ({Name}) signed in", device.Id, name);
            return Results.Ok(new { signedIn = true, device = name });
        });

        var signedIn = api.MapGroup(string.Empty).AddEndpointFilter(async (context, next) =>
        {
            var device = await DeviceAuth.ResolveAsync(context.HttpContext, server, context.HttpContext.RequestAborted);
            if (device is null)
            {
                return Results.Json(new { error = "Sign in to continue." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            context.HttpContext.Items[DeviceAuth.ItemKey] = device;
            return await next(context);
        });

        signedIn.MapDelete("/session", async (HttpContext http, CancellationToken ct) =>
        {
            var device = DeviceAuth.Current(http);
            await server.Store.RemoveDeviceAsync(device.Id, ct);
            server.ForgetDevice(device.Id);
            DeviceAuth.ClearCookie(http);
            log.LogInformation("Device {Device} signed out", device.Id);
            return Results.NoContent();
        });

        signedIn.MapGet("/library", async (CancellationToken ct) => Results.Ok(await server.Library.GetLibraryAsync(ct)));

        signedIn.MapGet("/shows/{id}", async (string id, CancellationToken ct) =>
            await server.Library.GetShowAsync(id, ct) is { } show ? Results.Ok(show) : NotFound());

        signedIn.MapGet("/items/{id:long}", async (long id, CancellationToken ct) =>
            await server.Library.GetItemAsync(id, ct) is { } item ? Results.Ok(item) : NotFound());

        signedIn.MapGet("/items/{id:long}/image", async (long id, HttpContext http, CancellationToken ct) =>
        {
            var item = await server.Library.FindSharedAsync(id, ct);
            if (item is null)
            {
                return NotFound();
            }

            var settings = await server.SettingsAsync(ct);
            var path = await server.Thumbnails.GetAsync(item, settings.FfmpegPath, ct);
            if (path is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
        });

        signedIn.MapPost("/items/{id:long}/play", async (long id, PlayRequest request, HttpContext http, CancellationToken ct) =>
        {
            var device = DeviceAuth.Current(http);
            var item = await server.Library.FindSharedAsync(id, ct);
            if (item is null)
            {
                return NotFound();
            }

            if (!File.Exists(item.Path))
            {
                return Results.Ok(new PlayResponse("unavailable", null, 0, "The file can't be reached right now. Its drive may be disconnected.", null, null, null, null, null));
            }

            var settings = await server.SettingsAsync(ct);
            var (install, problem) = await server.Ffmpeg.FindAsync(settings.FfmpegPath, ct);
            var source = install is not null ? await server.Ffmpeg.InspectAsync(install, item.Path, ct) : null;
            source ??= GuessFromName(item);
            var client = ClientCapabilities.From(request.Video, request.Audio, request.Hls, request.WebM);
            var canConvert = install?.Encoder is not null;
            var plan = StreamPlanner.Decide(source, client, request.BandwidthBps, canConvert, request.AvoidDirectPlay);
            if (plan.Mode == StreamMode.Remux && install is null)
            {
                plan = plan with { Mode = StreamMode.Unavailable };
            }

            var start = Math.Max(0, request.StartSeconds ?? 0);
            if (item.Duration is { } length && start >= length.TotalSeconds - 1)
            {
                start = 0;
            }

            switch (plan.Mode)
            {
                case StreamMode.DirectPlay:
                {
                    var token = server.AddDirectStream(device.Id, item.Id);
                    var extension = Path.GetExtension(item.Path).ToLowerInvariant();
                    return Results.Ok(new PlayResponse("direct", $"/media/{token}/file{extension}", start, plan.Reason, plan.Warning, source.Describe(), "Original file", install?.Describe() ?? problem, null));
                }

                case StreamMode.Remux or StreamMode.Convert when plan.Progressive:
                {
                    var token = server.AddDirectStream(device.Id, item.Id, plan, start);
                    return Results.Ok(new PlayResponse(plan.Mode == StreamMode.Remux ? "remux" : "convert", $"/media/{token}/stream.mp4", start, plan.Reason, plan.Warning, source.Describe(), Describe(plan, source, "continuous MP4"), install!.Describe(), null));
                }

                case StreamMode.Remux or StreamMode.Convert:
                {
                    var session = await server.Conversions.StartAsync(install!, device.Id, item.Id, item.Path, start, plan, settings.MaxConversions);
                    if (session is null)
                    {
                        return Results.Ok(new PlayResponse("unavailable", null, 0, "The server is already converting for another device. Try again when it has finished.", null, source.Describe(), null, install!.Describe(), null));
                    }

                    return Results.Ok(new PlayResponse(plan.Mode == StreamMode.Remux ? "remux" : "convert", $"/media/{session.Id}/index.m3u8", start, plan.Reason, plan.Warning, source.Describe(), Describe(plan, source, "HLS"), install!.Describe(), session.Id));
                }

                default:
                    return Results.Ok(new PlayResponse("unavailable", null, 0, plan.Reason, null, source.Describe(), null, install?.Describe() ?? problem, null));
            }
        });

        signedIn.MapGet("/streams/{id}", (string id, HttpContext http) =>
        {
            var device = DeviceAuth.Current(http);
            var session = server.Conversions.Touch(id);
            if (session is null || session.DeviceId != device.Id)
            {
                return NotFound();
            }

            return Results.Ok(new { speed = session.Speed, produced = session.ProducedSeconds, finished = session.Finished, error = session.Error, encoder = session.EncoderLabel });
        });

        signedIn.MapPost("/items/{id:long}/progress", async (long id, ProgressRequest request, HttpContext http, CancellationToken ct) =>
        {
            server.Activity.Record(DeviceAuth.Current(http).Id); // reported every 10 s while playing
            var item = await server.Library.FindSharedAsync(id, ct);
            if (item is null)
            {
                return NotFound();
            }

            // The desktop's "Remember what I watch" setting applies to every device.
            var app = await server.AppSettings.LoadAsync(ct);
            if (!app.RememberHistory)
            {
                return Results.Ok(new { saved = false, reason = "History is turned off in Aperture's settings." });
            }

            var entry = RemoteProgress.ToEntry(item, request.Position, request.Duration, server.Clock.GetUtcNow());
            if (entry is null)
            {
                return Results.BadRequest(new { error = "That position can't be saved." });
            }

            await server.History.SaveAsync(entry, ct);
            server.Library.Invalidate();
            return Results.Ok(new { saved = true });
        });

        // A known amount of incompressible data, so the client can time its connection.
        signedIn.MapGet("/speedtest", async (int? kb, HttpContext http, CancellationToken ct) =>
        {
            var size = Math.Clamp(kb ?? 1024, 64, 8192) * 1024;
            http.Response.ContentType = "application/octet-stream";
            http.Response.ContentLength = size;
            http.Response.Headers.CacheControl = "no-store";
            var buffer = new byte[64 * 1024];
            RandomNumberGenerator.Fill(buffer);
            for (var sent = 0; sent < size; sent += buffer.Length)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, Math.Min(buffer.Length, size - sent)), ct);
            }
        });
    }

    private static string Describe(StreamPlan plan, SourceMedia source, string format)
    {
        var sound = plan.CopyAudio ? "sound copied" : "AAC stereo";
        return plan.AudioOnly ? $"AAC stereo, {format}"
            : plan.CopyVideo ? $"{StreamPlanner.CodecName(source.VideoCodec ?? "h264")} copied, {sound}, {format}"
            : $"H.264 {plan.TargetHeight}p {StreamPlanner.FormatRate((plan.VideoBitrateKbps ?? 0) * 1000L)}, {sound}, {format}";
    }

    private static IResult NotFound() => Results.Json(new { error = "That isn't shared from this computer." }, statusCode: StatusCodes.Status404NotFound);

    private static string Waiting(int seconds) => seconds < 90 ? $"{seconds} seconds" : $"{(seconds + 59) / 60} minutes";

    private static string CleanName(string? name)
    {
        var text = new string([.. (name ?? string.Empty).Where(c => !char.IsControl(c))]).Trim();
        return text.Length == 0 ? "Browser" : text.Length > 60 ? text[..60] : text;
    }

    /// <summary>When FFprobe isn't available: what the file name suggests, so a likely-playable file is still tried.</summary>
    private static SourceMedia GuessFromName(LibraryItem item)
    {
        var extension = Path.GetExtension(item.Path).ToLowerInvariant();
        var container = extension switch
        {
            ".mp4" or ".m4v" or ".mov" => "mp4",
            ".webm" => "webm",
            ".mkv" => "matroska",
            ".avi" => "avi",
            ".ts" or ".m2ts" or ".mts" => "mpegts",
            _ => extension.TrimStart('.'),
        };
        var codec = item.VideoCodec?.ToLowerInvariant() switch
        {
            null => container == "webm" ? "vp9" : container == "mp4" ? "h264" : null,
            "avc" or "avc1" or "h264" => "h264",
            "hevc" or "h265" => "hevc",
            var other => other,
        };
        var bitrate = item.Bitrate > 0 ? (long)item.Bitrate : (long?)null;
        return new SourceMedia(container, codec, item.Width, item.Height, null, bitrate, container == "webm" ? "opus" : container == "mp4" ? "aac" : null, null, item.Duration);
    }
}
