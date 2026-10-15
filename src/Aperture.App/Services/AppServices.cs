using Aperture.App.Interop;
using Aperture.Core.Input;
using Aperture.Core.Library;
using Aperture.Core.Series;
using Aperture.Core.Settings;
using Aperture.Core.Storage;
using Aperture.Data;
using Aperture.Media;
using Aperture.Media.Analysis;
using Aperture.Playback.Mpv;
using Aperture.Playback.Mpv.Interop;
using Aperture.YouTube;

namespace Aperture.App.Services;

/// <summary>The settings in effect right now, shared by the window and the background library work.</summary>
internal sealed class LiveSettings(AppSettings initial)
{
    private AppSettings _current = initial;

    public AppSettings Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}

/// <summary>The application's services, created once at startup (the composition root).</summary>
internal sealed record AppServices(
    AppDataLocation DataLocation,
    IPlaybackHistory History,
    ISettingsStore SettingsStore,
    IShortcutStore ShortcutStore,
    IItemStateStore ItemState,
    ISecretStore Secrets,
    LibraryCoordinator Library,
    ISeriesStore Series,
    SeriesState Shows,
    EpisodeAnalysisCoordinator Analysis,
    LiveSettings Settings,
    ShortcutMap InitialShortcuts,
    YouTubeService YouTube)
{
    /// <summary>The Credential Manager entry holding the user's TMDB key.</summary>
    public const string TmdbKeyName = "TMDB";

    // One client for the whole app, as .NET recommends; only used when online details are on.
    private static readonly HttpClient Http = CreateHttpClient();

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
            var settings = new LiveSettings(await settingsStore.LoadAsync().ConfigureAwait(true));
            var shortcuts = ShortcutMap.FromStorage(await settingsStore.LoadShortcutsAsync().ConfigureAwait(true));
            var history = new SqlitePlaybackHistory(database);
            var itemState = new SqliteItemStateStore(database);
            var libraryStore = new SqliteLibraryStore(database);
            var secrets = new WindowsCredentialStore();
            var artworkFolder = location.ArtworkDirectory;

            LibraryLookup? Lookup() =>
                settings.Current.OnlineMetadata && secrets.Read(TmdbKeyName) is { Length: > 0 } key
                    ? new LibraryLookup(libraryStore, new TmdbMetadataProvider(Http, key), new ArtworkCache(Http, artworkFolder), AppLog.FromEngine)
                    : null;

            var library = new LibraryCoordinator(libraryStore, itemState, history, CreateProbe, Lookup, TimeProvider.System, AppLog.FromEngine);
            var series = new SqliteSeriesStore(database);
            var analysisFolder = location.AnalysisDirectory;
            var analysis = new EpisodeAnalysisCoordinator(series, () => CreateSignalReader(analysisFolder), new AnalysisCache(analysisFolder), TimeProvider.System, AppLog.FromEngine);
            var youTube = CreateYouTube(database, secrets);
            return new StartupResult(
                new AppServices(location, history, settingsStore, settingsStore, itemState, secrets, library, series, new SeriesState(library), analysis, settings, shortcuts, youTube),
                null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Error("Startup failed", error);
            return new StartupResult(null, new StartupFailure(error.Message, location?.Root));
        }
    }

    /// <summary>
    /// The YouTube section's service. It shares nothing with local playback except the database
    /// file (its own table) and the credential store (its own entries), so the local player works
    /// the same with or without YouTube.
    /// </summary>
    private static YouTubeService CreateYouTube(ApertureDatabase database, WindowsCredentialStore secrets)
    {
        var account = new YouTubeAccount(secrets, new GoogleOAuth(Http, TimeProvider.System), TimeProvider.System);
        return new YouTubeService(
            new SqliteYouTubeStore(database),
            new YouTubeDataApi(Http),
            new YouTubeOEmbed(Http),
            account,
            secrets,
            TimeProvider.System,
            ViewerRegion(),
            AppLog.FromEngine);
    }

    /// <summary>The Windows region setting (ISO 3166-1 alpha-2), used to rank YouTube results and check regional limits.</summary>
    private static string? ViewerRegion()
    {
        try
        {
            var code = new Windows.Globalization.GeographicRegion().CodeTwoLetter;
            return code is { Length: 2 } && code != "ZZ" ? code : null;
        }
        catch (Exception error) when (error is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>The library's file inspector; without libmpv the library still works from file names.</summary>
    private static MpvMediaProbe? CreateProbe()
    {
        try
        {
            return MpvMediaProbe.Create(AppLog.FromEngine);
        }
        catch (MpvUnavailableException error)
        {
            AppLog.Warning("Library files can't be inspected because libmpv could not start", error);
            return null;
        }
    }

    /// <summary>Reads episodes for intro and credits detection; without libmpv, nothing is analysed.</summary>
    private static MpvSignalReader? CreateSignalReader(string workFolder)
    {
        try
        {
            MpvLibrary.EnsureLoaded();
            return new MpvSignalReader(workFolder, AppLog.FromEngine);
        }
        catch (MpvUnavailableException error)
        {
            AppLog.Warning("Intros and credits can't be found because libmpv could not start", error);
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var version = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Aperture/{version}");
        return client;
    }
}

/// <summary>Either the services, or why they could not be created.</summary>
internal sealed record StartupResult(AppServices? Services, StartupFailure? Failure);

/// <summary>A startup problem shown in place of the home screen.</summary>
internal sealed record StartupFailure(string Message, string? DataFolder);
