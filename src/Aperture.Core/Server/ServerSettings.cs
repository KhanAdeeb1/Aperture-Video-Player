namespace Aperture.Core.Server;

/// <summary>
/// How the media server behaves. Stored in Aperture's database and read by both the desktop
/// app (which edits it) and the server process (which follows it).
/// </summary>
/// <param name="Port">HTTPS port for the web client and the media it plays.</param>
/// <param name="SetupPort">Plain HTTP port that only serves the phone setup page and Aperture's certificate.</param>
/// <param name="AllowVpn">Also accept devices on a private network such as Tailscale (100.64.0.0/10). Off by default.</param>
/// <param name="KeepAwake">Ask Windows not to sleep from idle while a device is playing (lid close and the power button still sleep).</param>
/// <param name="FfmpegPath">FFmpeg chosen by the user, or null to look for it next to the server and on PATH.</param>
/// <param name="MaxConversions">How many files may be converted at once.</param>
/// <param name="StartWithWindows">Start the server when the user signs in to Windows.</param>
public sealed record ServerSettings(
    int Port = 8443,
    int SetupPort = 8080,
    bool AllowVpn = false,
    bool KeepAwake = true,
    string? FfmpegPath = null,
    int MaxConversions = 1,
    bool StartWithWindows = false)
{
    /// <summary>Defaults for a first launch: local network only, nothing shared.</summary>
    public static ServerSettings Default { get; } = new();

    /// <summary>The choices offered for <see cref="MaxConversions"/>.</summary>
    public static IReadOnlyList<int> ConversionChoices { get; } = [1, 2, 3];

    /// <summary>Returns a copy with out-of-range values replaced by defaults.</summary>
    public ServerSettings Normalized() => this with
    {
        Port = IsUsablePort(Port) ? Port : Default.Port,
        SetupPort = IsUsablePort(SetupPort) && SetupPort != Port ? SetupPort : (Port == Default.SetupPort ? Default.Port : Default.SetupPort),
        FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim(),
        MaxConversions = ConversionChoices.Contains(MaxConversions) ? MaxConversions : Default.MaxConversions,
    };

    /// <summary>Ports from 1024 up, so the server never needs administrator rights.</summary>
    public static bool IsUsablePort(int port) => port is >= 1024 and <= 65535;
}

/// <summary>A phone, tablet or browser signed in to the media server.</summary>
/// <param name="Id">Database id.</param>
/// <param name="Name">What the device called itself when it signed in ("iPhone · Safari").</param>
/// <param name="CreatedUtc">When it signed in.</param>
/// <param name="LastSeenUtc">Its last request.</param>
/// <param name="ExpiresUtc">When the sign-in ends unless the device is used again.</param>
/// <param name="LastAddress">The network address of its last request, for recognising it.</param>
public sealed record RemoteDevice(long Id, string Name, DateTimeOffset CreatedUtc, DateTimeOffset LastSeenUtc, DateTimeOffset ExpiresUtc, string? LastAddress);

/// <summary>The media server's records in Aperture's database. Nothing here touches media files.</summary>
public interface IServerStore
{
    Task<ServerSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(ServerSettings settings, CancellationToken cancellationToken = default);

    /// <summary>The stored passphrase hash (see <see cref="Passphrase"/>), or null when none is set.</summary>
    Task<string?> GetPassphraseHashAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores a new passphrase hash and signs every device out, so an old passphrase stops working everywhere.</summary>
    Task SetPassphraseHashAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>Ids of the library folders the user shared with the server.</summary>
    Task<IReadOnlySet<long>> GetSharedFolderIdsAsync(CancellationToken cancellationToken = default);

    Task SetFolderSharedAsync(long folderId, bool isShared, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a new sign-in. Only the token's hash is stored.</summary>
    Task<RemoteDevice> AddDeviceAsync(string tokenHash, string name, DateTimeOffset nowUtc, DateTimeOffset expiresUtc, string? address, CancellationToken cancellationToken = default);

    /// <summary>The device whose token has this hash and has not expired, or null.</summary>
    Task<RemoteDevice?> FindDeviceAsync(string tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Records a request from the device and extends its sign-in.</summary>
    Task TouchDeviceAsync(long deviceId, DateTimeOffset nowUtc, DateTimeOffset expiresUtc, string? address, CancellationToken cancellationToken = default);

    /// <summary>Signs one device out.</summary>
    Task RemoveDeviceAsync(long deviceId, CancellationToken cancellationToken = default);

    /// <summary>Signs every device out.</summary>
    Task RemoveAllDevicesAsync(CancellationToken cancellationToken = default);
}
