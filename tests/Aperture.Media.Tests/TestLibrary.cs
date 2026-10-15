using Aperture.Core.Library;
using Aperture.Data;
using Microsoft.Data.Sqlite;

namespace Aperture.Media.Tests;

/// <summary>A temporary media folder and a real, migrated library database, deleted afterwards.</summary>
internal sealed class TestLibrary : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aperture-media-tests-" + Guid.NewGuid().ToString("N"));

    private TestLibrary()
    {
        Media = Path.Combine(_root, "media");
        Data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Media);
        Directory.CreateDirectory(Data);
        Database = new ApertureDatabase(Path.Combine(Data, "aperture.db"));
        Store = new SqliteLibraryStore(Database);
    }

    public string Media { get; }

    public string Data { get; }

    public ApertureDatabase Database { get; }

    public SqliteLibraryStore Store { get; }

    public List<string> Log { get; } = [];

    public static async Task<TestLibrary> CreateAsync()
    {
        var library = new TestLibrary();
        await library.Database.MigrateAsync();
        return library;
    }

    public LibraryScanner Scanner(IMediaProbe? probe = null) =>
        new(Store, probe, TimeProvider.System, (level, message) => Log.Add($"{level}: {message}"));

    /// <summary>Writes a small file with distinct content under the media folder.</summary>
    public string AddFile(string relative, int size = 4096, byte seed = 1)
    {
        var path = Path.Combine(Media, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + (i * 31));
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Reports progress synchronously, so a test can react at an exact point.</summary>
internal sealed class InlineProgress(Action<ScanProgress> onReport) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => onReport(value);
}
