using System.Text;
using Aperture.Core.Library;
using Aperture.Core.Server;
using Aperture.Core.Series;
using Aperture.Data;

namespace Aperture.Server.Library;

/// <summary>One card in the web client.</summary>
public sealed record CardDto(
    long Id,
    string Title,
    string? Subtitle,
    double? DurationSeconds,
    double Progress,
    double ResumeSeconds,
    bool Watched,
    string? Quality,
    string Kind,
    string? Note,
    bool HasArtwork);

/// <summary>A show on the library page.</summary>
public sealed record ShowDto(string Id, string Title, int EpisodeCount, int SeasonCount, long ImageItemId, string? Note);

/// <summary>The library page.</summary>
public sealed record LibraryDto(
    IReadOnlyList<CardDto> Continue,
    IReadOnlyList<CardDto> Movies,
    IReadOnlyList<ShowDto> Shows,
    IReadOnlyList<CardDto> Personal,
    IReadOnlyList<CardDto> Podcasts,
    int SharedFolders);

/// <summary>A season of a show.</summary>
public sealed record SeasonDto(int Number, string Label, IReadOnlyList<CardDto> Episodes);

/// <summary>A show's page.</summary>
public sealed record ShowDetailDto(string Id, string Title, CardDto? Next, IReadOnlyList<SeasonDto> Seasons);

/// <summary>One item's page.</summary>
public sealed record ItemDetailDto(CardDto Card, string? Overview, string? ShowId, string? ShowTitle, long? NextId, long? PreviousId);

/// <summary>
/// The part of the library the user shared with the server, and what has been watched. Built
/// only from shared items, so nothing outside the shared folders (not even a show's other
/// episodes) is ever described to a device.
/// </summary>
public sealed class SharedLibrary(
    SqliteLibraryStore library,
    IServerStore server,
    IPlaybackHistory history,
    IItemStateStore itemState,
    TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan SnapshotLife = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snapshot;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// The item with this id when it is in a shared folder and present, otherwise null. Always
    /// read fresh, so un-sharing a folder takes effect on the very next request.
    /// </summary>
    public async Task<LibraryItem?> FindSharedAsync(long id, CancellationToken cancellationToken)
    {
        var item = await library.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var shared = await SharedFoldersAsync(cancellationToken).ConfigureAwait(false);
        return SharedMedia.IsShared(item, shared) ? item : null;
    }

    public async Task<LibraryDto> GetLibraryAsync(CancellationToken cancellationToken)
    {
        var s = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var continuing = ContinueWatching.Build(s.History.Values, s.Catalog, s.Overrides)
            .Select(c => s.Items.GetValueOrDefault(c.Path) is { } item ? Card(s, item, c.IsUpNext ? "Up next" : null) : null)
            .OfType<CardDto>()
            .ToList();

        var movies = s.Groups.Where(g => g.Best.Category == MediaCategory.Movie)
            .Select(g => Card(s, g.Best, null))
            .OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var shows = s.Catalog.All
            .Select(show => new ShowDto(
                ShowId(show.Key),
                show.Title,
                show.Episodes.Count,
                show.Seasons.Count,
                show.Episodes[0].Item.Id,
                WatchedNote(s, show)))
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var personal = s.Groups.Where(g => g.Best.Category == MediaCategory.Personal)
            .OrderByDescending(g => g.Best.Date?.ToDateTime(TimeOnly.MinValue) ?? g.Best.ModifiedUtc.UtcDateTime)
            .Select(g => Card(s, g.Best, null))
            .ToList();
        var podcasts = s.Groups.Where(g => g.Best.Category == MediaCategory.Podcast)
            .OrderByDescending(g => g.Best.Date?.ToDateTime(TimeOnly.MinValue) ?? g.Best.ModifiedUtc.UtcDateTime)
            .Select(g => Card(s, g.Best, null))
            .ToList();
        return new LibraryDto(continuing, movies, shows, personal, podcasts, s.SharedFolderCount);
    }

    public async Task<ShowDetailDto?> GetShowAsync(string id, CancellationToken cancellationToken)
    {
        var s = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var show = s.Catalog.All.FirstOrDefault(x => ShowId(x.Key) == id);
        if (show is null)
        {
            return null;
        }

        var seasons = show.Episodes
            .GroupBy(e => e.IsSpecial ? 0 : e.Season)
            .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)
            .Select(g => new SeasonDto(g.Key, g.Key == 0 ? "Specials" : $"Season {g.Key}", [.. g.Select(e => Card(s, e.Item, null, e.Label))]))
            .ToList();
        var next = ContinueWatching.Build(s.History.Values, s.Catalog, s.Overrides, int.MaxValue)
            .FirstOrDefault(c => c.Episode?.SeriesKey == show.Key);
        var nextCard = next is not null && s.Items.GetValueOrDefault(next.Path) is { } nextItem
            ? Card(s, nextItem, next.IsUpNext ? "Up next" : "Continue", s.Catalog.EpisodeOf(nextItem.Path)?.Label)
            : null;
        return new ShowDetailDto(id, show.Title, nextCard, seasons);
    }

    public async Task<ItemDetailDto?> GetItemAsync(long id, CancellationToken cancellationToken)
    {
        var item = await FindSharedAsync(id, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var s = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var show = s.Catalog.SeriesOf(item.Path);
        var episode = s.Catalog.EpisodeOf(item.Path);
        var next = s.Catalog.Next(item.Path)?.PreferredCopy(item.Path);
        var previous = s.Catalog.Previous(item.Path)?.PreferredCopy(item.Path);
        return new ItemDetailDto(
            Card(s, item, null, episode?.Label),
            item.Overview,
            show is null ? null : ShowId(show.Key),
            show?.Title,
            next?.Id,
            previous?.Id);
    }

    /// <summary>Forgets the cached snapshot (after a device saved progress).</summary>
    public void Invalidate() => _snapshot = null;

    /// <summary>Shared folder ids and paths, read fresh.</summary>
    public async Task<IReadOnlyDictionary<long, string>> SharedFoldersAsync(CancellationToken cancellationToken)
    {
        var ids = await server.GetSharedFolderIdsAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var folders = await library.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        return folders.Where(f => ids.Contains(f.Id)).ToDictionary(f => f.Id, f => f.Path);
    }

    /// <summary>An opaque, URL-safe id for a show.</summary>
    public static string ShowId(string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CardDto Card(Snapshot s, LibraryItem item, string? note, string? episodeLabel = null)
    {
        var entry = s.History.GetValueOrDefault(item.Path);
        var state = WatchStatus.Of(entry, s.Overrides.TryGetValue(item.Path, out var marked) ? marked : null);
        var duration = item.Duration ?? (entry?.Duration > TimeSpan.Zero ? entry.Duration : null);
        var resume = state == WatchState.InProgress ? WatchProgress.ResumePosition(entry) : TimeSpan.Zero;
        var subtitle = item.Category == MediaCategory.Episode && episodeLabel is not null
            ? item.EpisodeTitle is { Length: > 0 } name ? $"{episodeLabel} · {name}" : episodeLabel
            : item.Subtitle;

        return new CardDto(
            item.Id,
            item.Title,
            subtitle,
            duration?.TotalSeconds,
            state == WatchState.Watched ? 1 : entry?.Progress ?? 0,
            resume.TotalSeconds,
            state == WatchState.Watched,
            item.Quality,
            item.Category.ToString().ToLowerInvariant(),
            note,
            item.ArtworkPath is not null);
    }

    private static string? WatchedNote(Snapshot s, SeriesInfo show)
    {
        var watched = show.Episodes.Count(e => WatchStatus.Of(s.History.GetValueOrDefault(e.Item.Path), s.Overrides.TryGetValue(e.Item.Path, out var m) ? m : null) == WatchState.Watched);
        var count = show.Episodes.Count == 1 ? "1 episode" : $"{show.Episodes.Count} episodes";
        return watched == 0 ? count : watched == show.Episodes.Count ? $"{count}, all watched" : $"{count}, {watched} watched";
    }

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;
        var now = clock.GetUtcNow();
        if (current is not null && now - current.Taken < SnapshotLife)
        {
            return current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot is { } fresh && now - fresh.Taken < SnapshotLife)
            {
                return fresh;
            }

            var shared = await SharedFoldersAsync(cancellationToken).ConfigureAwait(false);
            var items = shared.Count == 0
                ? []
                : (await library.GetItemsAsync(cancellationToken).ConfigureAwait(false)).Where(i => SharedMedia.IsShared(i, shared)).ToList();
            var byPath = items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var groups = CopyGroup.Build(items);
            var catalog = SeriesCatalog.Build(groups);
            var historyByPath = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in await history.GetAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (byPath.ContainsKey(entry.Path))
                {
                    historyByPath.TryAdd(entry.Path, entry);
                }
            }

            var overrides = (await itemState.GetWatchedOverridesAsync(cancellationToken).ConfigureAwait(false))
                .Where(p => byPath.ContainsKey(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            _snapshot = new Snapshot(now, byPath, groups, catalog, historyByPath, overrides, shared.Count);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Snapshot(
        DateTimeOffset Taken,
        IReadOnlyDictionary<string, LibraryItem> Items,
        IReadOnlyList<CopyGroup> Groups,
        SeriesCatalog Catalog,
        IReadOnlyDictionary<string, HistoryEntry> History,
        IReadOnlyDictionary<string, bool> Overrides,
        int SharedFolderCount);
}
