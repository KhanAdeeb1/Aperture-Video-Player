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

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) =>
        AppLog.Error("Unhandled exception", e.Exception);
}
