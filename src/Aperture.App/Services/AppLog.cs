using System.Diagnostics;
using System.Globalization;
using Aperture.Core.Storage;

namespace Aperture.App.Services;

/// <summary>
/// Minimal error log for Milestone 1: one line per event in Logs\aperture.log and in the
/// debugger output. File paths of media are not written. A structured, rolling logger
/// replaces this in a later milestone (see docs/architecture.md).
/// </summary>
internal static class AppLog
{
    private static readonly Lock Gate = new();
    private static string? _logFile;

    public static void Initialize(AppDataLocation location) =>
        _logFile = Path.Combine(location.LogsDirectory, "aperture.log");

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{(exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message} (0x{exception.HResult:X8})")}");
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
