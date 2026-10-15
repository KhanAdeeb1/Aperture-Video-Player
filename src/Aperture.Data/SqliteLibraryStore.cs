using System.Globalization;
using Aperture.Core.Library;
using Microsoft.Data.Sqlite;

namespace Aperture.Data;

/// <summary>
/// Library folders and items in SQLite. Rows are keyed by file path (case-insensitive), so
/// a file is indexed at most once however many times it is scanned. Only Aperture's own
/// database is written; media files are never opened here.
/// </summary>
public sealed class SqliteLibraryStore(ApertureDatabase database, TimeProvider? clock = null) : ILibraryStore
{
    private const string ItemColumns = """
        id, folder_id, path, size_bytes, modified_utc, added_utc, missing, category, title, year, season, episode,
        episode_title, air_date, overview, source, confidence, artwork_path, probed, duration_ms, width, height,
        video_codec, fingerprint, match_service, match_id, match_title, match_year, match_episode_title,
        match_overview, match_artwork_url, match_confidence
        """;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // ===== Folders =====

    public async Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.path, f.kind, f.added_utc, f.last_scan_started_utc, f.last_scan_completed_utc,
                   (SELECT COUNT(*) FROM library_items i WHERE i.folder_id = f.id AND i.missing = 0)
            FROM library_folders f ORDER BY f.added_utc;
            """;
        var folders = new List<LibraryFolder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(new LibraryFolder(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseEnum(reader.GetString(2), LibraryFolderKind.Mixed),
                Time(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : Time(reader.GetInt64(4)),
                reader.IsDBNull(5) ? null : Time(reader.GetInt64(5)),
                reader.GetInt32(6)));
        }

        return folders;
    }

    public async Task<LibraryFolder> AddFolderAsync(string path, LibraryFolderKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var now = _clock.GetUtcNow();
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO library_folders (path, kind, added_utc) VALUES ($path, $kind, $added) RETURNING id;";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$added", now.ToUnixTimeMilliseconds());
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new LibraryFolder(id, path, kind, now, null, null);
    }

    public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM library_items WHERE folder_id = $id;", cancellationToken, ("$id", folderId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM library_folders WHERE id = $id;", cancellationToken, ("$id", folderId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task MarkScanStartedAsync(long folderId, DateTimeOffset startedUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync("UPDATE library_folders SET last_scan_started_utc = $t WHERE id = $id;", cancellationToken, ("$t", startedUtc.ToUnixTimeMilliseconds()), ("$id", folderId));

    public Task MarkScanCompletedAsync(long folderId, DateTimeOffset completedUtc, CancellationToken cancellationToken = default) =>
        ExecuteAsync("UPDATE library_folders SET last_scan_completed_utc = $t WHERE id = $id;", cancellationToken, ("$t", completedUtc.ToUnixTimeMilliseconds()), ("$id", folderId));

    // ===== Scanning =====

    public async Task<IReadOnlyList<IndexedFile>> GetIndexAsync(long folderId, CancellationToken cancellationToken = default) =>
        await ReadIndexAsync("SELECT id, path, size_bytes, modified_utc, probed FROM library_items WHERE folder_id = $id;", folderId, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<IndexedFile>> GetUnprobedAsync(long folderId, CancellationToken cancellationToken = default) =>
        await ReadIndexAsync("SELECT id, path, size_bytes, modified_utc, probed FROM library_items WHERE folder_id = $id AND probed = 0 AND missing = 0 ORDER BY added_utc, id;", folderId, cancellationToken).ConfigureAwait(false);

    public async Task UpsertAsync(long folderId, IReadOnlyList<ScannedFile> files, DateTimeOffset seenUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A changed file is inspected again. Its identity is re-read from the name only when
        // the name was the source; online and hand-entered details are kept.
        command.CommandText = """
            INSERT INTO library_items (folder_id, path, size_bytes, modified_utc, added_utc, last_seen_utc, missing,
                category, title, year, season, episode, episode_title, air_date, source, confidence)
            VALUES ($folder, $path, $size, $modified, $seen, $seen, 0,
                $category, $title, $year, $season, $episode, $episodeTitle, $date, 'FileName', $confidence)
            ON CONFLICT (path) DO UPDATE SET
                folder_id = excluded.folder_id,
                size_bytes = excluded.size_bytes,
                modified_utc = excluded.modified_utc,
                last_seen_utc = excluded.last_seen_utc,
                missing = 0,
                probed = CASE WHEN library_items.size_bytes = excluded.size_bytes AND library_items.modified_utc = excluded.modified_utc THEN library_items.probed ELSE 0 END,
                fingerprint = CASE WHEN library_items.size_bytes = excluded.size_bytes AND library_items.modified_utc = excluded.modified_utc THEN library_items.fingerprint ELSE NULL END,
                category = CASE WHEN library_items.source = 'FileName' THEN excluded.category ELSE library_items.category END,
                title = CASE WHEN library_items.source = 'FileName' THEN excluded.title ELSE library_items.title END,
                year = CASE WHEN library_items.source = 'FileName' THEN excluded.year ELSE library_items.year END,
                season = CASE WHEN library_items.source = 'FileName' THEN excluded.season ELSE library_items.season END,
                episode = CASE WHEN library_items.source = 'FileName' THEN excluded.episode ELSE library_items.episode END,
                episode_title = CASE WHEN library_items.source = 'FileName' THEN excluded.episode_title ELSE library_items.episode_title END,
                air_date = CASE WHEN library_items.source = 'FileName' THEN excluded.air_date ELSE library_items.air_date END,
                confidence = CASE WHEN library_items.source = 'FileName' THEN excluded.confidence ELSE library_items.confidence END;
            """;
        var folder = command.Parameters.Add("$folder", SqliteType.Integer);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var modified = command.Parameters.Add("$modified", SqliteType.Integer);
        var seen = command.Parameters.Add("$seen", SqliteType.Integer);
        var category = command.Parameters.Add("$category", SqliteType.Text);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var year = command.Parameters.Add("$year", SqliteType.Integer);
        var season = command.Parameters.Add("$season", SqliteType.Integer);
        var episode = command.Parameters.Add("$episode", SqliteType.Integer);
        var episodeTitle = command.Parameters.Add("$episodeTitle", SqliteType.Text);
        var date = command.Parameters.Add("$date", SqliteType.Text);
        var confidence = command.Parameters.Add("$confidence", SqliteType.Text);
        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

        foreach (var file in files)
        {
            var parsed = file.Parsed;
            folder.Value = folderId;
            path.Value = file.Path;
            size.Value = file.SizeBytes;
            modified.Value = file.ModifiedUtc.ToUnixTimeMilliseconds();
            seen.Value = seenUtc.ToUnixTimeMilliseconds();
            category.Value = parsed.Category.ToString();
            title.Value = parsed.Title;
            year.Value = (object?)parsed.Year ?? DBNull.Value;
            season.Value = (object?)parsed.Season ?? DBNull.Value;
            episode.Value = (object?)parsed.Episode ?? DBNull.Value;
            episodeTitle.Value = (object?)parsed.EpisodeTitle ?? DBNull.Value;
            date.Value = (object?)parsed.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value;
            confidence.Value = parsed.Confidence.ToString();
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task TouchAsync(IReadOnlyList<long> itemIds, DateTimeOffset seenUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE library_items SET last_seen_utc = $seen, missing = 0 WHERE id = $id;";
        command.Parameters.AddWithValue("$seen", seenUtc.ToUnixTimeMilliseconds());
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);
        foreach (var itemId in itemIds)
        {
            id.Value = itemId;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MarkMissingAsync(long folderId, DateTimeOffset scanStartedUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE library_items SET missing = 1 WHERE folder_id = $id AND missing = 0 AND last_seen_utc < $started;";
        command.Parameters.AddWithValue("$id", folderId);
        command.Parameters.AddWithValue("$started", scanStartedUtc.ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SaveProbeAsync(long itemId, MediaProbeResult? probe, string? fingerprint, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE library_items SET probed = 1, duration_ms = $duration, width = $width, height = $height, video_codec = $codec, fingerprint = $fingerprint WHERE id = $id;",
            cancellationToken,
            ("$duration", probe?.Duration is { } d ? (long)d.TotalMilliseconds : null),
            ("$width", probe?.Width),
            ("$height", probe?.Height),
            ("$codec", probe?.VideoCodec),
            ("$fingerprint", fingerprint),
            ("$id", itemId));

    // ===== Reading =====

    public async Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken = default) =>
        await ReadItemsAsync($"SELECT {ItemColumns} FROM library_items WHERE missing = 0;", cancellationToken).ConfigureAwait(false);

    public async Task<LibraryItem?> FindByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var items = await ReadItemsAsync($"SELECT {ItemColumns} FROM library_items WHERE path = $path;", cancellationToken, ("$path", path)).ConfigureAwait(false);
        return items.Count > 0 ? items[0] : null;
    }

    public async Task<IReadOnlyList<LibraryItem>> GetUnmatchedAsync(int limit, CancellationToken cancellationToken = default) =>
        await ReadItemsAsync(
            $"SELECT {ItemColumns} FROM library_items WHERE missing = 0 AND looked_up_utc IS NULL AND source = 'FileName' AND category IN ('Movie', 'Episode') ORDER BY added_utc DESC LIMIT $limit;",
            cancellationToken,
            ("$limit", limit)).ConfigureAwait(false);

    // ===== Details =====

    public async Task SaveOnlineMatchAsync(long itemId, OnlineMatch? match, bool apply, string? artworkPath, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (match is null)
        {
            await ExecuteAsync("UPDATE library_items SET looked_up_utc = $now WHERE id = $id;", cancellationToken, ("$now", now), ("$id", itemId)).ConfigureAwait(false);
            return;
        }

        if (apply)
        {
            // Episodes keep the show name in title; the match's own title is the show's.
            await ExecuteAsync(
                """
                UPDATE library_items SET
                    looked_up_utc = $now, title = $title, year = COALESCE($year, year),
                    episode_title = COALESCE($episodeTitle, episode_title), overview = $overview,
                    artwork_path = COALESCE($artwork, artwork_path), source = 'Online', confidence = $confidence,
                    match_service = $service, match_id = $serviceId, match_title = NULL, match_year = NULL,
                    match_episode_title = NULL, match_overview = NULL, match_artwork_url = NULL, match_confidence = NULL
                WHERE id = $id AND source <> 'Manual';
                """,
                cancellationToken,
                ("$now", now), ("$title", match.Title), ("$year", match.Year), ("$episodeTitle", match.EpisodeTitle),
                ("$overview", match.Overview), ("$artwork", artworkPath), ("$confidence", match.Confidence.ToString()),
                ("$service", match.Service), ("$serviceId", match.ServiceId), ("$id", itemId)).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(
            """
            UPDATE library_items SET
                looked_up_utc = $now, match_service = $service, match_id = $serviceId, match_title = $title, match_year = $year,
                match_episode_title = $episodeTitle, match_overview = $overview, match_artwork_url = $artworkUrl,
                match_confidence = $confidence
            WHERE id = $id AND source <> 'Manual';
            """,
            cancellationToken,
            ("$now", now), ("$service", match.Service), ("$serviceId", match.ServiceId), ("$title", match.Title),
            ("$year", match.Year), ("$episodeTitle", match.EpisodeTitle), ("$overview", match.Overview),
            ("$artworkUrl", match.ArtworkUrl), ("$confidence", match.Confidence.ToString()), ("$id", itemId)).ConfigureAwait(false);
    }

    public Task SaveManualDetailsAsync(long itemId, ManualDetails details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrWhiteSpace(details.Title, nameof(details));
        return ExecuteAsync(
            """
            UPDATE library_items SET
                category = $category, title = $title, year = $year, season = $season, episode = $episode,
                episode_title = $episodeTitle, source = 'Manual', confidence = 'Certain',
                match_service = NULL, match_id = NULL, match_title = NULL, match_year = NULL, match_episode_title = NULL,
                match_overview = NULL, match_artwork_url = NULL, match_confidence = NULL
            WHERE id = $id;
            """,
            cancellationToken,
            ("$category", details.Category.ToString()), ("$title", details.Title.Trim()), ("$year", details.Year),
            ("$season", details.Category == MediaCategory.Episode ? details.Season : null),
            ("$episode", details.Category is MediaCategory.Episode or MediaCategory.Podcast ? details.Episode : null),
            ("$episodeTitle", string.IsNullOrWhiteSpace(details.EpisodeTitle) ? null : details.EpisodeTitle.Trim()),
            ("$id", itemId));
    }

    public Task ResetDetailsAsync(long itemId, ParsedName parsed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        return ExecuteAsync(
            """
            UPDATE library_items SET
                category = $category, title = $title, year = $year, season = $season, episode = $episode,
                episode_title = $episodeTitle, air_date = $date, overview = NULL, artwork_path = NULL,
                source = 'FileName', confidence = $confidence, looked_up_utc = NULL,
                match_service = NULL, match_id = NULL, match_title = NULL, match_year = NULL, match_episode_title = NULL,
                match_overview = NULL, match_artwork_url = NULL, match_confidence = NULL
            WHERE id = $id;
            """,
            cancellationToken,
            ("$category", parsed.Category.ToString()), ("$title", parsed.Title), ("$year", parsed.Year),
            ("$season", parsed.Season), ("$episode", parsed.Episode), ("$episodeTitle", parsed.EpisodeTitle),
            ("$date", parsed.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$confidence", parsed.Confidence.ToString()), ("$id", itemId));
    }

    // ===== Helpers =====

    private async Task<List<IndexedFile>> ReadIndexAsync(string sql, long folderId, CancellationToken cancellationToken)
    {
        var files = new List<IndexedFile>();
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            files.Add(new IndexedFile(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), Time(reader.GetInt64(3)), reader.GetInt64(4) != 0));
        }

        return files;
    }

    private async Task<List<LibraryItem>> ReadItemsAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var items = new List<LibraryItem>();
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    private static LibraryItem ReadItem(SqliteDataReader r)
    {
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        int? Int(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

        var matchTitle = Text(26);
        return new LibraryItem
        {
            Id = r.GetInt64(0),
            FolderId = r.GetInt64(1),
            Path = r.GetString(2),
            SizeBytes = r.GetInt64(3),
            ModifiedUtc = Time(r.GetInt64(4)),
            AddedUtc = Time(r.GetInt64(5)),
            IsMissing = r.GetInt64(6) != 0,
            Category = ParseEnum(r.GetString(7), MediaCategory.Personal),
            Title = r.GetString(8),
            Year = Int(9),
            Season = Int(10),
            Episode = Int(11),
            EpisodeTitle = Text(12),
            Date = Text(13) is { } date && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            Overview = Text(14),
            Source = ParseEnum(r.GetString(15), MetadataSource.FileName),
            Confidence = ParseEnum(r.GetString(16), MatchConfidence.Uncertain),
            ArtworkPath = Text(17),
            Probed = r.GetInt64(18) != 0,
            Duration = r.IsDBNull(19) ? null : TimeSpan.FromMilliseconds(r.GetInt64(19)),
            Width = Int(20),
            Height = Int(21),
            VideoCodec = Text(22),
            Fingerprint = Text(23),
            SuggestedMatch = matchTitle is null
                ? null
                : new OnlineMatch(
                    Text(24) ?? string.Empty,
                    Text(25) ?? string.Empty,
                    matchTitle,
                    Int(27),
                    Text(28),
                    Text(29),
                    Text(30),
                    ParseEnum(Text(31) ?? string.Empty, MatchConfidence.Uncertain)),
        };
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, sql, cancellationToken, parameters).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset Time(long unixMilliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);

    private static T ParseEnum<T>(string text, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: false, out var value) && Enum.IsDefined(value) ? value : fallback;
}
