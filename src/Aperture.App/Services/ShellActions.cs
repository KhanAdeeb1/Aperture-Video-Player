using System.Diagnostics;
using System.ComponentModel;

namespace Aperture.App.Services;

/// <summary>Hands things to Windows: Explorer for folders. Never modifies files.</summary>
internal static class ShellActions
{
    /// <summary>Opens Explorer with the file selected, or its folder if the file is gone.</summary>
    public static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            {
                Start("explorer.exe", $"\"{folder}\"");
            }
        }
        catch (Win32Exception error)
        {
            AppLog.Error("Could not open Explorer", error);
        }
    }

    /// <summary>Opens a folder in Explorer.</summary>
    public static void OpenFolder(string folder)
    {
        try
        {
            Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Win32Exception error)
        {
            AppLog.Error("Could not open Explorer", error);
        }
    }

    private static void Start(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
    }
}
