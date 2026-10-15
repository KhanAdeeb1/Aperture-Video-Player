using System.Reflection;
using System.Runtime.InteropServices;
using Aperture.Core.Diagnostics;
using Aperture.Playback.Mpv.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace Aperture.App.Services;

/// <summary>
/// Puts a diagnostic report on the clipboard for the user to paste into a bug report: versions,
/// the problem shown, and the end of Aperture's log, with file paths and the user name removed.
/// Nothing is sent anywhere; the user decides where the text goes.
/// </summary>
internal static class DiagnosticClipboard
{
    /// <summary>
    /// Builds the report and copies it. <paramref name="mediaPath"/> is the file the problem is
    /// about: its name is removed too, wherever it appears (messages quote it on its own).
    /// Returns the text copied.
    /// </summary>
    public static string Copy(string? problem, IEnumerable<(string Label, string? Value)>? extraFacts = null, string? mediaPath = null)
    {
        var report = DiagnosticReport.Build(
            Facts().Concat(extraFacts ?? []),
            problem,
            AppLog.ReadTail(DiagnosticReport.LogLines),
            Environment.UserName);
        if (Path.GetFileName(mediaPath) is { Length: > 0 } name)
        {
            report = report.Replace(name, "<file>" + Path.GetExtension(name), StringComparison.OrdinalIgnoreCase);
        }

        var package = new DataPackage();
        package.SetText(report);
        Clipboard.SetContent(package);
        AppLog.Info("A diagnostic report was copied to the clipboard.");
        return report;
    }

    private static IEnumerable<(string, string?)> Facts()
    {
        yield return ("Aperture", typeof(DiagnosticClipboard).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        yield return ("Windows", Environment.OSVersion.VersionString);
        yield return (".NET", RuntimeInformation.FrameworkDescription);
        yield return ("Processor", RuntimeInformation.ProcessArchitecture.ToString());
        string? engine;
        try
        {
            engine = $"libmpv client API {MpvLibrary.ApiVersion}";
        }
        catch (MpvUnavailableException error)
        {
            engine = $"unavailable: {error.Message}";
        }

        yield return ("Engine", engine);
    }
}
