using Aperture.Core.Library;
using Aperture.Core.Settings;
using Aperture.Core.Storage;
using Aperture.Data;

namespace Aperture.App.Services;

/// <summary>The application's services, created once at startup (the composition root).</summary>
internal sealed record AppServices(
    AppDataLocation DataLocation,
    IPlaybackHistory History,
    ISettingsStore SettingsStore,
    AppSettings InitialSettings)
{
    /// <summary>
    /// Opens the data folder and database. A failure here is reported to the user by the
    /// main window rather than crashing, because nothing about it is the user's media.
    /// </summary>
    public static async Task<StartupResult> CreateAsync()
    {
        AppDataLocation? location = null;
        try
        {
            location = AppDataLocation.Resolve();
            location.EnsureCreated();
            AppLog.Initialize(location);

            var database = new ApertureDatabase(location.DatabasePath);
            await database.MigrateAsync().ConfigureAwait(true);

            var settingsStore = new SqliteSettingsStore(database);
            var settings = await settingsStore.LoadAsync().ConfigureAwait(true);
            return new StartupResult(new AppServices(location, new SqlitePlaybackHistory(database), settingsStore, settings), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Error("Startup failed", error);
            return new StartupResult(null, new StartupFailure(error.Message, location?.Root));
        }
    }
}

/// <summary>Either the services, or why they could not be created.</summary>
internal sealed record StartupResult(AppServices? Services, StartupFailure? Failure);

/// <summary>A startup problem shown in place of the home screen.</summary>
internal sealed record StartupFailure(string Message, string? DataFolder);
