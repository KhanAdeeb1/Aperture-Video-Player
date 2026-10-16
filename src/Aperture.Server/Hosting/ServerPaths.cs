using Aperture.Core.Storage;

namespace Aperture.Server.Hosting;

/// <summary>
/// Where the server keeps its own files, all under Aperture's data folder. Media files are only
/// ever read; conversions and thumbnails are written here, never next to the media.
/// </summary>
public sealed class ServerPaths
{
    public ServerPaths(AppDataLocation data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Root = Path.Combine(data.Root, "Server");
    }

    public AppDataLocation Data { get; }

    /// <summary>The server's folder.</summary>
    public string Root { get; }

    /// <summary>Written while the server runs; see <see cref="Core.Server.ServerStateFile"/>.</summary>
    public string StateFile => Path.Combine(Root, "server.json");

    /// <summary>Held open while the server runs, so a second copy can't start.</summary>
    public string LockFile => Path.Combine(Root, "server.lock");

    /// <summary>The certificate authority (non-Windows only; Windows keeps it in the user's certificate store).</summary>
    public string CertificateAuthorityFile => Path.Combine(Root, "ca.pfx");

    /// <summary>Converted streams in progress. Emptied when the server starts and stops.</summary>
    public string ConversionsDirectory => Path.Combine(Root, "Conversions");

    /// <summary>Frames taken from videos that have no artwork.</summary>
    public string ThumbnailsDirectory => Path.Combine(Root, "Thumbnails");

    /// <summary>The server's log, next to the app's.</summary>
    public string LogFile => Path.Combine(Data.LogsDirectory, "server.log");

    public void EnsureCreated()
    {
        Data.EnsureCreated();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ConversionsDirectory);
        Directory.CreateDirectory(ThumbnailsDirectory);
    }
}
