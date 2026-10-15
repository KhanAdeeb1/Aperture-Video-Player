using Aperture.Core.YouTube;
using Microsoft.Data.Sqlite;

namespace Aperture.Data;

/// <summary>YouTube videos watched in Aperture or saved for later, in the <c>youtube_videos</c> table.</summary>
public sealed class SqliteYouTubeStore(ApertureDatabase database) : IYouTubeStore
{
    private const string SelectColumns =
        "SELECT video_id, position_ms, duration_ms, watched_utc, saved_utc, title, channel, thumbnail_url, length_ms, details_utc FROM youtube_videos";

    public async Task<IReadOnlyList<YouTubeEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + ";";
        var entries = new List<YouTubeEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(Read(reader));
        }

        return entries;
    }

    public async Task<YouTubeEntry?> GetAsync(string videoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE video_id = $id;";
        command.Parameters.AddWithValue("$id", videoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task RecordProgressAsync(string videoId, TimeSpan position, TimeSpan duration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        await ExecuteAsync(
            """
            INSERT INTO youtube_videos (video_id, position_ms, duration_ms, watched_utc)
            VALUES ($id, $position, $duration, $now)
            ON CONFLICT (video_id) DO UPDATE SET
                position_ms = excluded.position_ms,
                duration_ms = CASE WHEN excluded.duration_ms > 0 THEN excluded.duration_ms ELSE youtube_videos.duration_ms END,
                watched_utc = excluded.watched_utc;
            """,
            cancellationToken,
            ("$id", videoId),
            ("$position", Milliseconds(position)),
            ("$duration", Milliseconds(duration)),
            ("$now", nowUtc.ToUnixTimeMilliseconds())).ConfigureAwait(false);
    }

    public async Task SetDetailsAsync(YouTubeVideo video, DateTimeOffset fetchedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        // Only updates existing rows: details are never stored for videos Aperture has no record of.
        await ExecuteAsync(
            """
            UPDATE youtube_videos SET title = $title, channel = $channel, thumbnail_url = $thumbnail,
                length_ms = $length, details_utc = $fetched
            WHERE video_id = $id;
            """,
            cancellationToken,
            ("$id", video.Id),
            ("$title", video.Title),
            ("$channel", video.Channel),
            ("$thumbnail", (object?)video.ThumbnailUrl ?? DBNull.Value),
            ("$length", video.Duration is { } length ? Milliseconds(length) : DBNull.Value),
            ("$fetched", fetchedUtc.ToUnixTimeMilliseconds())).ConfigureAwait(false);
    }

    public async Task ClearDetailsAsync(IEnumerable<string> videoIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoIds);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in videoIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE youtube_videos SET title = NULL, channel = NULL, thumbnail_url = NULL, length_ms = NULL, details_utc = NULL WHERE video_id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSavedAsync(string videoId, DateTimeOffset? savedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        if (savedUtc is { } saved)
        {
            await ExecuteAsync(
                """
                INSERT INTO youtube_videos (video_id, saved_utc) VALUES ($id, $saved)
                ON CONFLICT (video_id) DO UPDATE SET saved_utc = excluded.saved_utc;
                """,
                cancellationToken,
                ("$id", videoId),
                ("$saved", saved.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(
            "UPDATE youtube_videos SET saved_utc = NULL WHERE video_id = $id; DELETE FROM youtube_videos WHERE video_id = $id AND watched_utc IS NULL;",
            cancellationToken,
            ("$id", videoId)).ConfigureAwait(false);
    }

    public Task ForgetProgressAsync(string videoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        return ExecuteAsync(
            "UPDATE youtube_videos SET position_ms = 0, watched_utc = NULL WHERE video_id = $id; DELETE FROM youtube_videos WHERE video_id = $id AND saved_utc IS NULL;",
            cancellationToken,
            ("$id", videoId));
    }

    public Task ClearAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM youtube_videos;", cancellationToken);

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long Milliseconds(TimeSpan value) => (long)Math.Max(0, value.TotalMilliseconds);

    private static YouTubeEntry Read(SqliteDataReader reader)
    {
        DateTimeOffset? Time(int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));
        var id = reader.GetString(0);
        var details = Time(9);
        YouTubeVideo? video = details is null || reader.IsDBNull(5)
            ? null
            : new YouTubeVideo(
                id,
                reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(8)));
        return new YouTubeEntry(
            id,
            video,
            TimeSpan.FromMilliseconds(reader.GetInt64(1)),
            TimeSpan.FromMilliseconds(reader.GetInt64(2)),
            Time(3),
            Time(4),
            details);
    }
}
