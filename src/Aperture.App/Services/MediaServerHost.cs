using System.Diagnostics;
using Aperture.Core.Server;
using Aperture.Core.Storage;
using Microsoft.Win32;

namespace Aperture.App.Services;

/// <summary>
/// Starts, stops and asks after the media server, which runs as its own process
/// (<c>Server\Aperture.Server.exe</c> next to Aperture.exe). Closing Aperture leaves it running;
/// it stops only when asked to, here or with <c>Aperture.Server stop</c>.
/// </summary>
internal sealed class MediaServerHost(AppDataLocation data)
{
    /// <summary>Argument that makes Aperture.exe start the server and exit without a window (used at sign-in).</summary>
    public const string StartArgument = "--media-server";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Aperture media server";

    // Only ever talks to 127.0.0.1, so no proxy.
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    // The last server this app started; kept so its error output can still be read.
    private Process? _started;

    /// <summary>The server program shipped with the app.</summary>
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "Server", "Aperture.Server.exe");

    /// <summary>False in builds without the server (it is published next to the app by the App project).</summary>
    public static bool IsInstalled => File.Exists(ExecutablePath);

    /// <summary>The server's log, for "Details are in…" messages.</summary>
    public string LogFile => Path.Combine(data.LogsDirectory, "server.log");

    private string StateFile => Path.Combine(data.Root, "Server", "server.json");

    /// <summary>The running server's status, or null when it isn't running (or doesn't answer).</summary>
    public async Task<ServerStatus?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ServerStateFile.Read(StateFile) is { } state
            ? await new ServerControlClient(Http).GetStatusAsync(state, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Starts the server in the background and waits until it answers. Returns why it didn't start, or null.</summary>
    public async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            return "This copy of Aperture doesn't include the media server.";
        }

        if (await GetStatusAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
        };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--background");

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("The process didn't start.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Could not start the media server", error);
            return $"The media server couldn't be started: {error.Message}";
        }

        // In the background the server writes nothing to the console unless it can't start.
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        _started = process;
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var text = (await errors.ConfigureAwait(false)).Trim();
                AppLog.Warning($"The media server stopped as it started (exit code {process.ExitCode}): {text}");
                return text.Length > 0 ? text : $"The media server stopped as it started. Details are in {LogFile}.";
            }

            if (await GetStatusAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return $"The media server started but didn't answer. Details are in {LogFile}.";
    }

    /// <summary>Asks the running server to stop and waits until it has. Returns why it didn't, or null.</summary>
    public async Task<string?> StopAsync(CancellationToken cancellationToken = default)
    {
        if (ServerStateFile.Read(StateFile) is not { } state || !await new ServerControlClient(Http).StopAsync(state, cancellationToken).ConfigureAwait(false))
        {
            return null; // not running
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessRunning(state.ProcessId))
            {
                return null;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return "The media server is taking a long time to stop. It finishes the requests in progress first.";
    }

    private static bool IsProcessRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when Windows starts the server at sign-in (a value under the user's Run key; nothing system-wide).</summary>
    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
    }

    /// <summary>Adds or removes the sign-in entry. Only changed when the user flips the setting.</summary>
    public static void SetStartsWithWindows(bool start)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (start)
        {
            var app = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Aperture.exe");
            key.SetValue(RunValueName, $"\"{app}\" {StartArgument}");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }
}
