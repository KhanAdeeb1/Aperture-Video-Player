using Aperture.Core.Library;
using Aperture.Core.Playback;
using Microsoft.Data.Sqlite;

namespace Aperture.Data;

/// <summary>Per-file watched marks and playback choices in the <c>item_state</c> table.</summary>
public sealed class SqliteItemStateStore(ApertureDatabase database) : IItemStateStore
{
    public async Task<PlaybackPreferences> GetPreferencesAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audio_track, subtitle_track, subtitles_off, subtitle_delay_ms, audio_delay_ms, aspect, speed
            FROM item_state WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return PlaybackPreferences.None;
        }

        return new PlaybackPreferences(
            AudioTrackId: reader.IsDBNull(0) ? null : reader.GetInt64(0),
            SubtitleTrackId: reader.IsDBNull(1) ? null : reader.GetInt64(1),
            SubtitlesOff: reader.GetInt64(2) != 0,
            SubtitleDelay: SyncOffset.Normalize(TimeSpan.FromMilliseconds(reader.GetInt64(3))),
            AudioDelay: SyncOffset.Normalize(TimeSpan.FromMilliseconds(reader.GetInt64(4))),
            Aspect: !reader.IsDBNull(5) && Enum.TryParse<AspectMode>(reader.GetString(5), out var aspect) && Enum.IsDefined(aspect) ? aspect : null,
            Speed: reader.IsDBNull(6) ? null : Math.Clamp(reader.GetDouble(6), 0.25, 4));
    }

    public async Task SavePreferencesAsync(string path, PlaybackPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(preferences);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO item_state (path, audio_track, subtitle_track, subtitles_off, subtitle_delay_ms, audio_delay_ms, aspect, speed)
            VALUES ($path, $audio, $subtitle, $off, $subDelay, $audioDelay, $aspect, $speed)
            ON CONFLICT (path) DO UPDATE SET
                audio_track = excluded.audio_track,
                subtitle_track = excluded.subtitle_track,
                subtitles_off = excluded.subtitles_off,
                subtitle_delay_ms = excluded.subtitle_delay_ms,
                audio_delay_ms = excluded.audio_delay_ms,
                aspect = excluded.aspect,
                speed = excluded.speed;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$audio", (object?)preferences.AudioTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subtitle", (object?)preferences.SubtitleTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("$off", preferences.SubtitlesOff ? 1 : 0);
        command.Parameters.AddWithValue("$subDelay", (long)SyncOffset.Normalize(preferences.SubtitleDelay).TotalMilliseconds);
        command.Parameters.AddWithValue("$audioDelay", (long)SyncOffset.Normalize(preferences.AudioDelay).TotalMilliseconds);
        command.Parameters.AddWithValue("$aspect", (object?)preferences.Aspect?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$speed", (object?)preferences.Speed ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetWatchedOverridesAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, watched FROM item_state WHERE watched IS NOT NULL;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetInt64(1) != 0;
        }

        return result;
    }

    public async Task SetWatchedAsync(string path, bool? watched, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO item_state (path, watched) VALUES ($path, $watched)
            ON CONFLICT (path) DO UPDATE SET watched = excluded.watched;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$watched", watched is { } w ? (w ? 1 : 0) : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
