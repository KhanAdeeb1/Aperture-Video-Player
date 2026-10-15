using Aperture.Core.Playback;

namespace Aperture.Core.Library;

/// <summary>What a scan learned about one file, before it is inspected.</summary>
/// <param name="Path">Full path.</param>
/// <param name="SizeBytes">File size.</param>
/// <param name="ModifiedUtc">Last-modified time.</param>
/// <param name="Parsed">What the name says.</param>
public sealed record ScannedFile(string Path, long SizeBytes, DateTimeOffset ModifiedUtc, ParsedName Parsed);

/// <summary>The facts a scan compares to decide whether a file changed.</summary>
public sealed record IndexedFile(long Id, string Path, long SizeBytes, DateTimeOffset ModifiedUtc, bool Probed);

/// <summary>What inspecting a file found. Any part may be unknown.</summary>
public sealed record MediaProbeResult(TimeSpan? Duration, int? Width, int? Height, string? VideoCodec);

/// <summary>Aperture's library records. Every operation reads or writes Aperture's database only, never media files.</summary>
public interface ILibraryStore
{
    Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);

    Task<LibraryFolder> AddFolderAsync(string path, LibraryFolderKind kind, CancellationToken cancellationToken = default);

    /// <summary>Forgets a folder and its items. Files on disk are not touched.</summary>
    Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default);

    Task MarkScanStartedAsync(long folderId, DateTimeOffset startedUtc, CancellationToken cancellationToken = default);

    Task MarkScanCompletedAsync(long folderId, DateTimeOffset completedUtc, CancellationToken cancellationToken = default);

    /// <summary>Every file indexed under a folder, including missing ones, for comparing with disk.</summary>
    Task<IReadOnlyList<IndexedFile>> GetIndexAsync(long folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds new files and updates changed ones (one row per path, so repeated scans never
    /// duplicate). Details the user entered by hand are kept.
    /// </summary>
    Task UpsertAsync(long folderId, IReadOnlyList<ScannedFile> files, DateTimeOffset seenUtc, CancellationToken cancellationToken = default);

    /// <summary>Records that unchanged files are still there.</summary>
    Task TouchAsync(IReadOnlyList<long> itemIds, DateTimeOffset seenUtc, CancellationToken cancellationToken = default);

    /// <summary>After a complete scan: marks items not seen since <paramref name="scanStartedUtc"/> as missing, and returns how many.</summary>
    Task<int> MarkMissingAsync(long folderId, DateTimeOffset scanStartedUtc, CancellationToken cancellationToken = default);

    /// <summary>Stores what inspecting a file found.</summary>
    Task SaveProbeAsync(long itemId, MediaProbeResult? probe, string? fingerprint, CancellationToken cancellationToken = default);

    /// <summary>All items that are present (not missing).</summary>
    Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken = default);

    Task<LibraryItem?> FindByPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Items still to be inspected, oldest first.</summary>
    Task<IReadOnlyList<IndexedFile>> GetUnprobedAsync(long folderId, CancellationToken cancellationToken = default);

    /// <summary>Items that have not been looked up online yet (and were not corrected by hand).</summary>
    Task<IReadOnlyList<LibraryItem>> GetUnmatchedAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Applies an online match (when certain enough) or keeps it as a suggestion. Never touches manual details.</summary>
    Task SaveOnlineMatchAsync(long itemId, OnlineMatch? match, bool apply, string? artworkPath, CancellationToken cancellationToken = default);

    /// <summary>Saves details the user corrected. These win over scans and online lookups from then on.</summary>
    Task SaveManualDetailsAsync(long itemId, ManualDetails details, CancellationToken cancellationToken = default);

    /// <summary>Drops manual and online details and goes back to what the file name says.</summary>
    Task ResetDetailsAsync(long itemId, ParsedName parsed, CancellationToken cancellationToken = default);
}

/// <summary>Details the user typed in the "Edit details" dialog.</summary>
public sealed record ManualDetails(MediaCategory Category, string Title, int? Year, int? Season, int? Episode, string? EpisodeTitle);

/// <summary>Aperture's per-file state outside history: the watched mark and remembered playback choices.</summary>
public interface IItemStateStore
{
    Task<PlaybackPreferences> GetPreferencesAsync(string path, CancellationToken cancellationToken = default);

    Task SavePreferencesAsync(string path, PlaybackPreferences preferences, CancellationToken cancellationToken = default);

    /// <summary>Every "mark as watched/unwatched" choice, keyed by path.</summary>
    Task<IReadOnlyDictionary<string, bool>> GetWatchedOverridesAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets or clears (null) the user's watched mark.</summary>
    Task SetWatchedAsync(string path, bool? watched, CancellationToken cancellationToken = default);
}

/// <summary>Inspects a media file for its length and picture size, without changing it.</summary>
public interface IMediaProbe
{
    /// <summary>Returns what could be learned, or null when the file could not be read as media.</summary>
    Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Where a scan is.</summary>
public enum ScanPhase
{
    /// <summary>Walking folders and reading names.</summary>
    Finding,

    /// <summary>Reading length and picture size from new or changed files.</summary>
    Inspecting,

    /// <summary>Looking up artwork and details online.</summary>
    LookingUp,
}

/// <summary>A progress report from a scan.</summary>
/// <param name="FolderId">The folder being scanned.</param>
/// <param name="Phase">What it is doing.</param>
/// <param name="Done">Files handled so far in this phase.</param>
/// <param name="Total">Files in this phase, or null while still counting.</param>
/// <param name="Added">New items so far.</param>
public sealed record ScanProgress(long FolderId, ScanPhase Phase, int Done, int? Total, int Added)
{
    /// <summary>"Finding videos… 1,240 so far", "Reading details… 40 of 312".</summary>
    public string Describe() => Phase switch
    {
        ScanPhase.Finding => $"Finding videos… {Done:N0} so far",
        ScanPhase.Inspecting when Total is { } total => $"Reading video details… {Done:N0} of {total:N0}",
        ScanPhase.LookingUp when Total is { } total => $"Looking up artwork… {Done:N0} of {total:N0}",
        ScanPhase.Inspecting => "Reading video details…",
        _ => "Looking up artwork…",
    };

    /// <summary>Fraction done, or null when the total is not known.</summary>
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Done / Total.Value, 0, 1) : null;
}

/// <summary>How a scan ended.</summary>
/// <param name="Completed">True when every file was looked at; false when cancelled.</param>
/// <param name="Added">New items.</param>
/// <param name="Updated">Items whose file changed.</param>
/// <param name="Missing">Items whose file has gone (complete scans only).</param>
/// <param name="Problems">Files or folders that could not be read.</param>
/// <param name="FolderUnavailable">True when the folder itself could not be reached (a drive unplugged); nothing was marked missing.</param>
public sealed record ScanSummary(bool Completed, int Added, int Updated, int Missing, int Problems, bool FolderUnavailable = false);
