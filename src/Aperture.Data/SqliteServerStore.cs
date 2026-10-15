using System.Globalization;
using Aperture.Core.Server;
using Microsoft.Data.Sqlite;

namespace Aperture.Data;

/// <summary>
/// The media server's settings (rows of <c>settings</c> under "server."), its shared folders and
/// signed-in devices. Used by the desktop app and the server process, each with its own connections.
/// </summary>
public sealed class SqliteServerStore(ApertureDatabase database) : IServerStore
{
    private const string PortKey = "server.port";
    private const string SetupPortKey = "server.setupPort";
    private const string AllowVpnKey = "server.allowVpn";
    private const string KeepAwakeKey = "server.keepAwake";
    private const string FfmpegKey = "server.ffmpegPath";
    private const string ConversionsKey = "server.maxConversions";
    private const string StartWithWindowsKey = "server.startWithWindows";
    private const string PassphraseKey = "server.passphraseHash";

    public async Task<ServerSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var values = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var d = ServerSettings.Default;
        return new ServerSettings(
            Port: ReadInt(values, PortKey, d.Port),
            SetupPort: ReadInt(values, SetupPortKey, d.SetupPort),
            AllowVpn: ReadBool(values, AllowVpnKey, d.AllowVpn),
            KeepAwake: ReadBool(values, KeepAwakeKey, d.KeepAwake),
            FfmpegPath: values.GetValueOrDefault(FfmpegKey),
            MaxConversions: ReadInt(values, ConversionsKey, d.MaxConversions),
            StartWithWindows: ReadBool(values, StartWithWindowsKey, d.StartWithWindows)).Normalized();
    }

    public async Task SaveSettingsAsync(ServerSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalized();
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetAsync(connection, transaction, PortKey, Int(settings.Port), cancellationToken).ConfigureAwait(false);
        await SetAsync(connection, transaction, SetupPortKey, Int(settings.SetupPort), cancellationToken).ConfigureAwait(false);
        await SetAsync(connection, transaction, AllowVpnKey, Bool(settings.AllowVpn), cancellationToken).ConfigureAwait(false);
        await SetAsync(connection, transaction, KeepAwakeKey, Bool(settings.KeepAwake), cancellationToken).ConfigureAwait(false);
        await SetAsync(connection, transaction, FfmpegKey, settings.FfmpegPath, cancellationToken).ConfigureAwait(false);
        await SetAsync(connection, transaction, ConversionsKey, Int(settings.MaxConversions), cancellationToken).ConfigureAwait(false);
        await SetAsync(connection, transaction, StartWithWindowsKey, Bool(settings.StartWithWindows), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetPassphraseHashAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", PassphraseKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task SetPassphraseHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetAsync(connection, transaction, PassphraseKey, hash, cancellationToken).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM server_devices;";
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<long>> GetSharedFolderIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder_id FROM server_shared_folders;";
        var ids = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    public async Task SetFolderSharedAsync(long folderId, bool isShared, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = isShared
            ? "INSERT INTO server_shared_folders (folder_id) SELECT id FROM library_folders WHERE id = $id ON CONFLICT DO NOTHING;"
            : "DELETE FROM server_shared_folders WHERE folder_id = $id;";
        command.Parameters.AddWithValue("$id", folderId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_utc, last_seen_utc, expires_utc, last_address FROM server_devices ORDER BY last_seen_utc DESC;";
        var devices = new List<RemoteDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            devices.Add(ReadDevice(reader));
        }

        return devices;
    }

    public async Task<RemoteDevice> AddDeviceAsync(string tokenHash, string name, DateTimeOffset nowUtc, DateTimeOffset expiresUtc, string? address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM server_devices WHERE expires_utc <= $now;
            INSERT INTO server_devices (token_hash, name, created_utc, last_seen_utc, expires_utc, last_address)
            VALUES ($hash, $name, $now, $now, $expires, $address)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", expiresUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new RemoteDevice(id, name, Trim(nowUtc), Trim(nowUtc), Trim(expiresUtc), address);
    }

    public async Task<RemoteDevice?> FindDeviceAsync(string tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_utc, last_seen_utc, expires_utc, last_address FROM server_devices WHERE token_hash = $hash AND expires_utc > $now;";
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDevice(reader) : null;
    }

    public async Task TouchDeviceAsync(long deviceId, DateTimeOffset nowUtc, DateTimeOffset expiresUtc, string? address, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE server_devices SET last_seen_utc = $now, expires_utc = $expires, last_address = $address WHERE id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", expiresUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveDeviceAsync(long deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM server_devices WHERE id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAllDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM server_devices;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings WHERE substr(key, 1, 7) = 'server.';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static async Task SetAsync(SqliteConnection connection, SqliteTransaction transaction, string key, string? value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = value is null
            ? "DELETE FROM settings WHERE key = $key;"
            : "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        if (value is not null)
        {
            command.Parameters.AddWithValue("$value", value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RemoteDevice ReadDevice(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
        r.IsDBNull(5) ? null : r.GetString(5));

    private static DateTimeOffset Trim(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
