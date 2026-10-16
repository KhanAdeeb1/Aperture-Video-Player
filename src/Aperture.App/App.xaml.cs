using Aperture.App.Services;
using Microsoft.UI.Xaml;

namespace Aperture.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        AppLog.InitializeEarly();
        UnhandledException += OnUnhandledException;
        try
        {
            InitializeComponent();
        }
        catch (Exception error)
        {
            AppLog.Error("The application resources could not be loaded", error);
            throw;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Unpackaged apps receive files as command-line arguments ("Open with", drag onto the exe).
        var commandLine = Environment.GetCommandLineArgs();

        // At sign-in (Settings › Media server › Start with Windows): start the server, show nothing, exit.
        if (commandLine.Skip(1).Contains(MediaServerHost.StartArgument, StringComparer.OrdinalIgnoreCase))
        {
            await StartMediaServerAndExitAsync();
            return;
        }

        var fileToOpen = commandLine.Length > 1 ? commandLine[1] : null;

        var startup = await AppServices.CreateAsync();
        try
        {
            _window = new MainWindow(startup);
        }
        catch (Exception error)
        {
            AppLog.Error("The main window could not be created", error);
            throw;
        }

        _window.Activate();

        if (startup.Services is not null && !string.IsNullOrWhiteSpace(fileToOpen))
        {
            await _window.OpenFileAsync(fileToOpen);
        }
    }

    private async Task StartMediaServerAndExitAsync()
    {
        try
        {
            var location = Aperture.Core.Storage.AppDataLocation.Resolve();
            location.EnsureCreated();
            AppLog.Initialize(location);
            if (await new MediaServerHost(location).StartAsync() is { } problem)
            {
                AppLog.Warning($"The media server didn't start at sign-in: {problem}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error("The media server couldn't be started at sign-in", error);
        }

        Exit();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) =>
        AppLog.Error("Unhandled exception", e.Exception);
}
