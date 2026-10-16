using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Hosting;

/// <summary>
/// Writes the server's log to <c>Logs\server.log</c> in Aperture's data folder, starting a new
/// file when it passes 2 MB (one older file is kept). The server never logs passphrases,
/// tokens, request addresses or media paths; items are named by id.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly string _path;
    private readonly Lock _gate = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        // Each write opens and closes the file, so there is nothing to release.
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var shortCategory = category[(category.LastIndexOf('.') + 1)..];
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {Level(level)} {shortCategory}: {message}");
        if (exception is not null)
        {
            line += Environment.NewLine + "    " + exception.GetType().FullName + ": " + exception.Message;
        }

        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // The log is best effort; the console still has the line.
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
