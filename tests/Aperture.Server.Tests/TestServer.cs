using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Aperture.Core.Library;
using Aperture.Core.Server;
using Aperture.Core.Storage;
using Aperture.Data;
using Aperture.Server.Hosting;
using Microsoft.Data.Sqlite;

namespace Aperture.Server.Tests;

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The real media server on loopback with its own data folder, database and media folder.
/// Clients talk HTTPS to it and trust only the server's own certificate authority.
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    public const string Passphrase = "lantern river 42";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _folder;

    private TestServer(string folder, AppDataLocation data, ApertureDatabase database, ManualClock clock)
    {
        _folder = folder;
        Data = data;
        Database = database;
        Clock = clock;
        Store = new SqliteServerStore(database);
        Library = new SqliteLibraryStore(database);
        History = new SqlitePlaybackHistory(database);
        MediaFolder = Path.Combine(folder, "media");
        Directory.CreateDirectory(MediaFolder);
    }

    public AppDataLocation Data { get; }

    public ApertureDatabase Database { get; }

    public ManualClock Clock { get; }

    public SqliteServerStore Store { get; }

    public SqliteLibraryStore Library { get; }

    public SqlitePlaybackHistory History { get; }

    public string MediaFolder { get; }

    public MediaServer Server { get; private set; } = null!;

    public ServerContext Context => Server.Context;

    public Uri Address => new($"https://127.0.0.1:{Server.Port}/");

    public Uri SetupAddress => new($"http://127.0.0.1:{Server.SetupPort}/");

    /// <summary>Creates the data folder; <paramref name="seed"/> runs before the server starts.</summary>
    /// <param name="useFfmpeg">False points the server at an FFmpeg that doesn't exist, so it behaves as if none is installed.</param>
    public static async Task<TestServer> StartAsync(Func<TestServer, Task>? seed = null, bool useFfmpeg = false, bool withPassphrase = true)
    {
        var folder = Path.Combine(Path.GetTempPath(), "aperture-server-tests-" + Guid.NewGuid().ToString("N"));
        var data = AppDataLocation.At(Path.Combine(folder, "data"));
        data.EnsureCreated();
        var database = new ApertureDatabase(data.DatabasePath);
        await database.MigrateAsync();
        var test = new TestServer(folder, data, database, new ManualClock(DateTimeOffset.UtcNow));
        if (!useFfmpeg)
        {
            await test.Store.SaveSettingsAsync(ServerSettings.Default with { FfmpegPath = Path.Combine(folder, "no-ffmpeg", "ffmpeg") });
        }

        if (withPassphrase)
        {
            // Few iterations keep the tests quick; the stored value says how many were used.
            await test.Store.SetPassphraseHashAsync(Core.Server.Passphrase.Hash(Passphrase, 1000));
        }

        if (seed is not null)
        {
            await seed(test);
        }

        test.Server = await MediaServer.StartAsync(new MediaServerOptions
        {
            Data = data,
            UseWindowsCertificateStore = false,
            BindAddress = IPAddress.Loopback,
            PortOverride = 0,
            SetupPortOverride = 0,
            Clock = test.Clock,
        });
        return test;
    }

    /// <summary>Adds a library folder under the media folder, optionally shared.</summary>
    public async Task<LibraryFolder> AddFolderAsync(string name, bool shared, LibraryFolderKind kind = LibraryFolderKind.Movies)
    {
        var path = Path.Combine(MediaFolder, name);
        Directory.CreateDirectory(path);
        var folder = await Library.AddFolderAsync(path, kind);
        await Store.SetFolderSharedAsync(folder.Id, shared);
        return folder;
    }

    /// <summary>Writes a file (random bytes unless given) and indexes it as a movie.</summary>
    public async Task<LibraryItem> AddItemAsync(LibraryFolder folder, string fileName, string title, byte[]? content = null, TimeSpan? duration = null, string? videoCodec = null)
    {
        var path = Path.Combine(folder.Path, fileName);
        if (content is not null)
        {
            await File.WriteAllBytesAsync(path, content);
        }
        else if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(path, RandomBytes(64 * 1024));
        }

        var info = new FileInfo(path);
        await Library.UpsertAsync(folder.Id, [new ScannedFile(path, info.Length, info.LastWriteTimeUtc, new ParsedName(MediaCategory.Movie, title, 2021, Confidence: MatchConfidence.Certain))], Clock.GetUtcNow());
        var item = (await Library.FindByPathAsync(path))!;
        await Library.SaveProbeAsync(item.Id, new MediaProbeResult(duration ?? TimeSpan.FromMinutes(90), 1920, 1080, videoCodec), "test");
        return (await Library.FindByPathAsync(path))!;
    }

    public static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>An HTTPS client that trusts only this server's authority, keeps cookies and sends the request header the server requires.</summary>
    public HttpClient Client(bool withHeader = true)
    {
        var authority = Context.Certificates.Authority;
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && ChainsTo(certificate, authority),
        };
        var client = new HttpClient(handler) { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(60) };
        if (withHeader)
        {
            client.DefaultRequestHeaders.Add(ServerContext.CsrfHeader, "1");
        }

        return client;
    }

    /// <summary>A plain HTTP client for the setup port.</summary>
    public HttpClient SetupClient() => new(new HttpClientHandler { UseProxy = false }) { BaseAddress = SetupAddress, Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A signed-in client.</summary>
    public async Task<HttpClient> SignedInClientAsync(string name = "Test phone")
    {
        var client = Client();
        using var response = await client.PostAsJsonAsync("api/session", new { passphrase = Passphrase, deviceName = name });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public static bool ChainsTo(X509Certificate2 certificate, X509Certificate2 authority)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    /// <summary>What a phone sends when asking to play: H.264 and AAC, with or without HLS.</summary>
    public static object PlayRequest(bool hls = true, double? start = null, bool avoidDirectPlay = false, long? bandwidth = null) => new
    {
        video = new[] { "h264" },
        audio = new[] { "aac", "mp3" },
        hls,
        webM = false,
        bandwidthBps = bandwidth,
        startSeconds = start,
        avoidDirectPlay,
    };

    /// <summary>True when ffmpeg and ffprobe are on PATH (the conversion tests need them).</summary>
    public static bool HasFfmpeg { get; } = CanRun("ffmpeg") && CanRun("ffprobe");

    private static bool CanRun(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Makes a short test clip with FFmpeg's built-in test sources.</summary>
    public static void MakeClip(string path, string videoCodec, string audioCodec, int seconds = 8, int height = 360)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={height * 16 / 9}x{height}:rate=25:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            "-c:v", videoCodec, "-g", "50", "-c:a", audioCodec, "-shortest", path,
        })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg couldn't make the test clip: {errors}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A conversion may still be releasing a file; the temp folder is cleaned by the system later.
        }
    }
}

/// <summary>A test that needs FFmpeg and FFprobe; reported as skipped (not passed) when they aren't installed.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!TestServer.HasFfmpeg)
        {
            Skip = "FFmpeg and FFprobe aren't installed on this machine.";
        }
    }
}
