using Aperture.Core.Playback;
using Aperture.Core.Series;
using Microsoft.Data.Sqlite;

namespace Aperture.Data;

/// <summary>
/// Segments, corrections, analysis records and per-show preferences in SQLite. Detection
/// results and the user's corrections are kept in separate tables, so analysing a file again
/// can never overwrite what the user set.
/// </summary>
public sealed class SqliteSeriesStore(ApertureDatabase database, TimeProvider? clock = null) : ISeriesStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<EpisodeSegments> GetSegmentsAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var detected = new List<MediaSegment>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT kind, source, start_ms, end_ms, confidence, evidence FROM media_segments WHERE fingerprint = $f;";
            command.Parameters.AddWithValue("$f", fingerprint);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (TryKind(reader.GetString(0), out var kind))
                {
                    detected.Add(new MediaSegment(
                        kind,
                        TimeSpan.FromMilliseconds(reader.GetInt64(2)),
                        TimeSpan.FromMilliseconds(reader.GetInt64(3)),
                        reader.GetDouble(4),
                        Enum.TryParse<SegmentSource>(reader.GetString(1), out var source) ? source : SegmentSource.Detected,
                        reader.IsDBNull(5) ? null : reader.GetString(5)));
                }
            }
        }

        var corrections = new List<SegmentCorrection>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT kind, start_ms, end_ms FROM segment_corrections WHERE fingerprint = $f;";
            command.Parameters.AddWithValue("$f", fingerprint);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (TryKind(reader.GetString(0), out var kind))
                {
                    corrections.Add(new SegmentCorrection(
                        kind,
                        reader.IsDBNull(1) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(1)),
                        reader.IsDBNull(2) ? null : TimeSpan.FromMilliseconds(reader.GetInt64(2))));
                }
            }
        }

        return new EpisodeSegments(detected, corrections);
    }

    public async Task SaveDetectedAsync(AnalysisRecord record, IReadOnlyList<MediaSegment> segments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(segments);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM media_segments WHERE fingerprint = $f;", cancellationToken, ("$f", record.Fingerprint)).ConfigureAwait(false);
        foreach (var segment in segments.Where(s => s.Source != SegmentSource.Manual).GroupBy(s => s.Kind).Select(g => g.MaxBy(s => s.Confidence)!))
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO media_segments (fingerprint, kind, source, start_ms, end_ms, confidence, evidence)
                VALUES ($f, $kind, $source, $start, $end, $confidence, $evidence);
                """,
                cancellationToken,
                ("$f", record.Fingerprint),
                ("$kind", segment.Kind.ToString()),
                ("$source", segment.Source.ToString()),
                ("$start", (long)segment.Start.TotalMilliseconds),
                ("$end", (long)segment.End.TotalMilliseconds),
                ("$confidence", Math.Clamp(segment.Confidence, 0, 1)),
                ("$evidence", segment.Evidence)).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO episode_analysis (fingerprint, detector_version, signature, analyzed_utc, outcome)
            VALUES ($f, $version, $signature, $at, $outcome)
            ON CONFLICT (fingerprint) DO UPDATE SET
                detector_version = excluded.detector_version, signature = excluded.signature,
                analyzed_utc = excluded.analyzed_utc, outcome = excluded.outcome;
            """,
            cancellationToken,
            ("$f", record.Fingerprint),
            ("$version", record.DetectorVersion),
            ("$signature", record.Signature),
            ("$at", record.AnalyzedUtc.ToUnixTimeMilliseconds()),
            ("$outcome", record.Outcome.ToString())).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCorrectionAsync(string fingerprint, SegmentCorrection correction, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(correction);
        if (!correction.IsNone && correction.End - correction.Start < SegmentCorrection.MinimumLength)
        {
            throw new ArgumentException("A corrected segment must end at least a second after it starts.", nameof(correction));
        }

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO segment_corrections (fingerprint, kind, start_ms, end_ms, updated_utc)
            VALUES ($f, $kind, $start, $end, $at)
            ON CONFLICT (fingerprint, kind) DO UPDATE SET
                start_ms = excluded.start_ms, end_ms = excluded.end_ms, updated_utc = excluded.updated_utc;
            """,
            cancellationToken,
            ("$f", fingerprint),
            ("$kind", correction.Kind.ToString()),
            ("$start", correction.IsNone ? null : (long)correction.Start!.Value.TotalMilliseconds),
            ("$end", correction.IsNone ? null : (long)correction.End!.Value.TotalMilliseconds),
            ("$at", _clock.GetUtcNow().ToUnixTimeMilliseconds())).ConfigureAwait(false);
    }

    public async Task ClearCorrectionAsync(string fingerprint, SegmentKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "DELETE FROM segment_corrections WHERE fingerprint = $f AND kind = $kind;", cancellationToken, ("$f", fingerprint), ("$kind", kind.ToString())).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, AnalysisRecord>> GetAnalysesAsync(CancellationToken cancellationToken = default)
    {
        var records = new Dictionary<string, AnalysisRecord>(StringComparer.Ordinal);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint, detector_version, signature, analyzed_utc, outcome FROM episode_analysis;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records[reader.GetString(0)] = new AnalysisRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                Enum.TryParse<AnalysisOutcome>(reader.GetString(4), out var outcome) ? outcome : AnalysisOutcome.Failed);
        }

        return records;
    }

    public async Task<SeriesPreferences> GetPreferencesAsync(string seriesKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesKey);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT autoplay, intro_mode, recap_mode FROM series_preferences WHERE series_key = $key;";
        command.Parameters.AddWithValue("$key", seriesKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return SeriesPreferences.Default;
        }

        return new SeriesPreferences(
            reader.IsDBNull(0) ? null : reader.GetInt64(0) != 0,
            Mode(reader, 1),
            Mode(reader, 2));
    }

    public async Task SavePreferencesAsync(string seriesKey, SeriesPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesKey);
        ArgumentNullException.ThrowIfNull(preferences);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (preferences.IsDefault)
        {
            await ExecuteAsync(connection, null, "DELETE FROM series_preferences WHERE series_key = $key;", cancellationToken, ("$key", seriesKey)).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO series_preferences (series_key, autoplay, intro_mode, recap_mode) VALUES ($key, $autoplay, $intro, $recap)
            ON CONFLICT (series_key) DO UPDATE SET
                autoplay = excluded.autoplay, intro_mode = excluded.intro_mode, recap_mode = excluded.recap_mode;
            """,
            cancellationToken,
            ("$key", seriesKey),
            ("$autoplay", preferences.Autoplay is { } on ? (on ? 1 : 0) : null),
            ("$intro", preferences.Intro?.ToString()),
            ("$recap", preferences.Recap?.ToString())).ConfigureAwait(false);
    }

    public async Task<ShowChoices> GetShowChoicesAsync(string seriesKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesKey);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audio_language, subtitle_language, subtitles_off, speed, clear_dialogue, even_volume
            FROM show_choices WHERE series_key = $key;
            """;
        command.Parameters.AddWithValue("$key", seriesKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ShowChoices.None;
        }

        return new ShowChoices(
            AudioLanguage: reader.IsDBNull(0) ? null : reader.GetString(0),
            SubtitleLanguage: reader.IsDBNull(1) ? null : reader.GetString(1),
            SubtitlesOff: reader.GetInt64(2) != 0,
            Speed: reader.IsDBNull(3) ? null : Math.Clamp(reader.GetDouble(3), 0.25, 4),
            Sound: reader.IsDBNull(4) && reader.IsDBNull(5) ? null : new SoundOptions(!reader.IsDBNull(4) && reader.GetInt64(4) != 0, !reader.IsDBNull(5) && reader.GetInt64(5) != 0));
    }

    public async Task SaveShowChoicesAsync(string seriesKey, ShowChoices choices, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesKey);
        ArgumentNullException.ThrowIfNull(choices);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (choices == ShowChoices.None)
        {
            await ExecuteAsync(connection, null, "DELETE FROM show_choices WHERE series_key = $key;", cancellationToken, ("$key", seriesKey)).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO show_choices (series_key, audio_language, subtitle_language, subtitles_off, speed, clear_dialogue, even_volume)
            VALUES ($key, $audio, $subtitle, $off, $speed, $dialogue, $even)
            ON CONFLICT (series_key) DO UPDATE SET
                audio_language = excluded.audio_language, subtitle_language = excluded.subtitle_language,
                subtitles_off = excluded.subtitles_off, speed = excluded.speed,
                clear_dialogue = excluded.clear_dialogue, even_volume = excluded.even_volume;
            """,
            cancellationToken,
            ("$key", seriesKey),
            ("$audio", choices.AudioLanguage),
            ("$subtitle", choices.SubtitleLanguage),
            ("$off", choices.SubtitlesOff ? 1 : 0),
            ("$speed", choices.Speed),
            ("$dialogue", choices.Sound is { } sound ? (sound.ClearDialogue ? 1 : 0) : null),
            ("$even", choices.Sound is { } chosen ? (chosen.EvenVolume ? 1 : 0) : null)).ConfigureAwait(false);
    }

    private static SkipMode? Mode(SqliteDataReader reader, int column) =>
        !reader.IsDBNull(column) && Enum.TryParse<SkipMode>(reader.GetString(column), out var mode) && Enum.IsDefined(mode) ? mode : null;

    private static bool TryKind(string text, out SegmentKind kind) =>
        Enum.TryParse(text, out kind) && Enum.IsDefined(kind);

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
}
