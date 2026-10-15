using System.Globalization;
using Aperture.Core.Settings;

namespace Aperture.Data;

/// <summary>Settings stored as key/value rows in the <c>settings</c> table.</summary>
public sealed class SqliteSettingsStore(ApertureDatabase database) : ISettingsStore
{
    private const string ResumePlaybackKey = "playback.resume";
    private const string RememberHistoryKey = "history.remember";
    private const string HideDelayKey = "playback.controlsHideDelaySeconds";

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM settings;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var defaults = AppSettings.Default;
        return new AppSettings(
            ResumePlayback: ReadBool(values, ResumePlaybackKey, defaults.ResumePlayback),
            RememberHistory: ReadBool(values, RememberHistoryKey, defaults.RememberHistory),
            ControlsHideDelaySeconds: ReadInt(values, HideDelayKey, defaults.ControlsHideDelaySeconds)).Normalized();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalized();

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in new[]
        {
            (ResumePlaybackKey, settings.ResumePlayback ? "true" : "false"),
            (RememberHistoryKey, settings.RememberHistory ? "true" : "false"),
            (HideDelayKey, settings.ControlsHideDelaySeconds.ToString(CultureInfo.InvariantCulture)),
        })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
