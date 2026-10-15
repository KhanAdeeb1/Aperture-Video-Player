using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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
}
