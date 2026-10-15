using Aperture.Core.Server;
using Aperture.Core.Storage;
using Aperture.Data;
using Aperture.Server.Hosting;

namespace Aperture.Server;

/// <summary>
/// <c>Aperture.Server</c> runs the media server in the background, separate from the desktop
/// app. The same commands the app uses are available on the command line:
/// <code>
///   Aperture.Server                 run the server (the default)
///   Aperture.Server run --background  run without writing to the console (how the desktop app starts it)
///   Aperture.Server status          is it running, and where
///   Aperture.Server stop            stop the running server
///   Aperture.Server set-passphrase  read a new passphrase from the console (signs every device out)
///   Aperture.Server folders         list library folders and whether each is shared
///   Aperture.Server share ID | unshare ID
///   Aperture.Server devices         list signed-in devices
///   Aperture.Server sign-out ID|all sign devices out
///   Aperture.Server ports HTTPS SETUP  choose the ports (from 1024 up), used from the next start
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var data = AppDataLocation.Resolve();
        var paths = new ServerPaths(data);
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "run";
        try
        {
            return command switch
            {
                "run" => await RunAsync(paths, background: args.Skip(1).Contains("--background", StringComparer.OrdinalIgnoreCase)),
                "status" => await StatusAsync(paths),
                "stop" => await StopAsync(paths),
                "set-passphrase" => await SetPassphraseAsync(data),
                "folders" => await FoldersAsync(data),
                "share" or "unshare" when args.Length > 1 && long.TryParse(args[1], out var id) => await ShareAsync(data, id, command == "share"),
                "devices" => await DevicesAsync(data),
                "sign-out" when args.Length > 1 => await SignOutAsync(data, args[1]),
                "ports" when args.Length > 2 && int.TryParse(args[1], out var port) && int.TryParse(args[2], out var setupPort) => await PortsAsync(data, port, setupPort),
                _ => Usage(),
            };
        }
        catch (ServerStartException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 3;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("Usage: Aperture.Server [run | status | stop | set-passphrase | folders | share ID | unshare ID | devices | sign-out ID|all | ports HTTPS SETUP]");
        return 2;
    }

    private static async Task<int> RunAsync(ServerPaths paths, bool background)
    {
        paths.EnsureCreated();
        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            await Console.Error.WriteLineAsync("The media server is already running.");
            return 1;
        }

        await using (instanceLock)
        {
            await using var server = await MediaServer.StartAsync(new MediaServerOptions { Data = paths.Data, ConsoleLog = !background });
            var state = new ServerStateFile(Environment.ProcessId, server.Port, server.SetupPort, server.Context.ControlToken, DateTimeOffset.UtcNow);
            state.Write(paths.StateFile);
            if (!background)
            {
                Console.WriteLine($"Aperture media server is running on port {server.Port}. Phone setup: http://<this computer>:{server.SetupPort}/");
            }

            try
            {
                await server.WaitForShutdownAsync();
            }
            finally
            {
                File.Delete(paths.StateFile);
            }
        }

        return 0;
    }

    private static async Task<int> StatusAsync(ServerPaths paths)
    {
        var state = ServerStateFile.Read(paths.StateFile);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var status = state is null ? null : await new ServerControlClient(http).GetStatusAsync(state);
        if (status is null)
        {
            Console.WriteLine("The media server isn't running.");
            return 1;
        }

        Console.WriteLine("The media server is running.");
        foreach (var address in status.Addresses)
        {
            Console.WriteLine($"  {address}");
        }

        Console.WriteLine(status.SetupAddresses.Count > 0
            ? $"Phone setup: {string.Join(", ", status.SetupAddresses)}"
            : "This computer has no home-network address right now, so devices can't reach it. Is it connected to Wi-Fi or Ethernet?");
        Console.WriteLine($"Certificate fingerprint (SHA-256): {status.CertificateFingerprint}");
        Console.WriteLine($"Shared folders: {status.SharedFolders}. Playing now: {status.ActiveStreams}.");
        Console.WriteLine(status.Converter is { } c ? $"Conversion: {c}" : $"Conversion: unavailable. {status.ConverterProblem}");
        if (!status.HasPassphrase)
        {
            Console.WriteLine("No passphrase is set, so no device can sign in yet. Run: Aperture.Server set-passphrase");
        }

        return 0;
    }

    private static async Task<int> StopAsync(ServerPaths paths)
    {
        var state = ServerStateFile.Read(paths.StateFile);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        if (state is null || !await new ServerControlClient(http).StopAsync(state))
        {
            Console.WriteLine("The media server isn't running.");
            return 1;
        }

        Console.WriteLine("The media server is stopping.");
        return 0;
    }

    private static async Task<int> SetPassphraseAsync(AppDataLocation data)
    {
        Console.Write("New passphrase: ");
        var first = ReadHidden();
        Console.Write("Again: ");
        var second = ReadHidden();
        if (first != second)
        {
            await Console.Error.WriteLineAsync("The two didn't match. Nothing was changed.");
            return 1;
        }

        if (Passphrase.Check(first) is { } problem)
        {
            await Console.Error.WriteLineAsync(problem);
            return 1;
        }

        var store = await OpenStoreAsync(data);
        await store.SetPassphraseHashAsync(Passphrase.Hash(first!));
        Console.WriteLine("Passphrase set. Every device has been signed out.");
        return 0;
    }

    private static async Task<int> FoldersAsync(AppDataLocation data)
    {
        var database = await OpenDatabaseAsync(data);
        var shared = await new SqliteServerStore(database).GetSharedFolderIdsAsync();
        foreach (var folder in await new SqliteLibraryStore(database).GetFoldersAsync())
        {
            Console.WriteLine($"{folder.Id,4}  {(shared.Contains(folder.Id) ? "shared " : "private")}  {folder.Path}");
        }

        return 0;
    }

    private static async Task<int> ShareAsync(AppDataLocation data, long folderId, bool share)
    {
        var store = await OpenStoreAsync(data);
        await store.SetFolderSharedAsync(folderId, share);
        Console.WriteLine(share ? $"Folder {folderId} is shared." : $"Folder {folderId} is no longer shared.");
        return 0;
    }

    private static async Task<int> DevicesAsync(AppDataLocation data)
    {
        var store = await OpenStoreAsync(data);
        foreach (var device in await store.GetDevicesAsync())
        {
            Console.WriteLine($"{device.Id,4}  {device.Name}  last used {device.LastSeenUtc.ToLocalTime():g}");
        }

        return 0;
    }

    private static async Task<int> SignOutAsync(AppDataLocation data, string which)
    {
        var store = await OpenStoreAsync(data);
        if (which == "all")
        {
            await store.RemoveAllDevicesAsync();
        }
        else if (long.TryParse(which, out var id))
        {
            await store.RemoveDeviceAsync(id);
        }
        else
        {
            return Usage();
        }

        Console.WriteLine("Signed out.");
        return 0;
    }

    private static async Task<int> PortsAsync(AppDataLocation data, int port, int setupPort)
    {
        if (!ServerSettings.IsUsablePort(port) || !ServerSettings.IsUsablePort(setupPort) || port == setupPort)
        {
            await Console.Error.WriteLineAsync("Choose two different ports from 1024 to 65535.");
            return 1;
        }

        var store = await OpenStoreAsync(data);
        var settings = await store.LoadSettingsAsync();
        await store.SaveSettingsAsync(settings with { Port = port, SetupPort = setupPort });
        Console.WriteLine($"Ports set to {port} (HTTPS) and {setupPort} (setup). They take effect the next time the server starts.");
        return 0;
    }

    private static async Task<ApertureDatabase> OpenDatabaseAsync(AppDataLocation data)
    {
        data.EnsureCreated();
        var database = new ApertureDatabase(data.DatabasePath);
        await database.MigrateAsync();
        return database;
    }

    private static async Task<SqliteServerStore> OpenStoreAsync(AppDataLocation data) => new(await OpenDatabaseAsync(data));

    private static string? ReadHidden()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
