using System.Diagnostics;
using System.Globalization;
using Aperture.Core.Storage;

namespace Aperture.App.Services;

/// <summary>
/// One line per event in Logs\aperture.log and in the debugger output. File paths of media
/// are not written. When the log passes 2 MB it is moved to aperture.old.log and a new one
/// started, so it never grows without limit (Milestone 9).
/// </summary>
internal static class AppLog
{
    private const long MaximumBytes = 2 * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _logFile;

    /// <summary>The current log file, once known.</summary>
    public static string? LogFile => _logFile;

    /// <summary>The last <paramref name="count"/> lines of the log (empty when it can't be read).</summary>
    public static IReadOnlyList<string> ReadTail(int count)
    {
        var file = _logFile;
        if (file is null)
        {
            return [];
        }

        try
        {
            lock (Gate)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // The tail is in the last 64 KB; reading less keeps this quick for a full log.
                stream.Seek(Math.Max(0, stream.Length - (64 * 1024)), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
                return lines.Count > count ? lines[^count..] : lines;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not read the log file: {error.Message}");
            return [];
        }
    }

    public static void Initialize(AppDataLocation location) =>
        _logFile = Path.Combine(location.LogsDirectory, "aperture.log");

    /// <summary>
    /// Points the log at the data folder before anything else runs, so failures while
    /// loading the interface itself are recorded. Problems here are ignored on purpose:
    /// startup reports them properly a moment later in <see cref="AppServices.CreateAsync"/>.
    /// </summary>
    public static void InitializeEarly()
    {
        try
        {
            var location = AppDataLocation.Resolve();
            location.EnsureCreated();
            Initialize(location);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine($"Early log setup failed: {error.Message}");
        }
    }

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Info(string message) => Write("INFO", message, null);

    /// <summary>Routes a playback-engine message ("error", "warn", "info"...) to the matching level.</summary>
    public static void FromEngine(string level, string message)
    {
        switch (level)
        {
            case "fatal" or "error":
                Error(message);
                break;
            case "warn":
                Warning(message);
                break;
            case "info":
                Info(message);
                break;
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{(exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message} (0x{exception.HResult:X8}){(level == "ERROR" ? Environment.NewLine + exception : string.Empty)}")}");
        Debug.WriteLine(line);

        var file = _logFile;
        if (file is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaximumBytes)
                {
                    File.Move(file, Path.ChangeExtension(file, ".old.log"), overwrite: true);
                }

                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch (IOException ioError)
        {
            Debug.WriteLine($"Could not write to the log file: {ioError.Message}");
        }
        catch (UnauthorizedAccessException accessError)
        {
            Debug.WriteLine($"Could not write to the log file: {accessError.Message}");
        }
    }
}
