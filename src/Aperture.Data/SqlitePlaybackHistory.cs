using Aperture.Core.Library;
using Microsoft.Data.Sqlite;

namespace Aperture.Data;

/// <summary>Playback history stored in the <c>playback_history</c> table.</summary>
public sealed class SqlitePlaybackHistory(ApertureDatabase database) : IPlaybackHistory
{
    private const string SelectColumns = "SELECT path, title, position_ms, duration_ms, last_played_utc FROM playback_history";

    public async Task<IReadOnlyList<HistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY last_played_utc DESC;";

        var entries = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(Read(reader));
        }

        return entries;
    }

    public async Task<HistoryEntry?> FindAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task SaveAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO playback_history (path, title, position_ms, duration_ms, last_played_utc)
            VALUES ($path, $title, $position, $duration, $lastPlayed)
            ON CONFLICT (path) DO UPDATE SET
                title = excluded.title,
                position_ms = excluded.position_ms,
                duration_ms = excluded.duration_ms,
                last_played_utc = excluded.last_played_utc;
            """;
        command.Parameters.AddWithValue("$path", entry.Path);
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$position", (long)entry.Position.TotalMilliseconds);
        command.Parameters.AddWithValue("$duration", (long)entry.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$lastPlayed", entry.LastPlayedUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_history WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_history;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HistoryEntry Read(SqliteDataReader reader) => new(
        Path: reader.GetString(0),
        Title: reader.GetString(1),
        Position: TimeSpan.FromMilliseconds(reader.GetInt64(2)),
        Duration: TimeSpan.FromMilliseconds(reader.GetInt64(3)),
        LastPlayedUtc: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));
}
