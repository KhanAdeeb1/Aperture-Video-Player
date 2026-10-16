using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aperture.Core.Casting;

namespace Aperture.Core.Server;

/// <summary>
/// What a running server writes to <c>Server\server.json</c> in Aperture's data folder, so the
/// desktop app and the command line can find and control it. The control token only works from
/// this computer (the control address answers loopback requests only).
/// </summary>
/// <param name="ProcessId">The server's process id.</param>
/// <param name="Port">HTTPS port.</param>
/// <param name="SetupPort">Setup and control port (plain HTTP).</param>
/// <param name="ControlToken">Random token for the control requests, new for every start.</param>
/// <param name="StartedUtc">When it started.</param>
public sealed record ServerStateFile(int ProcessId, int Port, int SetupPort, string ControlToken, DateTimeOffset StartedUtc)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads the file, or null when it is missing or unreadable.</summary>
    public static ServerStateFile? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ServerStateFile>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the file, readable by the current user only where the platform allows saying so.</summary>
    public void Write(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>A running server's own account of itself, from its control address.</summary>
/// <param name="Running">Always true when the server answered.</param>
/// <param name="Addresses">Addresses devices can use ("https://192.168.1.20:8443").</param>
/// <param name="SetupAddresses">The phone setup page on each address.</param>
/// <param name="CertificateFingerprint">SHA-256 of Aperture's certificate authority, as phones show it.</param>
/// <param name="HasPassphrase">False until a passphrase is set; no device can sign in before.</param>
/// <param name="SharedFolders">How many folders are shared.</param>
/// <param name="ActiveStreams">Devices playing right now.</param>
/// <param name="Converter">"FFmpeg 7.1 (NVIDIA NVENC)", or null when FFmpeg isn't available.</param>
/// <param name="ConverterProblem">Why FFmpeg can't be used, when it can't.</param>
/// <param name="KeepingAwake">True while the server asks Windows not to sleep.</param>
public sealed record ServerStatus(
    bool Running,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> SetupAddresses,
    string CertificateFingerprint,
    bool HasPassphrase,
    int SharedFolders,
    int ActiveStreams,
    string? Converter,
    string? ConverterProblem,
    bool KeepingAwake);

/// <summary>Talks to a server running on this computer through its loopback control address.</summary>
public sealed class ServerControlClient(HttpClient http)
{
    /// <summary>Header that carries <see cref="ServerStateFile.ControlToken"/>.</summary>
    public const string TokenHeader = "X-Aperture-Control";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>How control requests and answers are written (the server uses the same).</summary>
    public static JsonSerializerOptions SerializerOptions => Json;

    /// <summary>The server's status, or null when it doesn't answer (not running, starting, or the file is stale).</summary>
    public async Task<ServerStatus?> GetStatusAsync(ServerStateFile state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{state.SetupPort}/control/status");
        request.Headers.Add(TokenHeader, state.ControlToken);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ServerStatus>(Json, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Asks the server to stop. True when it accepted.</summary>
    public async Task<bool> StopAsync(ServerStateFile state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{state.SetupPort}/control/stop");
        request.Headers.Add(TokenHeader, state.ControlToken);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
    /// <summary>
    /// TVs and other renderers on the home network. <paramref name="search"/> looks again
    /// (a few seconds); otherwise the server answers with what it found last. Null when the server doesn't answer.
    /// </summary>
    public async Task<IReadOnlyList<CastRenderer>?> GetRenderersAsync(ServerStateFile state, bool search, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(state, HttpMethod.Get, search ? "cast/renderers?search=1" : "cast/renderers", null, cancellationToken).ConfigureAwait(false);
        return response is { IsSuccessStatusCode: true }
            ? await ReadAsync<List<CastRenderer>>(response, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>The current or last cast, or null when the server doesn't answer.</summary>
    public async Task<CastStatus?> GetCastStatusAsync(ServerStateFile state, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(state, HttpMethod.Get, "cast/status", null, cancellationToken).ConfigureAwait(false);
        return response is { IsSuccessStatusCode: true }
            ? await ReadAsync<CastStatus>(response, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>Starts casting, replacing any cast in progress. Returns the new status, or why it couldn't start.</summary>
    public async Task<(CastStatus? Status, string? Problem)> StartCastAsync(ServerStateFile state, CastStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendAsync(state, HttpMethod.Post, "cast/start", JsonContent.Create(request, options: Json), cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return (null, "The media server isn't answering.");
        }

        if (response.IsSuccessStatusCode)
        {
            return (await ReadAsync<CastStatus>(response, cancellationToken).ConfigureAwait(false), null);
        }

        var problem = await ReadAsync<CastProblem>(response, cancellationToken).ConfigureAwait(false);
        return (null, problem?.Error ?? "The media server couldn't start casting.");
    }

    /// <summary>Play, pause or stop the cast. Returns false when the server doesn't answer or there is no cast.</summary>
    public async Task<bool> CastCommandAsync(ServerStateFile state, CastCommand command, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(state, HttpMethod.Post, "cast/" + command.ToString().ToLowerInvariant(), null, cancellationToken).ConfigureAwait(false);
        return response is { IsSuccessStatusCode: true };
    }

    /// <summary>Moves the cast to <paramref name="seconds"/> into the file.</summary>
    public async Task<bool> CastSeekAsync(ServerStateFile state, double seconds, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(state, HttpMethod.Post, "cast/seek", JsonContent.Create(new CastSeek(seconds), options: Json), cancellationToken).ConfigureAwait(false);
        return response is { IsSuccessStatusCode: true };
    }

    private async Task<HttpResponseMessage?> SendAsync(ServerStateFile state, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{state.SetupPort}/control/{path}") { Content = content };
        request.Headers.Add(TokenHeader, state.ControlToken);
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            return default;
        }
    }

    private sealed record CastProblem(string? Error);
}

/// <summary>Transport commands for a cast.</summary>
public enum CastCommand
{
    Play,
    Pause,
    Stop,
}

/// <summary>Body of a seek request.</summary>
/// <param name="Seconds">Where to go in the file.</param>
public sealed record CastSeek(double Seconds);
