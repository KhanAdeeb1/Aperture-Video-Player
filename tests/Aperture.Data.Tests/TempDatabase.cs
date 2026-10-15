using Microsoft.Data.Sqlite;

namespace Aperture.Data.Tests;

/// <summary>A real SQLite file in a fresh temporary folder, deleted afterwards.</summary>
internal sealed class TempDatabase : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aperture-tests-" + Guid.NewGuid().ToString("N"));

    public TempDatabase()
    {
        Directory.CreateDirectory(_folder);
        Database = new ApertureDatabase(Path.Combine(_folder, "aperture.db"));
    }

    public ApertureDatabase Database { get; }

    public string Folder => _folder;

    public static async Task<TempDatabase> CreateMigratedAsync()
    {
        var temp = new TempDatabase();
        await temp.Database.MigrateAsync();
        return temp;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
        return ValueTask.CompletedTask;
    }
}
