namespace Aperture.Core.Library;

/// <summary>
/// Decides which folders may become library folders. Aperture indexes only folders the user
/// picks, and refuses picks that would sweep up the whole computer (a drive root, the
/// Windows or program folders, the whole user profile) or Aperture's own data.
/// </summary>
public static class LibraryFolderRules
{
    /// <summary>
    /// Returns null when <paramref name="folder"/> can be added, or a sentence explaining why not.
    /// </summary>
    /// <param name="folder">The folder the user picked.</param>
    /// <param name="protectedFolders">System folders that may not be added or contain the pick
    /// (Windows, Program Files, ProgramData, AppData). The user profile root is passed in
    /// <paramref name="profileFolder"/> instead because folders inside it are fine.</param>
    /// <param name="profileFolder">The user's profile folder, which may not be added as a whole.</param>
    /// <param name="existing">Folders already in the library.</param>
    /// <param name="dataFolder">Aperture's own data folder.</param>
    public static string? Check(
        string folder,
        IEnumerable<string> protectedFolders,
        string? profileFolder,
        IEnumerable<string> existing,
        string? dataFolder)
    {
        ArgumentNullException.ThrowIfNull(protectedFolders);
        ArgumentNullException.ThrowIfNull(existing);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "Choose a folder.";
        }

        var pick = Normalize(folder);
        if (IsRoot(pick))
        {
            return "Aperture doesn’t index whole drives. Choose the folder that holds your videos.";
        }

        if (profileFolder is not null && Same(pick, Normalize(profileFolder)))
        {
            return "That’s your whole user folder. Choose the folder that holds your videos, such as Videos.";
        }

        foreach (var system in protectedFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Normalize))
        {
            if (Same(pick, system) || IsInside(pick, system) || IsInside(system, pick))
            {
                return "That folder belongs to Windows or installed programs, so Aperture won’t index it.";
            }
        }

        if (dataFolder is not null && (Same(pick, Normalize(dataFolder)) || IsInside(pick, Normalize(dataFolder)) || IsInside(Normalize(dataFolder), pick)))
        {
            return "That folder holds Aperture’s own data.";
        }

        foreach (var other in existing.Select(Normalize))
        {
            if (Same(pick, other))
            {
                return "That folder is already in your library.";
            }

            if (IsInside(pick, other))
            {
                return "That folder is already part of your library, inside another library folder.";
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="folder"/> (not equal to it).</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return p.Length > f.Length
            && p.StartsWith(f, StringComparison.OrdinalIgnoreCase)
            && (f.EndsWith('/') || p[f.Length] == '/');
    }

    /// <summary>Forward slashes, no trailing slash (except a root), for comparisons.</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var text = path.Trim().Replace('\\', '/');
        while (text.Length > 1 && text.EndsWith('/') && !IsRoot(text))
        {
            text = text[..^1];
        }

        return text;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsRoot(string normalized)
    {
        var text = normalized.TrimEnd('/');
        if (text.Length == 0)
        {
            return true; // "/"
        }

        // "C:" or "C:/"
        if (text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
        {
            return true;
        }

        // "//server/share"
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return text.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
        }

        return false;
    }
}
