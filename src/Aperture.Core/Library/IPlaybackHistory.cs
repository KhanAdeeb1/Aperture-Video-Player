namespace Aperture.Core.Library;

/// <summary>Aperture's record of files the user has played.</summary>
public interface IPlaybackHistory
{
    /// <summary>All remembered files, most recently played first.</summary>
    Task<IReadOnlyList<HistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The entry for one file, or null when it has never been played.</summary>
    Task<HistoryEntry?> FindAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates the entry for a file.</summary>
    Task SaveAsync(HistoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Forgets one file. The media file itself is not touched.</summary>
    Task RemoveAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Forgets every file. Media files are not touched.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
