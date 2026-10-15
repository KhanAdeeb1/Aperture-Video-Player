using System.Net;
using Aperture.Core.Server;
using Aperture.Core.Storage;
using Aperture.Data;
using Aperture.Server.Library;
using Aperture.Server.Media;
using Aperture.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Hosting;

/// <summary>How to run the server. The defaults are what the real process uses.</summary>
public sealed class MediaServerOptions
{
    public required AppDataLocation Data { get; init; }

    /// <summary>Keep the certificate authority in the Windows certificate store (Windows only).</summary>
    public bool UseWindowsCertificateStore { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Listen on this address only (tests use loopback); null listens on every address.</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>Ports to use instead of the settings (0 picks a free one, for tests).</summary>
    public int? PortOverride { get; init; }

    public int? SetupPortOverride { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Also log to the console (the command line), not only to the log file.</summary>
    public bool ConsoleLog { get; init; }
}

/// <summary>
/// The media server: HTTPS for the web client and media, plain HTTP for the setup page and the
/// loopback control requests. Every request is first checked against <see cref="NetworkAccess"/>.
/// </summary>
public sealed class MediaServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ServerCertificates _certificates;
    private readonly KeepAwake _keepAwake;
    private readonly ConversionManager _conversions;

    private MediaServer(WebApplication app, ServerContext context, ServerCertificates certificates, KeepAwake keepAwake, ConversionManager conversions)
    {
        _app = app;
        Context = context;
        _certificates = certificates;
        _keepAwake = keepAwake;
        _conversions = conversions;
    }

    public ServerContext Context { get; }

    public int Port => Context.Port;

    public int SetupPort => Context.SetupPort;

    /// <summary>Completes when the server has been asked to stop (control request, Ctrl+C, or <see cref="StopAsync"/>).</summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => _app.WaitForShutdownAsync(cancellationToken);

    public Task StopAsync() => _app.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
        await _conversions.DisposeAsync().ConfigureAwait(false);
        _keepAwake.Dispose();
        _certificates.Dispose();
        Context.Library.Dispose();
        Context.Ffmpeg.Dispose();
        Context.Thumbnails.Dispose();
    }

    public static async Task<MediaServer> StartAsync(MediaServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var paths = new ServerPaths(options.Data);
        paths.EnsureCreated();

        var database = new ApertureDatabase(options.Data.DatabasePath);
        await database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        var store = new SqliteServerStore(database);
        var settings = await store.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(paths.LogFile));
        if (options.ConsoleLog)
        {
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        }

        // ASP.NET Core's own request logs would record addresses that contain stream tokens.
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        var authority = new CertificateAuthority(paths.CertificateAuthorityFile, options.UseWindowsCertificateStore).LoadOrCreate();
        var certificates = new ServerCertificates(authority);
        var port = options.PortOverride ?? settings.Port;
        var setupPort = options.SetupPortOverride ?? settings.SetupPort;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
            void Secure(ListenOptions o)
            {
                o.Protocols = HttpProtocols.Http1AndHttp2;
                o.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificates.Current);
            }

            void Plain(ListenOptions o) => o.Protocols = HttpProtocols.Http1;
            if (options.BindAddress is { } bind)
            {
                kestrel.Listen(bind, port, Secure);
                kestrel.Listen(bind, setupPort, Plain);
            }
            else
            {
                kestrel.ListenAnyIP(port, Secure);
                kestrel.ListenAnyIP(setupPort, Plain);
            }
        });

        var app = builder.Build();
        var loggers = app.Services.GetRequiredService<ILoggerFactory>();
        var log = loggers.CreateLogger("Aperture.Server");
        var clock = options.Clock;
        var libraryStore = new SqliteLibraryStore(database, clock);
        var history = new SqlitePlaybackHistory(database);
        var ffmpeg = new FfmpegTools(loggers.CreateLogger<FfmpegTools>());
        var conversions = new ConversionManager(paths.ConversionsDirectory, clock, loggers.CreateLogger<ConversionManager>());
        var activity = new PlaybackActivity(clock);
        var context = new ServerContext(
            paths,
            store,
            new SharedLibrary(libraryStore, store, history, new SqliteItemStateStore(database), clock),
            libraryStore,
            history,
            new SqliteSettingsStore(database),
            ffmpeg,
            conversions,
            new Thumbnails(paths.ThumbnailsDirectory, options.Data.ArtworkDirectory, ffmpeg, loggers.CreateLogger<Thumbnails>()),
            activity,
            new LoginThrottle(clock),
            certificates,
            clock);
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        context.RequestStop = lifetime.StopApplication;
        var keepAwake = new KeepAwake(activity, () => context.SettingsAsync(CancellationToken.None).GetAwaiter().GetResult().KeepAwake, loggers.CreateLogger<KeepAwake>());
        context.KeepingAwake = () => keepAwake.Active;

        Configure(app, context, log);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await conversions.DisposeAsync().ConfigureAwait(false);
            keepAwake.Dispose();
            certificates.Dispose();
            context.Library.Dispose();
            ffmpeg.Dispose();
            context.Thumbnails.Dispose();
            await app.DisposeAsync().ConfigureAwait(false);
            log.LogError("The server could not start: port {Port} or {SetupPort} is in use", port, setupPort);
            throw new ServerStartException($"Port {port} or {setupPort} is already in use by another program. Choose other ports in Aperture's settings (Media server › Ports).", ex);
        }

        var ports = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(a => new Uri(a.Replace("[::]", "localhost", StringComparison.Ordinal).Replace("0.0.0.0", "localhost", StringComparison.Ordinal)))
            .ToList();
        context.Port = ports.First(u => u.Scheme == "https").Port;
        context.SetupPort = ports.First(u => u.Scheme == "http").Port;
        log.LogInformation(
            "Media server started on ports {Port} (HTTPS) and {SetupPort} (setup); {Folders} shared folders; private networks {Vpn}",
            context.Port,
            context.SetupPort,
            (await store.GetSharedFolderIdsAsync(cancellationToken).ConfigureAwait(false)).Count,
            settings.AllowVpn ? "allowed" : "not allowed");
        return new MediaServer(app, context, certificates, keepAwake, conversions);
    }

    private static void Configure(WebApplication app, ServerContext server, ILogger log)
    {
        var refused = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        // 1. Only this computer, the home network, and (when allowed) a private network.
        app.Use(async (http, next) =>
        {
            var settings = await server.SettingsAsync(http.RequestAborted);
            var remote = server.RemoteAddress(http);
            if (!NetworkAccess.IsAllowed(remote, settings.AllowVpn))
            {
                var zone = remote is null ? "unknown" : NetworkAccess.Classify(remote).ToString();
                if (refused.TryAdd(zone, 0))
                {
                    log.LogWarning("Refused a connection from a {Zone} address; only the local network{Vpn} may connect", zone, settings.AllowVpn ? " and private networks" : string.Empty);
                }

                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(http);
        });

        // 2. The setup port serves the setup page and control requests only.
        app.Use(async (http, next) =>
        {
            if (http.Connection.LocalPort == server.SetupPort || !http.Request.IsHttps)
            {
                await SetupEndpoints.HandleAsync(http, server, log);
                return;
            }

            await next(http);
        });

        // 3. Security headers, and a header no other site's form can send on changing requests.
        app.Use(async (http, next) =>
        {
            var headers = http.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self'; script-src 'self'; connect-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            var path = http.Request.Path;
            if (path.StartsWithSegments("/api", StringComparison.Ordinal))
            {
                headers.CacheControl = "no-store";
            }

            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && http.Request.Headers[ServerContext.CsrfHeader] != "1")
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(http);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        });

        ApiEndpoints.Map(app, server, log);
        MediaEndpoints.Map(app, server);
    }
}

/// <summary>The server could not start; the message says why in the user's terms.</summary>
public sealed class ServerStartException(string message, Exception inner) : Exception(message, inner);
