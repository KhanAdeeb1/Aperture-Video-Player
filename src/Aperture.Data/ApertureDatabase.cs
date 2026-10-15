using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Aperture.Data;

/// <summary>
/// Owns the location of Aperture's SQLite database and brings its schema up to date.
/// Connections are short-lived: each operation opens one, which keeps the file free
/// for backups and avoids sharing a connection across threads.
/// </summary>
public sealed class ApertureDatabase
{
    private const string MigrationPrefix = "Aperture.Data.Migrations.";

    private readonly string _connectionString;

    public ApertureDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Full path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>The schema version this build expects.</summary>
    public static int LatestVersion => LoadMigrations().Count;

    /// <summary>Opens a new connection. The caller disposes it.</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Applies any migrations newer than the file's <c>user_version</c>, each in its own
    /// transaction. When an existing database needs migrating, a copy is kept next to it
    /// first (<c>aperture.db.v{n}.bak</c>).
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var migrations = LoadMigrations();
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existed = File.Exists(DatabasePath);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (current > migrations.Count)
        {
            throw new InvalidOperationException(
                $"The database at '{DatabasePath}' has schema version {current}, which is newer than this version of Aperture supports ({migrations.Count}).");
        }

        if (current == migrations.Count)
        {
            return;
        }

        if (existed && current > 0)
        {
            await BackupAsync(connection, $"{DatabasePath}.v{current}.bak", cancellationToken).ConfigureAwait(false);
        }

        for (var version = current + 1; version <= migrations.Count; version++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migrations[version - 1] + $"\nPRAGMA user_version = {version};";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task BackupAsync(SqliteConnection source, string backupPath, CancellationToken cancellationToken)
    {
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false };
        await using var destination = new SqliteConnection(builder.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static List<string> LoadMigrations()
    {
        var assembly = typeof(ApertureDatabase).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        var scripts = new List<string>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var expectedPrefix = $"{MigrationPrefix}{i + 1:0000}_";
            if (!names[i].StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Migration '{names[i]}' is out of sequence; expected a name starting with '{expectedPrefix}'.");
            }

            scripts.Add(ReadResource(assembly, names[i]));
        }

        return scripts;
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded migration '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
