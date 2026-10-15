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

        Assert.Equal(["playback_history", "settings"], tables);
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
    public void Latest_version_counts_the_embedded_migrations() =>
        Assert.True(ApertureDatabase.LatestVersion >= 1);
}
