namespace Aperture.Core.Storage;

/// <summary>
/// Resolves where Aperture keeps its own per-user data (database, logs).
/// User media is never stored here; this folder holds only Aperture's records.
/// </summary>
public sealed class AppDataLocation
{
    /// <summary>Environment variable that redirects the data folder, for portable use and tests.</summary>
    public const string OverrideVariable = "APERTURE_DATA_DIR";

    private const string FolderName = "Aperture";

    private AppDataLocation(string root)
    {
        Root = root;
    }

    /// <summary>Absolute path of the data folder.</summary>
    public string Root { get; }

    /// <summary>Path of the SQLite database file.</summary>
    public string DatabasePath => Path.Combine(Root, "aperture.db");

    /// <summary>Folder for rolling log files.</summary>
    public string LogsDirectory => Path.Combine(Root, "Logs");

    /// <summary>Artwork downloaded for library items (only when online details are turned on).</summary>
    public string ArtworkDirectory => Path.Combine(Root, "Artwork");

    /// <summary>Sound fingerprints and picture measurements of episodes, used to find intros and credits.</summary>
    public string AnalysisDirectory => Path.Combine(Root, "Analysis");

    /// <summary>
    /// Resolves the data folder from the override variable when set, otherwise
    /// from the user's local application data folder (%LOCALAPPDATA%\Aperture on Windows).
    /// </summary>
    public static AppDataLocation Resolve() =>
        Resolve(
            Environment.GetEnvironmentVariable(OverrideVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>A data folder at an absolute path, for tests and tools that run several at once.</summary>
    public static AppDataLocation At(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The data folder must be an absolute path.", nameof(root));
        }

        return new AppDataLocation(Path.GetFullPath(root));
    }

    internal static AppDataLocation Resolve(string? overrideRoot, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            if (!Path.IsPathFullyQualified(overrideRoot))
            {
                throw new ArgumentException(
                    $"{OverrideVariable} must be an absolute path, but was '{overrideRoot}'.",
                    nameof(overrideRoot));
            }

            return new AppDataLocation(Path.GetFullPath(overrideRoot));
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The local application data folder could not be determined for this user.");
        }

        return new AppDataLocation(Path.Combine(localAppData, FolderName));
    }

    /// <summary>Creates the data and log folders if they do not exist yet.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
