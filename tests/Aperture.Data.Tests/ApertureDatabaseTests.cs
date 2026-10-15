namespace Aperture.Data.Tests;

public class ApertureDatabaseTests
{
    [Fact]
    public async Task Migrate_creates_the_schema_and_sets_the_version()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        await using var connection = await temp.Database.OpenAsync();

        Assert.Equal(ApertureDatabase.LatestVersion, await ApertureDatabase.GetUserVersionAsync(connection, default));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(["item_state", "library_folders", "library_items", "playback_history", "settings"], tables);
    }

    [Fact]
    public async Task Migrate_twice_is_harmless_and_makes_no_backup_when_nothing_changes()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        await temp.Database.MigrateAsync();

        Assert.Empty(Directory.GetFiles(temp.Folder, "*.bak"));
    }

    [Fact]
    public async Task A_database_from_a_newer_version_is_refused()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        await using (var connection = await temp.Database.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {ApertureDatabase.LatestVersion + 1};";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => temp.Database.MigrateAsync());
    }

    [Fact]
    public async Task A_milestone_1_database_upgrades_keeping_history_and_settings_with_a_backup()
    {
        await using var temp = new TempDatabase();
        await using (var connection = await temp.Database.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            // The schema exactly as Milestone 1 shipped it.
            command.CommandText = """
                CREATE TABLE settings (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE playback_history (path TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, title TEXT NOT NULL,
                    position_ms INTEGER NOT NULL, duration_ms INTEGER NOT NULL, last_played_utc INTEGER NOT NULL);
                CREATE INDEX ix_playback_history_last_played ON playback_history (last_played_utc DESC);
                INSERT INTO settings VALUES ('playback.resume', 'false');
                INSERT INTO playback_history VALUES ('D:\Films\a.mkv', 'a', 60000, 600000, 1700000000000);
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await temp.Database.MigrateAsync();

        Assert.Single(Directory.GetFiles(temp.Folder, "aperture.db.v1.bak"));
        var history = await new SqlitePlaybackHistory(temp.Database).GetAllAsync();
        Assert.Equal(TimeSpan.FromMinutes(1), Assert.Single(history).Position);
        var settings = await new SqliteSettingsStore(temp.Database).LoadAsync();
        Assert.Equal(Aperture.Core.Playback.ResumeMode.StartOver, settings.Resume);
        await using var check = await temp.Database.OpenAsync();
        Assert.Equal(ApertureDatabase.LatestVersion, await ApertureDatabase.GetUserVersionAsync(check, default));
    }

    [Fact]
    public void Latest_version_counts_the_embedded_migrations() =>
        Assert.True(ApertureDatabase.LatestVersion >= 1);
}
