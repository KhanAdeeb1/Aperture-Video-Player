namespace Aperture.Data.Tests;

/// <summary>
/// An upgrade from any earlier release ends with the same schema a new install gets, and keeps
/// what was stored. Every schema version Aperture has shipped is started from.
/// </summary>
public class UpgradePathTests
{
    public static TheoryData<int> EarlierVersions()
    {
        var versions = new TheoryData<int>();
        for (var version = 1; version < ApertureDatabase.LatestVersion; version++)
        {
            versions.Add(version);
        }

        return versions;
    }

    [Theory]
    [MemberData(nameof(EarlierVersions))]
    public async Task Upgrading_from_an_earlier_version_matches_a_new_install_and_keeps_history(int version)
    {
        await using var old = new TempDatabase();
        await using (var connection = await old.Database.OpenAsync())
        {
            var migrations = ApertureDatabase.LoadMigrations();
            for (var step = 1; step <= version; step++)
            {
                await ExecuteAsync(connection, migrations[step - 1] + $"\nPRAGMA user_version = {step};");
            }

            await ExecuteAsync(connection, @"INSERT INTO playback_history (path, title, position_ms, duration_ms, last_played_utc)
                VALUES ('D:\Films\Harbour.mkv', 'Harbour', 754000, 5400000, 1760000000000);");
        }

        await old.Database.MigrateAsync();

        await using var fresh = await TempDatabase.CreateMigratedAsync();
        Assert.Equal(await SchemaAsync(fresh.Database), await SchemaAsync(old.Database));
        Assert.Single(Directory.GetFiles(old.Folder, $"aperture.db.v{version}.bak"));
        var entry = Assert.Single(await new SqlitePlaybackHistory(old.Database).GetAllAsync());
        Assert.Equal(TimeSpan.FromMilliseconds(754000), entry.Position);
    }

    private static async Task ExecuteAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // Tables, columns (name, type, nullability, default, key) and indexes, in a stable order.
    private static async Task<List<string>> SchemaAsync(ApertureDatabase database)
    {
        await using var connection = await database.OpenAsync();
        var schema = new List<string>();
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type, name, tbl_name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schema.Add($"{reader.GetString(0)} {reader.GetString(1)} on {reader.GetString(2)}");
                if (reader.GetString(0) == "table")
                {
                    tables.Add(reader.GetString(1));
                }
            }
        }

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}') ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schema.Add($"{table}.{reader.GetString(0)} {reader.GetString(1)} notnull={reader.GetInt32(2)} default={(reader.IsDBNull(3) ? "none" : reader.GetString(3))} pk={reader.GetInt32(4)}");
            }
        }

        return schema;
    }
}
